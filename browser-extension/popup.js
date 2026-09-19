const DEFAULTS = { enabled: true, minSizeKb: 0, ignoreTypes: '', notify: true };

const $ = id => document.getElementById(id);

async function load() {
  const s = { ...DEFAULTS, ...(await chrome.storage.local.get(DEFAULTS)) };
  $('enabled').checked = s.enabled;
  $('notify').checked = s.notify;
  $('minSizeKb').value = s.minSizeKb;
  $('ignoreTypes').value = s.ignoreTypes;
}

function save() {
  chrome.storage.local.set({
    enabled: $('enabled').checked,
    notify: $('notify').checked,
    minSizeKb: Math.max(0, parseInt($('minSizeKb').value, 10) || 0),
    ignoreTypes: $('ignoreTypes').value.trim()
  });
}

for (const id of ['enabled', 'notify', 'minSizeKb', 'ignoreTypes']) {
  $(id).addEventListener('change', save);
  $(id).addEventListener('input', save);
}

function setStatus(kind, text) {
  const el = $('status');
  el.className = 'status ' + kind;
  $('statusText').textContent = text;
}

chrome.runtime.sendMessage({ type: 'ping' }, res => {
  if (res?.ok) setStatus('ok', `Velox conectado (v${res.version || '1.0'})`);
  else if (res?.running === false) setStatus('err', 'Velox fechado — abra o programa (ou ele abre sozinho no próximo download).');
  else setStatus('err', 'Velox não encontrado. No Velox: Configurações → Navegador → Instalar integração.');
});

$('open').addEventListener('click', () => {
  chrome.runtime.sendMessage({ type: 'show' }, () => window.close());
});

load();
