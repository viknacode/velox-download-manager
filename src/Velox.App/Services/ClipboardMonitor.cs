using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Velox.Core.Services;

namespace Velox.App.Services;

/// <summary>Observa a área de transferência e dispara quando um link de download é copiado.</summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly HwndSource _source;
    private string? _last;
    private bool _disposed;

    public bool Enabled { get; set; } = true;
    public bool CaptureAnyUrl { get; set; }

    public event Action<string>? UrlCopied;

    public ClipboardMonitor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("HwndSource indisponível.");
        _source.AddHook(Hook);
        AddClipboardFormatListener(hwnd);
    }

    /// <summary>Evita disparar para um texto que o próprio app colocou na área de transferência.</summary>
    public void Ignore(string text) => _last = text;

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && Enabled)
        {
            // pequeno atraso: quem copiou pode ainda estar segurando o clipboard
            _source.Dispatcher.BeginInvoke(new Action(CheckClipboard), System.Windows.Threading.DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void CheckClipboard()
    {
        string? text = null;
        for (int i = 0; i < 3 && text == null; i++)
        {
            try
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
                else return;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }

        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text == _last || text.Length > 4000) return;
        _last = text;

        if (UrlHelper.IsDownloadUrl(text, CaptureAnyUrl))
            UrlCopied?.Invoke(text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(Hook);
        }
        catch { }
    }
}

public static class UrlHelper
{
    public static bool IsHttpUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static bool IsDownloadUrl(string text, bool anyUrl)
    {
        if (text.Contains('\n') || text.Contains(' ')) return false;
        if (!IsHttpUrl(text)) return false;
        return anyUrl || CategoryDetector.HasKnownExtension(text);
    }

    public static List<string> ExtractUrls(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim().Trim('"', '<', '>');
            if (IsHttpUrl(t) && !result.Contains(t)) result.Add(t);
        }
        return result;
    }
}
