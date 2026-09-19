// Velox — botão flutuante "Baixar vídeo" sobre elementos <video> (estilo IDM / JDownloader).
// Roda em todas as páginas; só faz algo quando existe um <video> grande o bastante.
(() => {
  if (window.__veloxVideoButton) return;
  window.__veloxVideoButton = true;

  const MIN_W = 160, MIN_H = 90;
  let enabled = true;
  let host, root, pill, label, panel;
  let current = null;        // <video> sob o ponteiro
  let hideTimer = null;
  let raf = null;
  let candidates = [];       // mídias disponíveis para o vídeo atual
  let panelOpen = false;

  // rastro de diagnóstico (chrome.storage.local.videoTrace)
  let dbgChain = Promise.resolve();
  function dbg(...parts) {
    const line = new Date().toISOString().slice(11, 19) + ' ' + parts.map(x => typeof x === 'string' ? x : JSON.stringify(x)).join(' ');
    dbgChain = dbgChain.then(async () => {
      try { const { videoTrace: old = [] } = await chrome.storage.local.get('videoTrace'); await chrome.storage.local.set({ videoTrace: [...old.slice(-19), line] }); } catch { }
    });
  }

  chrome.storage?.local?.get({ videoButton: true }, s => { enabled = s.videoButton !== false; });
  chrome.storage?.onChanged?.addListener(ch => { if (ch.videoButton) { enabled = ch.videoButton.newValue !== false; if (!enabled) hide(true); } });

  // ------------------------------------------------------------ UI (Shadow DOM: não sofre com o CSS da página)
  function build() {
    host = document.createElement('velox-video-button');
    host.style.cssText = 'position:fixed;top:0;left:0;width:100vw;height:100vh;z-index:2147483646;pointer-events:none;overflow:visible;';
    root = host.attachShadow({ mode: 'open' });
    root.innerHTML = `
      <style>
        :host { all: initial; }
        .pill {
          position: absolute; display: flex; align-items: center; gap: 8px; height: 30px; padding: 0 10px 0 4px;
          border-radius: 15px; background: rgba(12, 14, 22, .82); color: #EDEFF7; border: 1px solid rgba(255,255,255,.12);
          box-shadow: 0 6px 20px rgba(0,0,0,.45); backdrop-filter: blur(8px); cursor: pointer; pointer-events: auto;
          font: 600 12.5px "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; opacity: 0; transform: translateY(-4px);
          transition: opacity .18s ease, transform .18s ease, background .15s; user-select: none; white-space: nowrap;
        }
        .pill.show { opacity: 1; transform: translateY(0); }
        .pill:hover { background: rgba(20, 24, 38, .95); border-color: rgba(124,108,255,.6); }
        .logo { width: 22px; height: 22px; border-radius: 7px; background: linear-gradient(135deg,#7C6CFF,#22D3EE); display:grid; place-items:center; flex:none; }
        .logo svg { width: 13px; height: 13px; fill: #fff; }
        .txt { max-width: 0; overflow: hidden; opacity: 0; transition: max-width .22s ease, opacity .18s ease; }
        .pill:hover .txt, .pill.busy .txt, .pill.done .txt { max-width: 160px; opacity: 1; }
        .pill.done { border-color: rgba(52,211,153,.7); }
        .pill.done .logo { background: #34D399; }
        .pill.err { border-color: rgba(248,113,113,.7); }
        .pill.err .logo { background: #F87171; }
        .panel {
          position: absolute; min-width: 260px; max-width: 380px; padding: 8px; border-radius: 12px; background: rgba(12,14,22,.96);
          border: 1px solid rgba(255,255,255,.12); box-shadow: 0 12px 32px rgba(0,0,0,.55); backdrop-filter: blur(10px); pointer-events: auto;
          font: 12.5px "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; color: #EDEFF7; display: none;
        }
        .panel.show { display: block; }
        .panel h4 { margin: 4px 8px 8px; font-size: 10.5px; font-weight: 700; letter-spacing: .04em; color: #8C93AB; }
        .item { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border-radius: 8px; cursor: pointer; }
        .item:hover { background: rgba(124,108,255,.18); }
        .item b { font-weight: 600; font-size: 12.5px; }
        .item small { color: #8C93AB; font-size: 11px; display: block; margin-top: 2px; max-width: 300px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .kind { flex: none; padding: 2px 7px; border-radius: 6px; background: rgba(34,211,238,.15); color: #22D3EE; font-size: 10.5px; font-weight: 700; }
        .empty { padding: 10px; color: #8C93AB; font-size: 12px; line-height: 1.5; }
      </style>
      <div class="pill" part="pill" title="Baixar este vídeo com o Velox">
        <span class="logo"><svg viewBox="0 0 24 24"><path d="M11 3h2v9.2l3.6-3.6 1.4 1.4-6 6-6-6 1.4-1.4L11 12.2V3zM5 19h14v2H5z"/></svg></span>
        <span class="txt">Baixar com Velox</span>
      </div>
      <div class="panel" part="panel"></div>`;
    pill = root.querySelector('.pill');
    label = root.querySelector('.txt');
    panel = root.querySelector('.panel');

    pill.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    pill.addEventListener('mouseleave', scheduleHide);
    panel.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    panel.addEventListener('mouseleave', scheduleHide);
    pill.addEventListener('click', onPillClick);
    (document.body || document.documentElement).appendChild(host);
  }

  // ------------------------------------------------------------ detecção do vídeo sob o ponteiro
  function videos() {
    return [...document.querySelectorAll('video')].filter(v => {
      const r = v.getBoundingClientRect();
      return r.width >= MIN_W && r.height >= MIN_H && r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
    });
  }

  let lastMove = 0;
  document.addEventListener('mousemove', e => {
    if (!enabled) return;
    const now = performance.now();
    if (now - lastMove < 60) return;
    lastMove = now;

    const x = e.clientX, y = e.clientY;
    let hit = null;
    for (const v of videos()) {
      const r = v.getBoundingClientRect();
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) { hit = v; break; }
    }
    if (hit) { show(hit); }
    else if (current && !panelOpen) scheduleHide();
  }, true);

  function show(video) {
    if (!host) build();
    clearTimeout(hideTimer);
    if (current !== video) {
      current = video;
      panelOpen = false;
      panel.classList.remove('show');
      pill.className = 'pill';
      label.textContent = 'Baixar com Velox';
    }
    pill.classList.add('show');
    position();
    if (!raf) raf = requestAnimationFrame(tick);
  }

  function scheduleHide() {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => hide(false), 350);
  }

  function hide(force) {
    if (!pill) return;
    if (panelOpen && !force) return;
    pill.classList.remove('show');
    panel.classList.remove('show');
    panelOpen = false;
    current = null;
    if (raf) { cancelAnimationFrame(raf); raf = null; }
  }

  function tick() {
    raf = null;
    if (!current || !current.isConnected) { hide(true); return; }
    position();
    raf = requestAnimationFrame(tick);
  }

  function position() {
    const r = current.getBoundingClientRect();
    const top = Math.max(6, r.top + 10);
    const right = Math.max(6, innerWidth - r.right + 10);
    pill.style.top = top + 'px';
    pill.style.right = right + 'px';
    pill.style.left = 'auto';
    panel.style.top = (top + 38) + 'px';
    panel.style.right = right + 'px';
    panel.style.left = 'auto';
  }

  // ------------------------------------------------------------ fontes de mídia
  function extOf(url) {
    try { const m = /\.([a-z0-9]{2,5})(?:$|[?#])/i.exec(new URL(url).pathname); return m ? m[1].toLowerCase() : ''; } catch { return ''; }
  }

  function directSources(video) {
    const list = [];
    const push = (u, type) => { if (u && /^https?:/i.test(u) && !list.some(x => x.url === u)) list.push({ url: u, type: type || '', size: -1, origin: 'src' }); };
    push(video.currentSrc, video.getAttribute('type'));
    push(video.getAttribute('src'));
    for (const s of video.querySelectorAll('source')) push(s.src || s.getAttribute('src'), s.type);
    return list;
  }

  async function gather(video) {
    const direct = directSources(video);
    let sniffed = [];
    try { sniffed = (await chrome.runtime.sendMessage({ type: 'getMedia' }))?.media || []; } catch (err) { dbg('getMedia falhou', String(err)); }
    dbg('sniffed', sniffed.length, sniffed.map(x => [x.url.slice(-30), x.size]), 'direct', direct.map(d => d.url.slice(-30)));
    // completa as fontes diretas com o que o sniffer sabe (tipo real, tamanho)
    for (const d of direct) {
      const known = sniffed.find(x => x.url === d.url || x.url === d.url.split('#')[0]);
      if (known) { if (!d.type) d.type = known.type; if (known.size > 0) d.size = known.size; }
    }
    const all = [...direct, ...sniffed.filter(x => !direct.some(d => d.url === x.url))];
    return all
      .filter(m => !/\.(m3u8|mpd)(?:$|[?#])/i.test(m.url)) // streams segmentados ficam de fora por enquanto
      .sort((a, b) => (b.size > 0 ? 1 : 0) - (a.size > 0 ? 1 : 0));
  }

  function fmtSize(n) {
    if (!(n > 0)) return '';
    const u = ['B', 'KB', 'MB', 'GB']; let i = 0; let v = n;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return (i ? v.toFixed(1) : v) + ' ' + u[i];
  }

  function fmtTime(sec) {
    sec = Math.round(sec);
    const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s2 = sec % 60;
    return (h ? h + ':' + String(m).padStart(2, '0') : String(m)) + ':' + String(s2).padStart(2, '0');
  }

  function kindOf(m) {
    const t = (m.type || '').split(';')[0].trim();
    if (t) return t.replace('video/', '').replace('audio/', '').toUpperCase().slice(0, 6) || 'MÍDIA';
    return (extOf(m.url) || 'MÍDIA').toUpperCase();
  }

  // ------------------------------------------------------------ clique
  async function onPillClick(e) {
    e.preventDefault(); e.stopPropagation();
    if (!current) return;

    if (panelOpen) { panel.classList.remove('show'); panelOpen = false; return; }

    pill.classList.add('busy'); label.textContent = 'Procurando…';
    let media = [];
    try { media = await gather(current); } catch (err) { dbg('gather falhou', String(err)); }
    dbg('mídias encontradas', media.length, media.map(m => m.url.slice(-40)));
    pill.classList.remove('busy');

    if (media.length === 0) {
      label.textContent = 'Nenhuma mídia direta';
      pill.classList.add('err');
      showPanel(`<div class="empty">Este player usa um stream segmentado (HLS/DASH) ou protegido, sem arquivo direto para baixar.<br>Dica: reproduza alguns segundos e tente de novo, ou use o botão direito → "Baixar mídia com Velox".</div>`);
      setTimeout(() => { pill.classList.remove('err'); label.textContent = 'Baixar com Velox'; }, 2500);
      return;
    }

    if (media.length === 1) { send(media[0]); return; }

    // várias opções (qualidades / áudio separado): mostra o painel
    label.textContent = 'Escolha o arquivo';
    const meta = [current.videoWidth && current.videoHeight ? `${current.videoWidth}×${current.videoHeight}` : '',
                  isFinite(current.duration) && current.duration > 0 ? fmtTime(current.duration) : ''].filter(Boolean).join(' · ');
    panel.innerHTML = '<h4>ESCOLHA O ARQUIVO' + (meta ? ' · ' + meta : '') + '</h4>' + media.map((m, i) => `
      <div class="item" data-i="${i}">
        <span class="kind">${kindOf(m)}</span>
        <div><b>${fmtSize(m.size) || 'tamanho desconhecido'}</b><small>${m.url.replace(/^https?:\/\//, '')}</small></div>
      </div>`).join('');
    panel.querySelectorAll('.item').forEach(el => el.addEventListener('click', () => { send(media[+el.dataset.i]); }));
    showPanel();
  }

  function showPanel(html) {
    if (html !== undefined) panel.innerHTML = html;
    panel.classList.add('show');
    panelOpen = true;
    position();
  }

  async function send(m) {
    panel.classList.remove('show'); panelOpen = false;
    pill.classList.add('busy'); label.textContent = 'Enviando…';
    let res;
    try {
      res = await chrome.runtime.sendMessage({
        type: 'sendVideo', url: m.url, mime: m.type || '', size: m.size ?? -1,
        pageUrl: location.href, title: document.title || ''
      });
    } catch (err) { res = { ok: false, error: String(err) }; }
    dbg('sendVideo →', res);
    pill.classList.remove('busy');
    if (res?.ok) {
      pill.classList.add('done'); label.textContent = 'Enviado ao Velox ✓';
    } else {
      pill.classList.add('err'); label.textContent = res?.error?.slice(0, 40) || 'Falhou';
    }
    setTimeout(() => { pill.classList.remove('done', 'err'); label.textContent = 'Baixar com Velox'; scheduleHide(); }, 2200);
  }

  // esconde ao rolar/trocar de aba para não ficar "flutuando" sem vídeo
  window.addEventListener('blur', () => hide(true));
  document.addEventListener('fullscreenchange', () => hide(true));
})();
