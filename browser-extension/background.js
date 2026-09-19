// Velox Download Manager — service worker (Manifest V3)
// Intercepta downloads do navegador e entrega ao Velox via Native Messaging.

const HOST = 'com.velox.dm';

const DEFAULTS = {
  enabled: true,        // capturar downloads automaticamente
  minSizeKb: 0,         // ignorar arquivos menores que X KB (0 = capturar tudo)
  ignoreTypes: '',      // extensões ignoradas, separadas por vírgula (ex.: "torrent, pdf")
  notify: true          // notificação ao capturar
};

let settings = { ...DEFAULTS };
const inFlight = new Set();

// rastro dos últimos eventos (diagnóstico: visível em chrome.storage.local.trace)
async function trace(...parts) {
  const line = new Date().toISOString().slice(11, 19) + ' ' + parts.map(p => typeof p === 'string' ? p : JSON.stringify(p)).join(' ');
  console.debug('[Velox]', line);
  try {
    const { trace: old = [] } = await chrome.storage.local.get('trace');
    await chrome.storage.local.set({ trace: [...old.slice(-29), line] });
  } catch { }
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
});

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
