// Velox Download Manager — service worker (Manifest V3)
// Intercepta downloads do navegador e entrega ao Velox via Native Messaging.

const HOST = 'com.velox.dm';

const DEFAULTS = {
  enabled: true,        // capturar downloads automaticamente
  minSizeKb: 0,         // ignorar arquivos menores que X KB (0 = capturar tudo)
  ignoreTypes: '',      // extensões ignoradas, separadas por vírgula (ex.: "torrent, pdf")
  notify: true,         // notificação ao capturar
  videoButton: true     // botão flutuante "Baixar com Velox" sobre vídeos
};

let settings = { ...DEFAULTS };
const inFlight = new Set();

// rastro dos últimos eventos (diagnóstico: visível em chrome.storage.local.trace)
let traceChain = Promise.resolve();
function trace(...parts) {
  const line = new Date().toISOString().slice(11, 19) + ' ' + parts.map(p => typeof p === 'string' ? p : JSON.stringify(p)).join(' ');
  console.debug('[Velox]', line);
  traceChain = traceChain.then(async () => {
    try {
      const { trace: old = [] } = await chrome.storage.local.get('trace');
      await chrome.storage.local.set({ trace: [...old.slice(-29), line] });
    } catch { }
  });
}

// ------------------------------------------------------------------ configurações

async function loadSettings() {
  const stored = await chrome.storage.local.get(DEFAULTS);
  settings = { ...DEFAULTS, ...stored };
  updateBadge();
}

function updateBadge() {
  try {
    chrome.action.setBadgeText({ text: settings.enabled ? '' : 'OFF' });
    chrome.action.setBadgeBackgroundColor({ color: '#5C637A' });
    chrome.action.setTitle({
      title: settings.enabled
        ? 'Velox: capturando downloads (Alt+Shift+V para pausar)'
        : 'Velox: captura desativada (Alt+Shift+V para ativar)'
    });
  } catch { }
}

function flashBadge(text, color) {
  try {
    chrome.action.setBadgeBackgroundColor({ color });
    chrome.action.setBadgeText({ text });
    setTimeout(updateBadge, 2500);
  } catch { }
}

// APIs opcionais (podem não existir em todos os navegadores/modos) — nunca podem derrubar o worker
function safe(fn) { try { fn(); } catch (e) { console.warn('[Velox] API indisponível:', e); } }

safe(() => chrome.storage.onChanged.addListener(loadSettings));
safe(() => chrome.runtime.onInstalled.addListener(() => { loadSettings(); createMenus(); }));
safe(() => chrome.runtime.onStartup.addListener(loadSettings));
loadSettings();

safe(() => chrome.commands.onCommand.addListener(async (command) => {
  if (command === 'toggle-capture') {
    await chrome.storage.local.set({ enabled: !settings.enabled });
  }
}));

// ------------------------------------------------------------------ menu de contexto

function createMenus() {
  if (!chrome.contextMenus) return;
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'velox-link', title: 'Baixar com Velox', contexts: ['link'] });
    chrome.contextMenus.create({ id: 'velox-media', title: 'Baixar mídia com Velox', contexts: ['image', 'video', 'audio'] });
    chrome.contextMenus.create({ id: 'velox-page', title: 'Baixar este endereço com Velox', contexts: ['page'] });
    chrome.contextMenus.create({ id: 'velox-bypass', title: 'Decifrar link com Velox (encurtador/safelink)', contexts: ['link', 'page'] });
  });
}

safe(() => chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const url = info.linkUrl || info.srcUrl || info.pageUrl;
  if (!url || !/^https?:/i.test(url)) return;
  if (info.menuItemId === 'velox-bypass') {
    const r = await sendNative({ type: 'bypass', url, referrer: info.pageUrl || tab?.url || '' });
    if (r?.ok) flashBadge('✓', '#22D3EE'); else notifyError(r?.error);
    return;
  }
  const payload = await buildPayload({ url, referrer: info.pageUrl || tab?.url || '', fileName: '' });
  const res = await sendNative(payload);
  if (res?.ok) flashBadge('✓', '#34D399');
  else notifyError(res?.error);
}));

// ------------------------------------------------------------------ interceptação

