using System.Windows.Input;
using Microsoft.Win32;
using Velox.Core.Models;
using Velox.Core.Streams;
using Velox.Core.Utils;

namespace Velox.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _original;

    public event Action<bool>? RequestClose;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _original = main.Settings;
        var s = _original;

        _downloadDirectory = s.DownloadDirectory;
        _organizeByCategory = s.OrganizeByCategory;
        _maxConcurrent = s.MaxConcurrentDownloads;
        _connections = s.ConnectionsPerDownload;
        _speedLimitEnabled = s.SpeedLimitEnabled;
        _speedLimitKb = (int)(s.SpeedLimitBytesPerSecond / 1024);
        _monitorClipboard = s.MonitorClipboard;
        _captureAnyUrl = s.CaptureAnyUrl;
        _minimizeToTray = s.MinimizeToTray;
        _closeToTray = s.CloseToTray;
        _startMinimized = s.StartMinimized;
        _startWithWindows = s.StartWithWindows;
        _autoResume = s.AutoResumeOnStartup;
        _autoRetry = s.AutoRetryFailed;
        _maxAutoRetries = s.MaxAutoRetries;
        _maxRetriesPerSegment = s.MaxRetriesPerSegment;
        _readTimeout = s.ReadTimeoutSeconds;
        _userAgent = s.UserAgent;
        _proxyUrl = s.ProxyUrl ?? "";
        _verifyHash = s.VerifyHashOnComplete;
        _showNotifications = s.ShowNotifications;
        _startImmediately = s.StartDownloadsImmediately;
        _browserAsk = s.BrowserAskBeforeDownload;
        _autoResolve = s.AutoResolveShortLinks;
        _streamConnections = s.StreamConnections;
        _ffmpegPath = s.FfmpegPath ?? "";
        _ytDlpPath = s.YtDlpPath ?? "";

        InstallIntegrationCommand = new RelayCommand(InstallIntegration);
        OpenExtensionFolderCommand = new RelayCommand(Services.BrowserIntegration.OpenExtensionFolder);
        OpenChromeExtensionsCommand = new RelayCommand(() => OpenExtensionsPage("chrome.exe", "chrome://extensions/"));
        OpenEdgeExtensionsCommand = new RelayCommand(() => OpenExtensionsPage("msedge.exe", "edge://extensions/"));
        RefreshIntegrationStatus();

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        BrowseCommand = new RelayCommand(Browse);
        ResetUserAgentCommand = new RelayCommand(() => UserAgent = new AppSettings().UserAgent);
        InstallFfmpegCommand = new AsyncRelayCommand(InstallFfmpegAsync, () => !_ffmpegInstalling);
        BrowseFfmpegCommand = new RelayCommand(BrowseFfmpeg);
        RefreshFfmpegStatus();
        InstallYtDlpCommand = new AsyncRelayCommand(InstallYtDlpAsync, () => !_ytInstalling);
        BrowseYtDlpCommand = new RelayCommand(BrowseYtDlp);
        RefreshYtDlpStatus();
    }

    // ------------------------------------------------------------ YouTube (yt-dlp)
    public ICommand InstallYtDlpCommand { get; }
    public ICommand BrowseYtDlpCommand { get; }

    private string _ytDlpPath;
    public string YtDlpPath
    {
        get => _ytDlpPath;
        set { if (Set(ref _ytDlpPath, value)) RefreshYtDlpStatus(); }
    }

    private bool _ytDlpFound;
    public bool YtDlpFound { get => _ytDlpFound; private set => Set(ref _ytDlpFound, value); }

    private string _ytDlpStatus = "";
    public string YtDlpStatus { get => _ytDlpStatus; private set => Set(ref _ytDlpStatus, value); }

    private string _jsRuntimeStatus = "";
    public string JsRuntimeStatus { get => _jsRuntimeStatus; private set => Set(ref _jsRuntimeStatus, value); }

    private bool _ytInstalling;
    private string _ytProgress = "";
    public string YtDlpProgress { get => _ytProgress; private set => Set(ref _ytProgress, value); }

    private void RefreshYtDlpStatus()
    {
        var path = YoutubeExtractor.LocateYtDlp(_main.DataDirectory, string.IsNullOrWhiteSpace(_ytDlpPath) ? null : _ytDlpPath.Trim());
        YtDlpFound = path != null;
        YtDlpStatus = path == null
            ? (string.IsNullOrWhiteSpace(_ytDlpPath) ? "yt-dlp não encontrado — vídeos do YouTube indisponíveis" : "Arquivo não encontrado no caminho informado")
            : "yt-dlp: " + path;
        var rt = YoutubeExtractor.LocateJsRuntime(_main.DataDirectory);
        JsRuntimeStatus = rt == null
            ? "Runtime JS: nenhum (o Deno é instalado junto com o yt-dlp)"
            : $"Runtime JS: {rt.Name} — {rt.Path}";
    }

    private async Task InstallYtDlpAsync()
    {
        _ytInstalling = true;
        try
        {
            YtDlpProgress = "Baixando…";
            await _main.InstallYoutubeToolsAsync(new Progress<string>(t => YtDlpProgress = t), CancellationToken.None);
            YtDlpProgress = "";
            YtDlpPath = "";
        }
        catch (Exception ex)
        {
            YtDlpProgress = "Falha: " + ex.Message;
        }
        finally
        {
            _ytInstalling = false;
            RefreshYtDlpStatus();
        }
    }

    private void BrowseYtDlp()
    {
        var dlg = new OpenFileDialog { Title = "Escolher yt-dlp.exe", Filter = "yt-dlp|yt-dlp.exe|Executáveis|*.exe", CheckFileExists = true };
        if (dlg.ShowDialog() == true) YtDlpPath = dlg.FileName;
    }

    // ------------------------------------------------------------ streams (HLS/DASH)
    public ICommand InstallFfmpegCommand { get; }
    public ICommand BrowseFfmpegCommand { get; }

    private double _streamConnections;
    public double StreamConnections
    {
        get => _streamConnections;
        set { if (Set(ref _streamConnections, Math.Round(value))) OnPropertyChanged(nameof(StreamConnectionsText)); }
    }
    public string StreamConnectionsText => $"{(int)_streamConnections} segmentos em paralelo";

    private string _ffmpegPath;
    /// <summary>Caminho manual do ffmpeg.exe (vazio = automático: pasta de ferramentas do Velox, PATH ou locais comuns).</summary>
    public string FfmpegPath
    {
        get => _ffmpegPath;
        set { if (Set(ref _ffmpegPath, value)) RefreshFfmpegStatus(); }
    }

    private bool _ffmpegFound;
    public bool FfmpegFound { get => _ffmpegFound; private set => Set(ref _ffmpegFound, value); }

    private string _ffmpegStatus = "";
    public string FfmpegStatus { get => _ffmpegStatus; private set => Set(ref _ffmpegStatus, value); }

    private bool _ffmpegInstalling;
    private string _ffmpegProgress = "";
    public string FfmpegProgress { get => _ffmpegProgress; private set => Set(ref _ffmpegProgress, value); }

    private void RefreshFfmpegStatus()
    {
        var path = Ffmpeg.Locate(_main.DataDirectory, string.IsNullOrWhiteSpace(_ffmpegPath) ? null : _ffmpegPath.Trim());
        FfmpegFound = path != null;
        FfmpegStatus = path == null
            ? (string.IsNullOrWhiteSpace(_ffmpegPath) ? "ffmpeg não encontrado — streams serão salvos como .ts" : "Arquivo não encontrado no caminho informado")
            : "ffmpeg: " + path;
    }

    private async Task InstallFfmpegAsync()
    {
        _ffmpegInstalling = true;
        try
        {
            FfmpegProgress = "Baixando…";
            var progress = new Progress<(long Done, long Total)>(p =>
                FfmpegProgress = p.Total > 0
                    ? $"Baixando… {FormatHelper.Bytes(p.Done)} de {FormatHelper.Bytes(p.Total)} ({p.Done * 100 / p.Total}%)"
                    : $"Baixando… {FormatHelper.Bytes(p.Done)}");
            await _main.InstallFfmpegAsync(progress, CancellationToken.None);
            FfmpegProgress = "";
            FfmpegPath = "";
        }
        catch (Exception ex)
        {
            FfmpegProgress = "Falha: " + ex.Message;
        }
        finally
        {
            _ffmpegInstalling = false;
            RefreshFfmpegStatus();
        }
    }

    private void BrowseFfmpeg()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Escolher ffmpeg.exe",
            Filter = "ffmpeg|ffmpeg.exe|Executáveis|*.exe",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true) FfmpegPath = dlg.FileName;
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand ResetUserAgentCommand { get; }
    public ICommand InstallIntegrationCommand { get; }
    public ICommand OpenExtensionFolderCommand { get; }
    public ICommand OpenChromeExtensionsCommand { get; }
    public ICommand OpenEdgeExtensionsCommand { get; }

    // ------------------------------------------------------------ navegador
    private bool _browserAsk;
    public bool BrowserAskBeforeDownload { get => _browserAsk; set => Set(ref _browserAsk, value); }

    private bool _autoResolve;
    public bool AutoResolveShortLinks { get => _autoResolve; set => Set(ref _autoResolve, value); }

    private bool _integrationRegistered;
    public bool IntegrationRegistered { get => _integrationRegistered; private set => Set(ref _integrationRegistered, value); }

    private string _integrationStatus = "";
    public string IntegrationStatus { get => _integrationStatus; private set => Set(ref _integrationStatus, value); }

    public string ExtensionPath => Services.BrowserIntegration.ExtensionDirectory;

    private void RefreshIntegrationStatus()
    {
        IntegrationRegistered = Services.BrowserIntegration.IsRegistered(_main.DataDirectory);
        IntegrationStatus = IntegrationRegistered
            ? "Integração registrada para Chrome, Edge, Brave, Vivaldi e Opera."
            : "Integração ainda não registrada neste computador.";
        if (!Services.BrowserIntegration.ExtensionFilesPresent)
            IntegrationStatus += " Pasta da extensão não encontrada ao lado do executável.";
    }

    private void InstallIntegration()
    {
        if (Services.BrowserIntegration.Register(_main.DataDirectory, out var error))
            _main.Toast("Integração instalada", "Agora carregue a extensão no navegador (passo 2).", "success");
        else
            _main.Toast("Falha ao registrar integração", error, "error");
        RefreshIntegrationStatus();
    }

    private void OpenExtensionsPage(string exe, string url)
    {
        if (!Services.BrowserIntegration.OpenExtensionsPage(exe, url))
            _main.Toast("Navegador não encontrado", $"Abra manualmente {url} no navegador.", "error");
    }

    private string _downloadDirectory;
    public string DownloadDirectory { get => _downloadDirectory; set => Set(ref _downloadDirectory, value); }

    private bool _organizeByCategory;
    public bool OrganizeByCategory { get => _organizeByCategory; set => Set(ref _organizeByCategory, value); }

    private double _maxConcurrent;
    public double MaxConcurrent
    {
        get => _maxConcurrent;
        set { if (Set(ref _maxConcurrent, Math.Round(value))) OnPropertyChanged(nameof(MaxConcurrentText)); }
    }
    public string MaxConcurrentText => $"{(int)_maxConcurrent} simultâneo(s)";

    private double _connections;
    public double Connections
    {
        get => _connections;
        set { if (Set(ref _connections, Math.Round(value))) OnPropertyChanged(nameof(ConnectionsText)); }
    }
    public string ConnectionsText => $"{(int)_connections} conexões por arquivo";

    private bool _speedLimitEnabled;
    public bool SpeedLimitEnabled { get => _speedLimitEnabled; set => Set(ref _speedLimitEnabled, value); }

    private int _speedLimitKb;
    public int SpeedLimitKb
    {
        get => _speedLimitKb;
        set { if (Set(ref _speedLimitKb, Math.Max(0, value))) OnPropertyChanged(nameof(SpeedLimitPreview)); }
    }
    public string SpeedLimitPreview => _speedLimitKb <= 0 ? "" : "= " + FormatHelper.Speed(_speedLimitKb * 1024L);

    private bool _monitorClipboard;
    public bool MonitorClipboard { get => _monitorClipboard; set => Set(ref _monitorClipboard, value); }

    private bool _captureAnyUrl;
    public bool CaptureAnyUrl { get => _captureAnyUrl; set => Set(ref _captureAnyUrl, value); }

    private bool _minimizeToTray;
    public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }

    private bool _closeToTray;
    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }

    private bool _startMinimized;
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

    private bool _autoResume;
    public bool AutoResume { get => _autoResume; set => Set(ref _autoResume, value); }

    private bool _autoRetry;
    public bool AutoRetry { get => _autoRetry; set => Set(ref _autoRetry, value); }

    private double _maxAutoRetries;
    public double MaxAutoRetries
    {
        get => _maxAutoRetries;
        set { if (Set(ref _maxAutoRetries, Math.Round(value))) OnPropertyChanged(nameof(MaxAutoRetriesText)); }
    }
    public string MaxAutoRetriesText => $"{(int)_maxAutoRetries} tentativa(s) por download";

    private double _maxRetriesPerSegment;
    public double MaxRetriesPerSegment
    {
        get => _maxRetriesPerSegment;
        set { if (Set(ref _maxRetriesPerSegment, Math.Round(value))) OnPropertyChanged(nameof(MaxRetriesPerSegmentText)); }
    }
    public string MaxRetriesPerSegmentText => $"{(int)_maxRetriesPerSegment} tentativas por conexão";

    private double _readTimeout;
    public double ReadTimeout
    {
        get => _readTimeout;
        set { if (Set(ref _readTimeout, Math.Round(value))) OnPropertyChanged(nameof(ReadTimeoutText)); }
    }
    public string ReadTimeoutText => $"{(int)_readTimeout} s sem resposta";

    private string _userAgent;
    public string UserAgent { get => _userAgent; set => Set(ref _userAgent, value); }

    private string _proxyUrl;
    public string ProxyUrl { get => _proxyUrl; set => Set(ref _proxyUrl, value); }

    private bool _verifyHash;
    public bool VerifyHash { get => _verifyHash; set => Set(ref _verifyHash, value); }

    private bool _showNotifications;
    public bool ShowNotifications { get => _showNotifications; set => Set(ref _showNotifications, value); }

    private bool _startImmediately;
    public bool StartImmediately { get => _startImmediately; set => Set(ref _startImmediately, value); }

    private void Browse()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pasta padrão de downloads",
            InitialDirectory = System.IO.Directory.Exists(_downloadDirectory) ? _downloadDirectory : null
        };
        if (dlg.ShowDialog() == true) DownloadDirectory = dlg.FolderName;
    }

    private void Save()
    {
        var s = _original.Clone();
        s.DownloadDirectory = string.IsNullOrWhiteSpace(_downloadDirectory) ? AppSettings.GetDefaultDownloadsFolder() : _downloadDirectory.Trim();
        s.OrganizeByCategory = _organizeByCategory;
        s.MaxConcurrentDownloads = (int)Math.Clamp(_maxConcurrent, 1, 10);
        s.ConnectionsPerDownload = (int)Math.Clamp(_connections, 1, 64);
        s.SpeedLimitEnabled = _speedLimitEnabled;
        s.SpeedLimitBytesPerSecond = Math.Max(16, _speedLimitKb) * 1024L;
        s.MonitorClipboard = _monitorClipboard;
        s.CaptureAnyUrl = _captureAnyUrl;
        s.MinimizeToTray = _minimizeToTray;
        s.CloseToTray = _closeToTray;
        s.StartMinimized = _startMinimized;
        s.StartWithWindows = _startWithWindows;
        s.AutoResumeOnStartup = _autoResume;
        s.AutoRetryFailed = _autoRetry;
        s.MaxAutoRetries = (int)_maxAutoRetries;
        s.MaxRetriesPerSegment = (int)_maxRetriesPerSegment;
        s.ReadTimeoutSeconds = (int)_readTimeout;
        s.UserAgent = string.IsNullOrWhiteSpace(_userAgent) ? new AppSettings().UserAgent : _userAgent.Trim();
        s.ProxyUrl = string.IsNullOrWhiteSpace(_proxyUrl) ? null : _proxyUrl.Trim();
        s.VerifyHashOnComplete = _verifyHash;
        s.ShowNotifications = _showNotifications;
        s.StartDownloadsImmediately = _startImmediately;
        s.BrowserAskBeforeDownload = _browserAsk;
        s.AutoResolveShortLinks = _autoResolve;
        s.StreamConnections = (int)Math.Clamp(_streamConnections, 1, 16);
        s.FfmpegPath = string.IsNullOrWhiteSpace(_ffmpegPath) ? null : _ffmpegPath.Trim();
        s.YtDlpPath = string.IsNullOrWhiteSpace(_ytDlpPath) ? null : _ytDlpPath.Trim();

        ApplyStartup(s.StartWithWindows);
        _main.ApplySettings(s);
        RequestClose?.Invoke(true);
    }

    private static void ApplyStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue("VeloxDM", $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue("VeloxDM", throwOnMissingValue: false);
            }
        }
        catch { }
    }
}
