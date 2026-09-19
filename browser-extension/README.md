# Extensão Velox para Chrome / Edge / Brave

Desvia os downloads do navegador para o Velox Download Manager, como IDM e JDownloader.

## Instalação (uma vez por navegador)

1. Abra o Velox → **Configurações** → seção **Navegador** → **Instalar / reparar integração**
   (o Velox também faz isso sozinho toda vez que abre).
2. No navegador, abra `chrome://extensions` (ou `edge://extensions`), ative o
   **Modo do desenvolvedor** e clique em **Carregar sem compactação**.
3. Escolha esta pasta (`extension`, ao lado do `VeloxDM.exe`).

Pronto: qualquer download iniciado no navegador é pausado, entregue ao Velox com os
cookies e o User-Agent da sua sessão, e removido da lista do navegador. Se o Velox
estiver fechado, ele é aberto automaticamente.

## Uso

- Ícone da extensão → liga/desliga a captura, tamanho mínimo, tipos ignorados.
- Atalho `Alt+Shift+V` alterna a captura.
- Botão direito em qualquer link/imagem/vídeo → **Baixar com Velox**.
- Botão direito em um link encurtado (shrinkme, gplinks…) → **Decifrar link com Velox**: abre a
  janela "Decifrar link" do Velox já resolvendo até o destino.
- Se o Velox não responder, o download continua normalmente no navegador.
- **Botão nos vídeos**: passe o mouse sobre um vídeo → aparece "Baixar com Velox" no canto
  superior direito. Clique envia ao Velox (nome = título da página). Se houver mais de um
  arquivo (qualidades/áudio), abre um painel com tipo e tamanho. Pode ser desligado no popup.
  Funciona com arquivos progressivos (mp4, webm, mp3…) e com streams **HLS/DASH**: o sniffer
  guarda o manifesto master (`.m3u8`/`.mpd`) visto na aba e o Velox baixa os segmentos e
  junta em MP4 (escolha da qualidade no diálogo do Velox). No YouTube (watch/shorts) o botão envia o
  endereço do vídeo e o Velox extrai as qualidades com o yt-dlp. Players com DRM não.

## Como funciona

`background.js` escuta `chrome.downloads.onCreated`, pausa o item, resolve nome/tipo/tamanho
e chama `chrome.runtime.sendNativeMessage("com.velox.dm", …)`. O Chrome inicia o
`VeloxDM.exe` como *native messaging host* (manifesto registrado em
`HKCU\Software\Google\Chrome\NativeMessagingHosts\com.velox.dm`), que repassa a
mensagem à instância do Velox pelo named pipe `VeloxDM_Bridge`. O ID da extensão é fixo
(`ednjdkmgoihpnopanpflbledhfkkmagh`) graças ao campo `key` do `manifest.json`.

Diagnóstico: os últimos eventos ficam em `chrome.storage.local.trace` (downloads) e
`chrome.storage.local.videoTrace` (botão de vídeo) (visível no
console do service worker em `chrome://extensions` → *Inspecionar visualizações*).

## Limitações

- Downloads via `blob:`/`data:` e formulários POST ficam no navegador (não há URL
  reproduzível).
- Extensões carregadas "sem compactação" fazem o Chrome mostrar um aviso de modo do
  desenvolvedor ao iniciar; publicar na Chrome Web Store elimina isso (a chave privada
  para manter o mesmo ID não está no repositório).