function extensionOf(nameOrUrl) {
  try {
    const path = /^https?:/i.test(nameOrUrl) ? new URL(nameOrUrl).pathname : nameOrUrl;
    const m = /\.([a-z0-9]{1,8})$/i.exec(path);
    return m ? m[1].toLowerCase() : '';
  } catch { return ''; }
}

function baseName(path) {
  if (!path) return '';
  return path.split(/[\\/]/).pop();
}

function shouldCapture(item) {
  const url = item.url || item.finalUrl || '';
  if (!/^https?:/i.test(url)) return false;          // blob:, data:, file: ficam no Chrome
  if (item.byExtensionId) return false;              // iniciado por outra extensão
  if (settings.minSizeKb > 0 && item.fileSize > 0 && item.fileSize < settings.minSizeKb * 1024) return false;

  const ignored = String(settings.ignoreTypes || '')
    .split(/[,\s;]+/).map(s => s.trim().replace(/^\./, '').toLowerCase()).filter(Boolean);
  const ext = extensionOf(item.filename || url);
  if (ext && ignored.includes(ext)) return false;
  return true;
}

// onCreated dispara assim que o navegador cria o download (antes de gravar em disco).
// É o gancho mais confiável no Manifest V3: pausamos, entregamos ao Velox e cancelamos.
chrome.downloads.onCreated.addListener((item) => {
  trace('onCreated', item.id, item.url, item.state, 'enabled=' + settings.enabled, 'capture=' + shouldCapture(item));
  if (!settings.enabled || !shouldCapture(item) || inFlight.has(item.id)) return;
  inFlight.add(item.id);
  handleDownload(item)
    .catch(e => trace('ERRO ao interceptar', String(e)))
    .finally(() => inFlight.delete(item.id));
});

const sleep = ms => new Promise(r => setTimeout(r, ms));

// Espera o navegador determinar nome/MIME/tamanho (chegam logo após os cabeçalhos).
async function refreshItem(item) {
  let current = item;
  for (let i = 0; i < 10; i++) {
    if (baseName(current.filename) && current.mime) break;
    await sleep(120);
    const [found] = await chrome.downloads.search({ id: item.id });
    if (!found) break;
    current = found;
    if (current.state !== 'in_progress') break;
  }
  return current;
}

async function handleDownload(item) {
  // segura o download enquanto o Velox responde, para não gastar banda em dobro
  try { await chrome.downloads.pause(item.id); } catch { }

  const current = await refreshItem(item);
  trace('interceptando', current.id, current.filename, current.mime, current.fileSize, current.state);

  if (current.state !== 'in_progress') {
    // já terminou (arquivo pequeno) ou falhou no navegador — não há o que desviar
    trace('ignorado: estado', current.state);
    return false;
  }

  if (!shouldCapture(current)) {
    // com nome/tamanho conhecidos caiu numa regra de exclusão → devolve ao navegador
    trace('ignorado por regra de exclusão');
    try { await chrome.downloads.resume(item.id); } catch { }
    return false;
  }

  // URL original: links finais (CDN assinado) costumam expirar e impediriam a retomada
  const payload = await buildPayload({
    url: current.url || current.finalUrl,
    finalUrl: current.finalUrl || '',
    referrer: current.referrer || '',
    fileName: baseName(current.filename),
    mime: current.mime || '',
    size: typeof current.fileSize === 'number' ? current.fileSize : -1
  });

  const res = await sendNative(payload);
  trace('resposta do host', res);

  if (res?.ok) {
    try { await chrome.downloads.cancel(item.id); } catch { }
    try { await chrome.downloads.erase({ id: item.id }); } catch { }
    flashBadge('✓', '#34D399');
    if (settings.notify) notify('Enviado para o Velox', payload.fileName || payload.url);
    return true;
  }

  // Velox indisponível → devolve o download ao navegador
  try { await chrome.downloads.resume(item.id); } catch { }
  notifyError(res?.error);
  return false;
}

async function buildPayload({ url, finalUrl = '', referrer, fileName, mime = '', size = -1 }) {
  return {
    type: 'download',
    url,
    finalUrl,
    referrer,
    fileName,
    mime,
    size,
    cookies: await cookieHeaderFor(url, finalUrl),
    userAgent: navigator.userAgent,
    source: 'chrome'
  };
}

