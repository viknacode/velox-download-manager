namespace Velox.Core.Models;

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = GetDefaultDownloadsFolder();
    public bool OrganizeByCategory { get; set; } = false;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int ConnectionsPerDownload { get; set; } = 16;
    public bool SpeedLimitEnabled { get; set; } = false;
    public long SpeedLimitBytesPerSecond { get; set; } = 2 * 1024 * 1024;
    public bool MonitorClipboard { get; set; } = true;
    public bool CaptureAnyUrl { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool AutoResumeOnStartup { get; set; } = true;
    public bool AutoRetryFailed { get; set; } = true;
    public int MaxAutoRetries { get; set; } = 5;
    public int MaxRetriesPerSegment { get; set; } = 15;
    public int ReadTimeoutSeconds { get; set; } = 30;
    public int MinSplitSizeKB { get; set; } = 512;
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
    public string? ProxyUrl { get; set; }
    public bool VerifyHashOnComplete { get; set; } = false;
    public bool ShowNotifications { get; set; } = true;
    public bool StartDownloadsImmediately { get; set; } = true;
    public bool BrowserAskBeforeDownload { get; set; } = true;
    public bool AutoResolveShortLinks { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public static string GetDefaultDownloadsFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return System.IO.Directory.Exists(downloads) ? downloads : profile;
    }
}
