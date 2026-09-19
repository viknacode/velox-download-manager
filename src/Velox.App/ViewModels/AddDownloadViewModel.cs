using System.IO;
using System.Windows.Input;
using Velox.Core.Models;
using Velox.Core.Resolvers;
using Velox.Core.Services;
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
        : Services.UrlHelper.IsHttpUrl(_url.Trim()) && !IsProbing;

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
                Probe = _probe
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