async function cookieHeaderFor(...urls) {
  const seen = new Map();
  for (const u of urls.filter(Boolean)) {
    try {
      for (const c of await chrome.cookies.getAll({ url: u })) seen.set(c.name, c.value);
    } catch { }
  }
  return [...seen].map(([k, v]) => `${k}=${v}`).join('; ');
}

// ------------------------------------------------------------------ native messaging

function sendNative(message) {
  return new Promise(resolve => {
    try {
      chrome.runtime.sendNativeMessage(HOST, message, response => {
        if (chrome.runtime.lastError) {
          resolve({ ok: false, error: chrome.runtime.lastError.message });
        } else {
          resolve(response || { ok: false, error: 'Sem resposta do Velox.' });
        }
      });
    } catch (e) {
      resolve({ ok: false, error: String(e) });
    }
  });
}

// o popup usa isto para checar o estado
chrome.runtime.onMessage.addListener((msg, _sender, reply) => {
  if (msg?.type === 'ping') {
    sendNative({ type: 'ping' }).then(reply);
    return true;
  }
  if (msg?.type === 'show') {
    sendNative({ type: 'show' }).then(reply);
    return true;
  }
  if (msg?.type === 'getMedia') {
    const list = (mediaByTab.get(_sender?.tab?.id ?? -1) || []).slice().sort((a, b) => b.at - a.at);
    reply({ media: list });
    return true;
  }
  if (msg?.type === 'sendVideo') {
    sendVideoToVelox(msg, _sender).then(reply);
    return true;
  }
});

// ------------------------------------------------------------------ sniffer de mídia (botão de vídeo)
// Observa respostas de vídeo/áudio por aba para oferecer o arquivo real mesmo quando o
// player usa blob:/MSE. Só leitura (webRequest não bloqueante, permitido no MV3).

