using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Velox.App.Services;
using Velox.App.ViewModels;

namespace Velox.App.Views;

public partial class MainWindow : Window, IUiService
{
    private readonly MainViewModel _vm;
    private ClipboardMonitor? _clipboard;
    private TrayService? _tray;
    private bool _exiting;
    private AddDownloadWindow? _addDialog;
    private BypassWindow? _bypassDialog;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(App.Manager, this);
        DataContext = _vm;

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Loaded += OnLoaded;

        DragEnter += OnDragEnter;
        DragOver += OnDragEnter;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    // ------------------------------------------------------------ ciclo de vida
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowEffects.Apply(this);

        _clipboard = new ClipboardMonitor(this)
        {
            Enabled = App.Manager.Settings.MonitorClipboard,
            CaptureAnyUrl = App.Manager.Settings.CaptureAnyUrl
        };
        _clipboard.UrlCopied += OnUrlCopied;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _tray = new TrayService { NotificationsEnabled = App.Manager.Settings.ShowNotifications };
        _tray.OpenRequested += ShowFromTray;
        _tray.AddRequested += () => { ShowFromTray(); OpenAddDialog(null); };
        _tray.PauseAllRequested += () => App.Manager.PauseAll();
        _tray.ResumeAllRequested += () => App.Manager.ResumeAll();
        _tray.ExitRequested += ExitApplication;

        App.Manager.Tick += () => Dispatcher.BeginInvoke(() =>
        {
            if (_tray == null) return;
            _tray.SetStatus(_vm.ActiveCount > 0 ? $"{_vm.ActiveCount} baixando • {_vm.TotalSpeedText}" : "ocioso");
        });
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Log.Info($"StateChanged: {WindowState}");
        // janela sem borda extrapola a tela ao maximizar; compensa com margem
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";

