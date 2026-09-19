using System.IO;
using System.Windows.Input;
using Velox.Core.Models;
using Velox.Core.Resolvers;
using Velox.Core.Services;
using Velox.Core.Streams;
using Velox.Core.Utils;

namespace Velox.App.ViewModels;

public sealed class AddDownloadViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _probeCts;
    private bool _fileNameEdited;
    private bool _categoryEdited;

    public event Action<bool>? RequestClose;

    public AddDownloadViewModel(MainViewModel main, IReadOnlyList<string>? urls,
        DownloadRequest? prefill = null, string? sourceLabel = null)
    {
        _main = main;
        var s = main.Settings;

        _directory = s.DownloadDirectory;
        _connections = s.ConnectionsPerDownload;
        _startImmediately = s.StartDownloadsImmediately;

        Categories = CategoryDetector.All.Select(c => new CategoryOption(c, DownloadItemViewModel.GlyphFor(c), this)).ToList();
        _category = CategoryDetector.Other;

        DownloadCommand = new RelayCommand(Submit, CanSubmit);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        PasteCommand = new RelayCommand(Paste);
        BrowseCommand = new RelayCommand(Browse);
        InstallFfmpegCommand = new AsyncRelayCommand(InstallFfmpegAsync, () => !_ffmpegInstalling && !HasFfmpeg);
        InstallYtDlpCommand = new AsyncRelayCommand(InstallYtDlpAsync, () => !_ytInstalling);

        if (urls != null && urls.Count > 1)
        {
            IsBatch = true;
            BatchUrls = string.Join(Environment.NewLine, urls);
        }
        else if (urls != null && urls.Count == 1)
        {
            Url = urls[0];
        }

        if (prefill != null) Prefill(prefill, sourceLabel);
    }

    /// <summary>Preenche o diálogo com um download vindo do navegador (cabeçalhos antes da URL, para a sondagem usá-los).</summary>
    public void Prefill(DownloadRequest req, string? sourceLabel)
    {
        IsBatch = false;
        SourceLabel = sourceLabel;
        Referer = req.Referer ?? "";
        HeadersText = req.Headers != null
            ? string.Join(Environment.NewLine, req.Headers.Select(kv => $"{kv.Key}: {kv.Value}"))
            : "";
        if (!string.IsNullOrWhiteSpace(req.Directory)) Directory = req.Directory;

        if (!string.IsNullOrWhiteSpace(req.FileName))
        {
            // o navegador já resolveu o nome (Content-Disposition etc.) — mantém
            Set(ref _fileName, req.FileName, nameof(FileName));
            _fileNameEdited = true;
            if (!_categoryEdited) SetCategoryInternal(CategoryDetector.Detect(req.FileName, null));
        }

        Url = req.Url;
    }

    private string? _sourceLabel;
    public string? SourceLabel { get => _sourceLabel; private set => Set(ref _sourceLabel, value); }

    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand InstallFfmpegCommand { get; }
    public ICommand InstallYtDlpCommand { get; }

    public List<CategoryOption> Categories { get; }

    // ------------------------------------------------------------ campos
    private string _url = "";
    public string Url
    {
        get => _url;
        set
        {
            if (Set(ref _url, value))
            {
                ResolvedUrl = null;
                Stream = null;
                _ = ProbeDebouncedAsync();
                OnPropertyChanged(nameof(CanDownload));
            }
        }
    }

    private bool _isBatch;
    public bool IsBatch
    {
        get => _isBatch;
        set
        {
            if (Set(ref _isBatch, value))
            {
                OnPropertyChanged(nameof(IsSingle));
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(BatchCountText));
            }
        }
    }

    public bool IsSingle => !_isBatch;

    private string _batchUrls = "";
    public string BatchUrls
    {
        get => _batchUrls;
        set
        {
            if (Set(ref _batchUrls, value))
            {
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(BatchCountText));
            }
        }
    }

    public string BatchCountText
    {
        get
        {
            int n = Services.UrlHelper.ExtractUrls(_batchUrls).Count;
            return n == 0 ? "Nenhum link válido" : n == 1 ? "1 link" : $"{n} links";
        }
    }

    private string _fileName = "";
    public string FileName
    {
        get => _fileName;
        set
        {
            if (Set(ref _fileName, value))
            {
                _fileNameEdited = true;
                if (!_categoryEdited) SetCategoryInternal(CategoryDetector.Detect(value, Probe?.ContentType));
            }
        }
    }

    private string _directory;
    public string Directory { get => _directory; set => Set(ref _directory, value); }

    private string _category;
    public string Category
    {
        get => _category;
        set
        {
            _categoryEdited = true;
            SetCategoryInternal(value);
        }
    }

    private void SetCategoryInternal(string value)
    {
        if (Set(ref _category, value, nameof(Category)))
            foreach (var c in Categories) c.NotifySelection();
    }

    private double _connections;
    public double Connections
    {
        get => _connections;
        set
        {
            if (Set(ref _connections, Math.Round(value))) OnPropertyChanged(nameof(ConnectionsText));
        }
    }

    public string ConnectionsText => $"{(int)_connections} {((int)_connections == 1 ? "conexão" : "conexões")}";

    private bool _startImmediately;
    public bool StartImmediately { get => _startImmediately; set => Set(ref _startImmediately, value); }

    private bool _showAdvanced;
    public bool ShowAdvanced { get => _showAdvanced; set => Set(ref _showAdvanced, value); }

    private string _referer = "";
    public string Referer { get => _referer; set => Set(ref _referer, value); }

    private string _headersText = "";
    public string HeadersText { get => _headersText; set => Set(ref _headersText, value); }

    private string _expectedHash = "";
    public string ExpectedHash { get => _expectedHash; set => Set(ref _expectedHash, value); }

    // ------------------------------------------------------------ link decifrado
    private string? _resolvedUrl;
    /// <summary>URL de destino quando a URL digitada era um encurtador/safelink.</summary>
    public string? ResolvedUrl
    {
        get => _resolvedUrl;
        private set
        {
            if (Set(ref _resolvedUrl, value)) OnPropertyChanged(nameof(HasResolvedUrl));
        }
    }

    public bool HasResolvedUrl => !string.IsNullOrEmpty(_resolvedUrl);

    private string _resolvedText = "";
    public string ResolvedText { get => _resolvedText; private set => Set(ref _resolvedText, value); }

    private static bool IsHtml(string? contentType) =>
        contentType != null && contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ stream (HLS/DASH)
    private StreamInfo? _stream;
    /// <summary>Preenchido quando a URL é um manifesto HLS/DASH; o download vira um StreamDownloadTask.</summary>
    public StreamInfo? Stream
    {
        get => _stream;
        private set
        {
            if (!Set(ref _stream, value)) return;
            Variants = value == null
                ? new List<VariantOption>()
                : value.Variants.Select(v => new VariantOption(v, this)).ToList();
            OnPropertyChanged(nameof(Variants));
            // YouTube: 4K é enorme (e AV1); começa na melhor até 1080p, o usuário sobe se quiser
            _selectedVariant = value?.Kind == StreamKind.Youtube
                ? value.Variants.Where(v => v.Height > 0 && v.Height <= 1080).OrderByDescending(v => v.Height).ThenByDescending(v => v.FrameRate).FirstOrDefault() ?? value.Best
                : value?.Best;
            foreach (var v in Variants) v.NotifySelection();
            OnPropertyChanged(nameof(IsStream));
            OnPropertyChanged(nameof(StreamTitle));
            OnPropertyChanged(nameof(StreamSummary));
            OnPropertyChanged(nameof(HasManyVariants));
            OnPropertyChanged(nameof(HasFfmpeg));
            OnPropertyChanged(nameof(FfmpegNote));
            OnPropertyChanged(nameof(ConnectionsMax));
            OnPropertyChanged(nameof(IsYoutube));
            _thumbnail = null;
            OnPropertyChanged(nameof(Thumbnail));
            if (value != null) Connections = Math.Clamp(_main.Settings.StreamConnections, 1, 16);
            else Connections = _main.Settings.ConnectionsPerDownload;
        }
    }

    public bool IsYoutube => _stream?.Kind == StreamKind.Youtube;

    private System.Windows.Media.ImageSource? _thumbnail;
    /// <summary>Miniatura (YouTube), baixada pelo WPF em segundo plano.</summary>
    public System.Windows.Media.ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail == null && _stream?.Thumbnail != null && Uri.TryCreate(_stream.Thumbnail, UriKind.Absolute, out var uri))
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = uri;
                    bmp.DecodePixelWidth = 192;
                    bmp.EndInit();
                    _thumbnail = bmp;
                }
                catch { }
            }
            return _thumbnail;
        }
    }

    /// <summary>Nome sugerido para um stream: título (YouTube) ou nome derivado da URL, com a extensão do contêiner da qualidade.</summary>
    private void SuggestStreamFileName()
    {
        if (_stream == null) return;
        var variant = _selectedVariant ?? _stream.Best;
        var ext = "." + (variant?.Container is "mp4" or "webm" or "mkv" or "m4a" ? variant.Container : "mp4");
        string baseName;
        if (!string.IsNullOrWhiteSpace(_stream.Title))
            baseName = FileNameHelper.Sanitize(_stream.Title);
        else
            baseName = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(_fileName) ? StreamFileName(_probe?.FileName, _stream.ManifestUrl) : _fileName);
        Set(ref _fileName, baseName + ext, nameof(FileName));
        if (!_categoryEdited) SetCategoryInternal(variant?.Container == "m4a" ? CategoryDetector.Music : CategoryDetector.Video);
    }

    public bool IsStream => _stream != null;
    public List<VariantOption> Variants { get; private set; } = new();
    public bool HasManyVariants => Variants.Count > 1;
    public int ConnectionsMax => IsStream ? 16 : 32;

    private StreamVariant? _selectedVariant;
    public StreamVariant? SelectedVariant
    {
        get => _selectedVariant;
        set
        {
            if (ReferenceEquals(_selectedVariant, value)) return;
            _selectedVariant = value;
            OnPropertyChanged();
            foreach (var v in Variants) v.NotifySelection();
            if (!_fileNameEdited || IsStreamName(_fileName)) SuggestStreamFileName();
        }
    }

    private static bool IsStreamName(string name) =>
        name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) || name.StartsWith("youtube-", StringComparison.OrdinalIgnoreCase);

    public string StreamTitle => _stream == null ? ""
        : _stream.Kind == StreamKind.Youtube ? (_stream.Title ?? "Vídeo do YouTube")
        : _stream.Kind == StreamKind.Dash ? "Stream DASH (MPEG-DASH)" : "Stream HLS (m3u8)";

    public string StreamSummary
    {
        get
        {
            if (_stream == null) return "";
            var parts = new List<string>();
            if (_stream.Kind == StreamKind.Youtube) parts.Add("YouTube" + (!string.IsNullOrWhiteSpace(_stream.Uploader) ? " · " + _stream.Uploader : ""));
            if (_stream.DurationSeconds > 0) parts.Add(FormatHelper.Duration(_stream.DurationSeconds));
            parts.Add(_stream.Variants.Count == 1 ? "1 qualidade" : $"{_stream.Variants.Count} qualidades");
            if (_stream.IsEncrypted && _stream.DrmSystem == null) parts.Add("AES-128");
            if (_stream.Variants.Any(v => v.HasSeparateAudio)) parts.Add("áudio separado");
            return string.Join("  •  ", parts);
        }
    }

    public bool HasFfmpeg => _main.FfmpegPath != null;

    private bool _ffmpegInstalling;
    private string _ffmpegProgress = "";
    public string FfmpegProgress { get => _ffmpegProgress; private set => Set(ref _ffmpegProgress, value); }

    private bool _needsYtDlp;
    /// <summary>URL do YouTube sem yt-dlp instalado: mostra o botão de instalar no lugar da sondagem.</summary>
    public bool NeedsYtDlp { get => _needsYtDlp; private set { if (Set(ref _needsYtDlp, value)) OnPropertyChanged(nameof(CanDownload)); } }

    private bool _ytInstalling;
    private string _ytProgress = "";
    public string YtDlpProgress { get => _ytProgress; private set => Set(ref _ytProgress, value); }

    private async Task InstallYtDlpAsync()
    {
        _ytInstalling = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            YtDlpProgress = "Baixando yt-dlp…";
            await _main.InstallYoutubeToolsAsync(new Progress<string>(t => YtDlpProgress = t), CancellationToken.None);
            YtDlpProgress = "";
            NeedsYtDlp = false;
            _ = ProbeDebouncedAsync(); // sonda de novo, agora com o extrator
        }
        catch (Exception ex)
        {
            YtDlpProgress = "Falha ao instalar: " + ex.Message;
        }
        finally
        {
            _ytInstalling = false;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public string FfmpegNote => HasFfmpeg
        ? "Os segmentos serão juntados em um MP4 pelo ffmpeg."
        : "Sem ffmpeg o vídeo é salvo como .ts (ou vídeo e áudio em arquivos separados). Instale para gerar MP4.";

    private async Task InstallFfmpegAsync()
    {
        _ffmpegInstalling = true;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            FfmpegProgress = "Baixando ffmpeg…";
            var progress = new Progress<(long Done, long Total)>(p =>
                FfmpegProgress = p.Total > 0
                    ? $"Baixando ffmpeg… {FormatHelper.Bytes(p.Done)} de {FormatHelper.Bytes(p.Total)}"
                    : $"Baixando ffmpeg… {FormatHelper.Bytes(p.Done)}");
            await _main.InstallFfmpegAsync(progress, CancellationToken.None);
            FfmpegProgress = "";
        }
        catch (Exception ex)
        {
            FfmpegProgress = "Falha ao instalar: " + ex.Message;
        }
        finally
        {
            _ffmpegInstalling = false;
            OnPropertyChanged(nameof(HasFfmpeg));
            OnPropertyChanged(nameof(FfmpegNote));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    // ------------------------------------------------------------ sondagem
    private ProbeResult? _probe;
    public ProbeResult? Probe
    {
        get => _probe;
        private set
        {
            if (Set(ref _probe, value)) OnPropertyChanged(nameof(HasProbe));
        }
    }

    public bool HasProbe => _probe != null;

    private string _probeState = "idle"; // idle | loading | ok | error
    public string ProbeState
    {
        get => _probeState;
        private set
        {
            if (Set(ref _probeState, value))
            {
                OnPropertyChanged(nameof(IsProbing));
                OnPropertyChanged(nameof(ProbeOk));
                OnPropertyChanged(nameof(ProbeError));
                OnPropertyChanged(nameof(CanDownload));
            }
        }
    }

    public bool IsProbing => _probeState == "loading";
    public bool ProbeOk => _probeState == "ok";
    public bool ProbeError => _probeState == "error";

    private string _probeMessage = "";
    public string ProbeMessage { get => _probeMessage; private set => Set(ref _probeMessage, value); }

    private string _probeSizeText = "";
    public string ProbeSizeText { get => _probeSizeText; private set => Set(ref _probeSizeText, value); }

    private string _probeTypeText = "";
    public string ProbeTypeText { get => _probeTypeText; private set => Set(ref _probeTypeText, value); }

    private string _probeHostText = "";
    public string ProbeHostText { get => _probeHostText; private set => Set(ref _probeHostText, value); }

    private bool _probeResumable;
    public bool ProbeResumable { get => _probeResumable; private set => Set(ref _probeResumable, value); }

    private int _probeVersion;

    private async Task ProbeDebouncedAsync()
    {
        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        int version = ++_probeVersion;

        var url = _url.Trim();
        if (!Services.UrlHelper.IsHttpUrl(url))
        {
            Probe = null;
            ProbeState = "idle";
            return;
        }

        try
        {
            await Task.Delay(450, cts.Token);
            ProbeState = "loading";

            if (YoutubeExtractor.IsYoutubeUrl(url))
            {
                await ProbeYoutubeAsync(url, version, cts.Token);
                return;
            }
            NeedsYtDlp = false;
            ProbeMessage = "Consultando servidor…";

            var result = await _main.ProbeAsync(url, ParseHeaders(), string.IsNullOrWhiteSpace(_referer) ? null : _referer, cts.Token);
            if (version != _probeVersion) return;

            string? resolvedNote = null;
            if (IsHtml(result.ContentType) && _main.Settings.AutoResolveShortLinks)
            {
                // é uma página, não um arquivo: pode ser um encurtador/safelink
                ProbeMessage = "É uma página — verificando se é um link encurtado…";
                var resolved = await _main.ResolveLinkAsync(url, line => ProbeMessage = line, cts.Token);
                if (version != _probeVersion) return;

                if (resolved.Status == ResolveStatus.Resolved)
                {
                    ResolvedUrl = resolved.FinalUrl;
                    var chain = string.Join(" → ", new[] { HostOf(url) }.Concat(resolved.Steps.Select(st => HostOf(st.Url))).Distinct());
                    ResolvedText = $"Link decifrado em {resolved.Steps.Count} passo(s): {chain}";
                    result = await _main.ProbeAsync(resolved.FinalUrl, ParseHeaders(), url, cts.Token);
                    if (version != _probeVersion) return;
                    if (IsHtml(result.ContentType))
                        resolvedNote = "O destino é uma página (não um arquivo direto). Abra no navegador para pegar o arquivo.";
                }
                else if (resolved.Status == ResolveStatus.NeedsBrowser)
                {
                    resolvedNote = resolved.Error;
                }
                else if (resolved.Status == ResolveStatus.Failed && resolved.Steps.Count > 0)
                {
                    resolvedNote = "Encurtador reconhecido, mas a decifração falhou: " + resolved.Error;
                }
                else
                {
                    resolvedNote = "Esta URL responde com uma página HTML, não com um arquivo.";
                }
            }

            // manifesto HLS/DASH? lista as qualidades e troca o modo do diálogo
            var probedUrl = _resolvedUrl ?? url;
            if (StreamProbe.LooksLikeManifestUrl(probedUrl) || StreamProbe.IsManifestContentType(result.ContentType))
            {
                ProbeMessage = "Lendo manifesto do stream…";
                StreamInfo? info = null;
                try { info = await _main.ProbeStreamAsync(probedUrl, ParseHeaders(), string.IsNullOrWhiteSpace(_referer) ? null : _referer, result.ContentType, cts.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { resolvedNote = "Não foi possível ler o manifesto: " + ex.Message; }
                if (version != _probeVersion) return;

                if (info != null)
                {
                    Stream = info;
                    Probe = result;
                    ProbeState = info.IsLive || info.DrmSystem != null || info.Variants.Count == 0 ? "error" : "ok";
                    ProbeSizeText = info.DurationSeconds > 0 ? FormatHelper.Duration(info.DurationSeconds) : "duração desconhecida";
                    ProbeTypeText = info.Kind == StreamKind.Dash ? "DASH" : "HLS";
                    ProbeHostText = result.Host;
                    ProbeResumable = true;
                    ProbeMessage = info.IsLive ? "Transmissão ao vivo — o Velox baixa apenas vídeos sob demanda (VOD)."
                        : info.DrmSystem != null ? $"Stream protegido por DRM ({info.DrmSystem}) — não é possível baixar."
                        : info.Variants.Count == 0 ? "O manifesto não lista nenhuma faixa de vídeo."
                        : $"Stream segmentado reconhecido — {info.Variants.Count} {(info.Variants.Count == 1 ? "qualidade" : "qualidades")} disponíveis";

                    if (!_fileNameEdited || _fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || _fileName.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
                        Set(ref _fileName, StreamFileName(result.FileName, probedUrl), nameof(FileName));
                    if (!_categoryEdited) SetCategoryInternal(CategoryDetector.Video);
                    return;
                }
            }

            Probe = result;
            ProbeState = "ok";
            ProbeSizeText = result.Size > 0 ? FormatHelper.Bytes(result.Size) : "tamanho desconhecido";
            ProbeTypeText = result.ContentType ?? "—";
            ProbeHostText = result.Host;
            ProbeResumable = result.SupportsResume;
            ProbeMessage = resolvedNote ?? (result.SupportsResume
                ? "Suporta retomada e múltiplas conexões"
                : "Servidor sem suporte a retomada — será usada 1 conexão");
            if (resolvedNote != null && !HasResolvedUrl) ProbeState = "error";

            if (!_fileNameEdited)
            {
                Set(ref _fileName, result.FileName, nameof(FileName));
            }
            if (!_categoryEdited)
                SetCategoryInternal(CategoryDetector.Detect(_fileName, result.ContentType));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _probeVersion) return;
            Probe = null;
            ProbeState = "error";
            ProbeMessage = ex.Message;
            if (!_fileNameEdited && string.IsNullOrWhiteSpace(_fileName))
                Set(ref _fileName, FileNameHelper.FromUrl(url), nameof(FileName));
        }
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>YouTube: sem sondagem HTTP — o yt-dlp lista título, duração e formatos (links diretos do googlevideo).</summary>
    private async Task ProbeYoutubeAsync(string url, int version, CancellationToken ct)
    {
        Probe = null;
        if (_main.YtDlpPath == null)
        {
            NeedsYtDlp = true;
            ProbeState = "error";
            ProbeMessage = "Para baixar do YouTube o Velox usa o yt-dlp (extrator de links) — instale com um clique. Os downloads continuam sendo feitos pelo engine do Velox.";
            return;
        }
        NeedsYtDlp = false;
        ProbeMessage = "Consultando o YouTube (yt-dlp)…";
        var info = await _main.ProbeStreamAsync(url, null, null, null, ct);
        if (version != _probeVersion) return;
        if (info == null) throw new Velox.Core.DownloadException("O yt-dlp não reconheceu este link.", false);

        Stream = info;
        ProbeState = info.IsLive || info.Variants.Count == 0 ? "error" : "ok";
        ProbeSizeText = info.DurationSeconds > 0 ? FormatHelper.Duration(info.DurationSeconds) : "duração desconhecida";
        ProbeTypeText = "YouTube";
        ProbeHostText = info.Uploader ?? "youtube.com";
        ProbeResumable = true;
        ProbeMessage = info.IsLive ? "Transmissão ao vivo — só vídeos já publicados podem ser baixados."
            : info.Variants.Count == 0 ? "O YouTube não devolveu nenhum formato baixável para este vídeo."
            : $"Vídeo reconhecido — {info.Variants.Count} {(info.Variants.Count == 1 ? "opção" : "opções")} de qualidade";
        if (!_fileNameEdited || IsStreamName(_fileName)) SuggestStreamFileName();
    }

    private static readonly string[] GenericStreamNames = { "index", "master", "playlist", "manifest", "video", "stream", "prog_index", "chunklist", "hls", "dash" };

    /// <summary>Nome de saída para um stream: troca .m3u8/.mpd por .mp4 e evita nomes genéricos (playlist, manifest, index…).</summary>
    private static string StreamFileName(string? probedName, string url)
    {
        var name = Path.GetFileNameWithoutExtension(probedName ?? "");
        if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || GenericStreamNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            // usa o último trecho "falante" do caminho, senão um nome com data
            name = "";
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int i = segs.Length - 2; i >= 0; i--)
                {
                    var s = Uri.UnescapeDataString(segs[i]);
                    if (s.Length >= 3 && !GenericStreamNames.Contains(s, StringComparer.OrdinalIgnoreCase))
                    {
                        name = Path.GetFileNameWithoutExtension(s);
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(name) || name.Length < 3) name = "video-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }
        return FileNameHelper.Sanitize(name) + ".mp4";
    }

    private Dictionary<string, string>? ParseHeaders()
    {
        if (string.IsNullOrWhiteSpace(_headersText)) return null;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _headersText.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (key.Length > 0) dict[key] = val;
        }
        return dict.Count == 0 ? null : dict;
    }

    // ------------------------------------------------------------ envio
    public bool CanDownload => _isBatch
        ? Services.UrlHelper.ExtractUrls(_batchUrls).Count > 0
        : Services.UrlHelper.IsHttpUrl(_url.Trim()) && !IsProbing && !_needsYtDlp
          && !(_stream != null && (_stream.IsLive || _stream.DrmSystem != null || _stream.Variants.Count == 0));

    private bool CanSubmit() => CanDownload && !string.IsNullOrWhiteSpace(_directory);

    private void Submit()
    {
        var headers = ParseHeaders();
        var referer = string.IsNullOrWhiteSpace(_referer) ? null : _referer.Trim();
        var dir = _directory.Trim();

        var requests = new List<DownloadRequest>();
        if (_isBatch)
        {
            foreach (var u in Services.UrlHelper.ExtractUrls(_batchUrls))
                requests.Add(new DownloadRequest
                {
                    Url = u,
                    Directory = dir,
                    MaxConnections = (int)_connections,
                    Referer = referer,
                    Headers = headers,
                    StartImmediately = _startImmediately
                });
        }
        else
        {
            requests.Add(new DownloadRequest
            {
                Url = _resolvedUrl ?? _url.Trim(),
                FileName = string.IsNullOrWhiteSpace(_fileName) ? null : _fileName.Trim(),
                Directory = dir,
                MaxConnections = (int)_connections,
                Referer = referer ?? (_resolvedUrl != null ? _url.Trim() : null),
                Headers = headers,
                ExpectedHash = string.IsNullOrWhiteSpace(_expectedHash) ? null : _expectedHash.Trim(),
                Category = _categoryEdited ? _category : null,
                StartImmediately = _startImmediately,
                Probe = _stream != null ? null : _probe,
                Kind = _stream?.Kind ?? StreamKind.File,
                VariantId = _stream != null ? (_selectedVariant ?? _stream.Best)?.Id : null,
                VariantLabel = _stream != null ? (_selectedVariant ?? _stream.Best)?.Label : null
            });
        }

        _main.AddDownloads(requests);
        RequestClose?.Invoke(true);
    }

    private void Paste()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var text = System.Windows.Clipboard.GetText();
            var urls = Services.UrlHelper.ExtractUrls(text);
            if (urls.Count > 1)
            {
                IsBatch = true;
                BatchUrls = string.Join(Environment.NewLine, urls);
            }
            else if (urls.Count == 1)
            {
                if (_isBatch) BatchUrls = string.IsNullOrWhiteSpace(_batchUrls) ? urls[0] : _batchUrls.TrimEnd() + Environment.NewLine + urls[0];
                else Url = urls[0];
            }
        }
        catch { }
    }

    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Escolher pasta de destino",
            InitialDirectory = System.IO.Directory.Exists(_directory) ? _directory : null
        };
        if (dlg.ShowDialog() == true) Directory = dlg.FolderName;
    }

    public void Cancel()
    {
        _probeCts?.Cancel();
    }

    public sealed class VariantOption : ObservableObject
    {
        private readonly AddDownloadViewModel _owner;

        public VariantOption(StreamVariant variant, AddDownloadViewModel owner)
        {
            Variant = variant;
            _owner = owner;
        }

        public StreamVariant Variant { get; }
        public string Label => Variant.Label;

        public string Detail
        {
            get
            {
                var parts = new List<string>();
                if (Variant.Width > 0 && Variant.Height > 0) parts.Add($"{Variant.Width}×{Variant.Height}");
                if (!string.IsNullOrEmpty(Variant.Codecs) && Variant.Height > 0)
                {
                    // "avc1.64002a" → "avc1"; nomes amigáveis ("H.264", "AV1") ficam como estão
                    var codec = Variant.Codecs.Split(',')[0].Trim();
                    if (System.Text.RegularExpressions.Regex.IsMatch(codec, @"^[a-z0-9]{3,5}\.[0-9a-fA-F.]+$")) codec = codec.Split('.')[0];
                    parts.Add(codec);
                }
                if (Variant.HasSeparateAudio && !string.IsNullOrEmpty(Variant.AudioLabel)) parts.Add("áudio: " + Variant.AudioLabel);
                else if (Variant.Height == 0 && !string.IsNullOrEmpty(Variant.AudioLabel)) parts.Add(Variant.AudioLabel);
                if (Variant.SizeBytes > 0) parts.Add(FormatHelper.Bytes(Variant.SizeBytes));
                return string.Join(" · ", parts);
            }
        }

        public bool HasDetail => Detail.Length > 0;

        public bool IsSelected
        {
            get => ReferenceEquals(_owner.SelectedVariant, Variant);
            set { if (value) _owner.SelectedVariant = Variant; }
        }

        public void NotifySelection() => OnPropertyChanged(nameof(IsSelected));
    }

    public sealed class CategoryOption : ObservableObject
    {
        private readonly AddDownloadViewModel _owner;

        public CategoryOption(string name, string glyph, AddDownloadViewModel owner)
        {
            Name = name;
            Glyph = glyph;
            _owner = owner;
        }

        public string Name { get; }
        public string Glyph { get; }

        public bool IsSelected
        {
            get => _owner.Category == Name;
            set { if (value) _owner.Category = Name; }
        }

        public void NotifySelection() => OnPropertyChanged(nameof(IsSelected));
    }
}