const mediaByTab = new Map();   // tabId -> [{url, type, size, at}]
const MEDIA_EXT = /\.(mp4|m4v|webm|mkv|mov|avi|flv|ts|mp3|m4a|aac|ogg|opus|wav|flac)(?:$|[?#])/i;
const MANIFEST_EXT = /\.(m3u8|m3u|mpd)(?:$|[?#])/i;
const SEGMENT_EXT = /\.(ts|m4s|m4v|m4a|aac|mp4|webm|cmfv|cmfa)(?:$|[?#])/i;

// 'hls' | 'dash' | null — manifesto de stream segmentado, por content-type ou extensão
function manifestKind(url, type) {
  if (/mpegurl/.test(type)) return 'hls';
  if (/dash\+xml/.test(type)) return 'dash';
  const m = MANIFEST_EXT.exec(url);
  if (m) return m[1].toLowerCase() === 'mpd' ? 'dash' : 'hls';
  return null;
}

function dirOf(url) { try { const u = new URL(url); return u.origin + u.pathname.slice(0, u.pathname.lastIndexOf('/') + 1); } catch { return ''; } }

function normalizeMediaUrl(raw) {
  // remove parâmetros de faixa (chunks) para agrupar o mesmo arquivo
  try {
    const u = new URL(raw);
    for (const k of ['range', 'bytes', 'rn', 'rbuf']) u.searchParams.delete(k);
    return u.toString();
  } catch { return raw; }
}

function rememberMedia(tabId, url, type, size, kind) {
  if (tabId < 0) return;
  const key = kind ? url : normalizeMediaUrl(url);
  let list = mediaByTab.get(tabId);
  if (!list) { list = []; mediaByTab.set(tabId, list); }
  const existing = list.find(m => m.url === key);
  if (existing) { if (size > existing.size) existing.size = size; existing.at = Date.now(); return; }
  if (kind) {
    // playlists de variante/áudio ficam na mesma pasta (ou abaixo) do master, que chega primeiro: guarda só o master
    const dir = dirOf(key);
    if (list.some(m => m.kind === kind && dir.startsWith(dirOf(m.url)))) return;
  }
  list.push({ url: key, type, size, at: Date.now(), kind: kind || '' });
  if (list.length > 40) list.shift();
}

safe(() => chrome.webRequest.onHeadersReceived.addListener(details => {
  try {
    if (details.tabId < 0 || !/^https?:/i.test(details.url)) return;
    const h = Object.fromEntries((details.responseHeaders || []).map(x => [x.name.toLowerCase(), x.value || '']));
    const type = (h['content-type'] || '').toLowerCase();

    // manifestos HLS/DASH: o Velox baixa os segmentos e junta (o manifesto em si é pequeno)
    const kind = manifestKind(details.url, type);
    if (kind) {
      const mime = /mpegurl|dash\+xml/.test(type) ? type.split(';')[0].trim()
        : kind === 'dash' ? 'application/dash+xml' : 'application/vnd.apple.mpegurl';
      rememberMedia(details.tabId, details.url, mime, -1, kind);
      return;
    }

    const isMedia = type.startsWith('video/') || type.startsWith('audio/') ||
      (type === 'application/octet-stream' && MEDIA_EXT.test(details.url)) ||
      (!type && MEDIA_EXT.test(details.url));
    if (!isMedia) return;

    let size = -1;
    const range = /\/(\d+)\s*$/.exec(h['content-range'] || '');
    if (range) size = parseInt(range[1], 10);
    else if (h['content-length']) size = parseInt(h['content-length'], 10);
    if (size > 0 && size < 200 * 1024) return; // prévias/miniaturas

    // com um manifesto já visto na aba, os pedaços (.ts/.m4s, faixas parciais) são segmentos dele — não ofereça um a um
    const list = mediaByTab.get(details.tabId);
    if (list && list.some(m => m.kind) && (/mp2t|iso\.segment/.test(type) || SEGMENT_EXT.test(details.url) || h['content-range'])) return;

    rememberMedia(details.tabId, details.url, type, size);
  } catch { }
}, { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other', 'object'] }, ['responseHeaders']));

safe(() => chrome.tabs.onUpdated.addListener((tabId, info) => { if (info.status === 'loading') mediaByTab.delete(tabId); }));
safe(() => chrome.tabs.onRemoved.addListener(tabId => mediaByTab.delete(tabId)));

function sanitizeFileName(name) {
  return String(name || '').replace(/[\\/:*?"<>|\u0000-\u001f]+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 100);
}

function extForMedia(url, mime) {
  if (manifestKind(url, (mime || '').toLowerCase())) return '.mp4'; // stream: o Velox junta os segmentos em MP4
  const m = MEDIA_EXT.exec(url);
  if (m) return '.' + m[1].toLowerCase();
  const t = (mime || '').split(';')[0].trim();
  return ({ 'video/mp4': '.mp4', 'video/webm': '.webm', 'video/x-matroska': '.mkv', 'video/quicktime': '.mov', 'video/x-flv': '.flv',
            'video/mp2t': '.ts', 'audio/mpeg': '.mp3', 'audio/mp4': '.m4a', 'audio/aac': '.aac', 'audio/ogg': '.ogg', 'audio/webm': '.weba',
            'audio/wav': '.wav', 'audio/flac': '.flac' })[t] || '.mp4';
}

async function sendVideoToVelox(msg, sender) {
  const title = sanitizeFileName(msg.title) || sanitizeFileName(new URL(msg.pageUrl || msg.url).hostname);
  const fileName = title + extForMedia(msg.url, msg.mime);
  const payload = await buildPayload({ url: msg.url, referrer: msg.pageUrl || sender?.tab?.url || '', fileName, mime: msg.mime || '', size: msg.size ?? -1 });
  payload.source = 'video';
  const res = await sendNative(payload);
  if (res?.ok) flashBadge('✓', '#34D399'); else notifyError(res?.error);
  return res;
}

// ------------------------------------------------------------------ notificações

function notify(title, message) {
  try {
    if (!chrome.notifications) return;
    chrome.notifications.create({
      type: 'basic',
      iconUrl: 'icons/icon128.png',
      title,
      message: String(message).slice(0, 200)
    });
  } catch { }
}

function notifyError(error) {
  flashBadge('!', '#F87171');
  const hint = /not found|não encontrado|forbidden/i.test(error || '')
    ? 'Abra o Velox e clique em "Instalar integração" nas configurações.'
    : (error || 'Velox não respondeu. O download continuou no Chrome.');
  notify('Velox indisponível', hint);
}