        if (WindowState == WindowState.Minimized && App.Manager.Settings.MinimizeToTray)
        {
            Hide();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Log.Info("Closing solicitado");
        if (_exiting) return;

        if (App.Manager.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (_vm.ActiveCount > 0)
                _tray?.Notify("Velox continua em segundo plano", $"{_vm.ActiveCount} download(s) em andamento.");
            return;
        }

        ExitApplication();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            _clipboard?.Dispose();
            _tray?.Dispose();
        }
        catch { }
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------ barra de título
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------ lista
    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item != null) item.IsSelected = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is DownloadItemViewModel vm)
        {
            if (vm.IsCompleted) vm.OpenFileCommand.Execute(null);
            else vm.ToggleCommand.Execute(null);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ------------------------------------------------------------ arrastar & soltar
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        var urls = ExtractDropUrls(e.Data);
        if (urls.Count > 0)
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var urls = ExtractDropUrls(e.Data);
        if (urls.Count > 0) OpenAddDialog(urls);
        e.Handled = true;
    }

    private static List<string> ExtractDropUrls(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is System.IO.MemoryStream ms)
            {
                var text = System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0');
                var u = UrlHelper.ExtractUrls(text);
                if (u.Count > 0) return u;
            }
            if (data.GetDataPresent(DataFormats.UnicodeText))
                return UrlHelper.ExtractUrls((string)data.GetData(DataFormats.UnicodeText)!);
            if (data.GetDataPresent(DataFormats.Text))
                return UrlHelper.ExtractUrls((string)data.GetData(DataFormats.Text)!);
        }
        catch { }
        return new List<string>();
    }

    // ------------------------------------------------------------ clipboard
    private void OnUrlCopied(string url)
    {
        if (_addDialog != null && _addDialog.IsVisible) return;
        ShowFromTray();
        OpenAddDialog(new[] { url });
    }

    // ------------------------------------------------------------ ponte com o navegador
    /// <summary>Chamado (na thread de UI) para cada mensagem vinda da extensão.</summary>
    public string HandleBridgeMessage(string json)
    {
        var msg = BrowserMessage.Parse(json);
        if (msg == null) return BrowserMessage.Error("Mensagem inválida.");

        switch (msg.Type)
        {
            case "ping":
                return BrowserMessage.Ok(new { ok = true, running = true, version = "1.0" });

            case "show":
                ShowFromTray();
                return BrowserMessage.Ok();

            case "bypass":
                if (string.IsNullOrWhiteSpace(msg.Url) || !UrlHelper.IsHttpUrl(msg.Url))
                    return BrowserMessage.Error("Endereço inválido.");
                Log.Info($"Decifrar link (navegador): {msg.Url}");
                ShowBypassDialog(msg.Url.Trim());
                return BrowserMessage.Ok();

            case "download":
                if (string.IsNullOrWhiteSpace(msg.Url) || !UrlHelper.IsHttpUrl(msg.Url))
                    return BrowserMessage.Error("Endereço inválido.");
                Log.Info($"Download capturado do navegador: {msg.Url}");
                _vm.HandleBrowserDownload(msg);
                return BrowserMessage.Ok();

            default:
                return BrowserMessage.Error("Tipo de mensagem desconhecido.");
        }
    }

    // ------------------------------------------------------------ IUiService
    public void OpenAddDialog(IReadOnlyList<string>? urls) => ShowAddDialog(urls);

    public void ShowMainWindow() => ShowFromTray();

    public void ShowBypassDialog(string? url)
    {
        if (url != null) ShowFromTray();
        if (_bypassDialog != null && _bypassDialog.IsVisible)
        {
            if (!string.IsNullOrWhiteSpace(url)) _bypassDialog.SetUrl(url);
            _bypassDialog.Activate();
            return;
        }

        _bypassDialog = new BypassWindow(_vm, url) { Owner = IsVisible ? this : null };
        _bypassDialog.Closed += (_, _) => _bypassDialog = null;
        _bypassDialog.Show();
    }

    public void ShowAddDialogPrefilled(Velox.Core.Models.DownloadRequest request, string sourceLabel)
    {
        ShowFromTray();
        if (_addDialog != null && _addDialog.IsVisible)
        {
            _addDialog.ApplyPrefill(request, sourceLabel);
            _addDialog.Activate();
            return;
        }

        _addDialog = new AddDownloadWindow(_vm, null, request, sourceLabel) { Owner = IsVisible ? this : null };
        _addDialog.Closed += (_, _) => _addDialog = null;
        _addDialog.Show();
    }

    public void ShowAddDialog(IReadOnlyList<string>? urls = null)
    {
        if (_addDialog != null && _addDialog.IsVisible)
        {
            if (urls != null && urls.Count > 0) _addDialog.ApplyUrls(urls);
            _addDialog.Activate();
            return;
        }

        _addDialog = new AddDownloadWindow(_vm, urls) { Owner = IsVisible ? this : null };
        _addDialog.Closed += (_, _) => _addDialog = null;
        _addDialog.Show();
    }

    public void ShowSettings()
    {
        var dlg = new SettingsWindow(_vm) { Owner = this };
        dlg.ShowDialog();

        var s = App.Manager.Settings;
        if (_clipboard != null)
        {
            _clipboard.Enabled = s.MonitorClipboard;
            _clipboard.CaptureAnyUrl = s.CaptureAnyUrl;
        }
        if (_tray != null) _tray.NotificationsEnabled = s.ShowNotifications;
    }

    public bool Confirm(string title, string message, string confirmLabel = "Confirmar", bool danger = false)
    {
        var dlg = new MessageWindow(title, message, confirmLabel, danger) { Owner = IsVisible ? this : null };
        return dlg.ShowDialog() == true;
    }

    public void TrayNotify(string title, string message, bool error = false)
    {
        if (!App.Manager.Settings.ShowNotifications) return;
        if (IsActive && !error) return; // já mostra toast in-app quando a janela está em foco
        _tray?.Notify(title, message, error);
    }

    public void SetClipboardIgnore(string text) => _clipboard?.Ignore(text);
}
