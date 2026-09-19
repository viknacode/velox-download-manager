using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Velox.Core.Models;
using Velox.Core.Resolvers;
using Velox.Core.Services;
using Velox.Core.Utils;

namespace Velox.App.ViewModels;

public interface IUiService
{
    void ShowAddDialog(IReadOnlyList<string>? urls = null);
    void ShowAddDialogPrefilled(DownloadRequest request, string sourceLabel);
    void ShowBypassDialog(string? url);
    void ShowMainWindow();
    void ShowSettings();
    bool Confirm(string title, string message, string confirmLabel = "Confirmar", bool danger = false);
    void TrayNotify(string title, string message, bool error = false);
    void SetClipboardIgnore(string text);
    void ExitApplication();
}

public sealed class NavItem : ObservableObject
{
    public NavItem(string key, string label, string glyph, bool isCategory = false)
    {
        Key = key;
        Label = label;
        Glyph = glyph;
        IsCategory = isCategory;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public bool IsCategory { get; }

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value)) OnPropertyChanged(nameof(BadgeVisibility));
        }
    }

    public Visibility BadgeVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class ToastItem : ObservableObject
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Kind { get; init; } = "info"; // info | success | error
    public string Glyph => Kind switch { "success" => "", "error" => "", _ => "" };
    public ICommand? CloseCommand { get; set; }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly IUiService _ui;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<Guid, DownloadItemViewModel> _index = new();
    private readonly LinkResolverService _resolver;

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<NavItem> StatusFilters { get; } = new();
    public ObservableCollection<NavItem> CategoryFilters { get; } = new();
    public ObservableCollection<ToastItem> Toasts { get; } = new();

    public MainViewModel(DownloadManager manager, IUiService ui)
    {
        _manager = manager;
        _ui = ui;
        _dispatcher = Application.Current.Dispatcher;
        _resolver = new LinkResolverService(() => new ResolverHttp(_manager.Settings.UserAgent, _manager.Settings.ProxyUrl));

        StatusFilters.Add(new NavItem("all", "Todos", ""));
        StatusFilters.Add(new NavItem("active", "Baixando", ""));
        StatusFilters.Add(new NavItem("queued", "Na fila", ""));
        StatusFilters.Add(new NavItem("paused", "Pausados", ""));
        StatusFilters.Add(new NavItem("completed", "Concluídos", ""));
        StatusFilters.Add(new NavItem("failed", "Falhas", ""));

        foreach (var c in CategoryDetector.All)
            CategoryFilters.Add(new NavItem("cat:" + c, c, DownloadItemViewModel.GlyphFor(c), true));

        _selectedFilter = StatusFilters[0];
        _selectedFilter.IsSelected = true;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(DownloadItemViewModel.CreatedAt), ListSortDirection.Descending));
        ItemsView.Filter = FilterItem;

        foreach (var item in _manager.GetItems())
            AddVm(item);

        _manager.ItemAdded += i => OnUi(() => { AddVm(i); RefreshView(); Toast("Download adicionado", i.FileName); });
        _manager.ItemRemoved += i => OnUi(() => { RemoveVm(i); RefreshView(); });
        _manager.ItemStatusChanged += i => OnUi(() => OnStatusChanged(i));
        _manager.ItemCompleted += i => OnUi(() => OnCompleted(i));
        _manager.ItemFailed += i => OnUi(() => OnFailed(i));
        _manager.Tick += () => OnUi(OnTick);

        AddDownloadCommand = new RelayCommand(() => _ui.ShowAddDialog());
        OpenSettingsCommand = new RelayCommand(() => _ui.ShowSettings());
        PauseAllCommand = new RelayCommand(() => _manager.PauseAll());
        ResumeAllCommand = new RelayCommand(() => _manager.ResumeAll());
        ClearCompletedCommand = new RelayCommand(ClearCompleted);
        SelectFilterCommand = new RelayCommand(p => { if (p is NavItem n) SelectedFilter = n; });
        CloseDetailsCommand = new RelayCommand(() => SelectedItem = null);
        RemoveSelectedCommand = new RelayCommand(() => { if (SelectedItem != null) Remove(SelectedItem, false); });
        ToggleSelectedCommand = new RelayCommand(() => { if (SelectedItem != null) Toggle(SelectedItem); });
        PasteCommand = new RelayCommand(PasteFromClipboard);
        OpenBypassCommand = new RelayCommand(() => _ui.ShowBypassDialog(null));
        ExitCommand = new RelayCommand(() => _ui.ExitApplication());

        _speedLimitEnabled = _manager.Settings.SpeedLimitEnabled;
        UpdateCounts();
        UpdateTotals();
        RefreshSpeedLimitText();
    }

    // ------------------------------------------------------------ comandos
    public ICommand AddDownloadCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand SelectFilterCommand { get; }
    public ICommand CloseDetailsCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ToggleSelectedCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand OpenBypassCommand { get; }
    public ICommand ExitCommand { get; }

    // ------------------------------------------------------------ estado
    private NavItem _selectedFilter;
    public NavItem SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value == null || ReferenceEquals(_selectedFilter, value)) return;
            _selectedFilter.IsSelected = false;
            _selectedFilter = value;
            _selectedFilter.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilterTitle));
            RefreshView();
        }
    }

    public string FilterTitle => _selectedFilter.Label;

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) RefreshView();
        }
    }

    private DownloadItemViewModel? _selectedItem;
    public DownloadItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value))
            {
                value?.Refresh(true);
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selectedItem != null;

    private bool _isEmpty;
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    private string _visibleCountText = "";
    public string VisibleCountText { get => _visibleCountText; private set => Set(ref _visibleCountText, value); }

    private string _totalSpeedText = "0 B/s";
    public string TotalSpeedText { get => _totalSpeedText; private set => Set(ref _totalSpeedText, value); }

    private string _statusBarText = "";
    public string StatusBarText { get => _statusBarText; private set => Set(ref _statusBarText, value); }

    private double[] _globalHistory = Array.Empty<double>();
    public double[] GlobalHistory { get => _globalHistory; private set => Set(ref _globalHistory, value); }

    private bool _speedLimitEnabled;
    public bool SpeedLimitEnabled
    {
        get => _speedLimitEnabled;
        set
        {
            if (Set(ref _speedLimitEnabled, value))
            {
                _manager.SetSpeedLimit(value);
                RefreshSpeedLimitText();
            }
        }
    }

    private string _speedLimitText = "";
    public string SpeedLimitText { get => _speedLimitText; private set => Set(ref _speedLimitText, value); }

    private int _activeCount;
    public int ActiveCount { get => _activeCount; private set => Set(ref _activeCount, value); }

    public AppSettings Settings => _manager.Settings;
    public string DataDirectory => _manager.DataDirectory;

    // ------------------------------------------------------------ ações
    public void Toggle(DownloadItemViewModel vm)
    {
        var m = vm.Model;
        if (m.IsActive || m.Status == DownloadStatus.Queued) _manager.Pause(m.Id);
        else if (m.Status != DownloadStatus.Completed) _manager.Resume(m.Id);
    }

    public async void Remove(DownloadItemViewModel vm, bool deleteFile)
    {
        var m = vm.Model;
        if (deleteFile)
        {
            if (!_ui.Confirm("Apagar arquivo", $"Remover \"{m.FileName}\" da lista e apagar o arquivo do disco?", "Apagar", danger: true))
                return;
        }
        else if (m.Status != DownloadStatus.Completed && m.DownloadedBytes > 0)
        {
            if (!_ui.Confirm("Remover download", $"Remover \"{m.FileName}\"? O progresso parcial será descartado.", "Remover", danger: true))
                return;
        }

        if (ReferenceEquals(SelectedItem, vm)) SelectedItem = null;
        await _manager.RemoveAsync(m.Id, deleteFile);
    }

    public async void Restart(DownloadItemViewModel vm)
    {
        if (!_ui.Confirm("Reiniciar download", $"Baixar \"{vm.Model.FileName}\" novamente do zero?", "Reiniciar"))
            return;
        await _manager.RestartAsync(vm.Model.Id);
    }

    public void CopyUrl(DownloadItemViewModel vm)
    {
        try
        {
            _ui.SetClipboardIgnore(vm.Model.Url);
            Clipboard.SetText(vm.Model.Url);
            Toast("Link copiado", vm.Model.Url, "success");
        }
        catch { }
    }

    private async void ClearCompleted()
    {
        int n = Items.Count(i => i.IsCompleted);
        if (n == 0) return;
        if (!_ui.Confirm("Limpar concluídos", $"Remover {n} download(s) concluído(s) da lista? Os arquivos não serão apagados.", "Limpar"))
            return;
        await _manager.ClearCompletedAsync();
    }

    private void PasteFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var urls = Services.UrlHelper.ExtractUrls(Clipboard.GetText());
            if (urls.Count > 0) _ui.ShowAddDialog(urls);
            else _ui.ShowAddDialog();
        }
        catch
        {
            _ui.ShowAddDialog();
        }
    }

    public void AddDownloads(IEnumerable<DownloadRequest> requests)
    {
        foreach (var r in requests)
        {
            try { _manager.Add(r); }
            catch (Exception ex) { Toast("Erro ao adicionar", ex.Message, "error"); }
        }
    }

    public Task<ProbeResult> ProbeAsync(string url, IDictionary<string, string>? headers, string? referer, CancellationToken ct)
        => _manager.ProbeAsync(url, headers, referer, ct);

    /// <summary>Decifra encurtadores/safelinks até a URL de destino (ver Velox.Core.Resolvers).</summary>
    public Task<ResolveResult> ResolveLinkAsync(string url, Action<string>? log, CancellationToken ct)
        => _resolver.ResolveAsync(url, log, ct);

    public void RequestAddDialog(DownloadRequest request, string sourceLabel) => _ui.ShowAddDialogPrefilled(request, sourceLabel);

    /// <summary>Download interceptado pela extensão do navegador (cookies e User-Agent da sessão incluídos).</summary>
    public void HandleBrowserDownload(Services.BrowserMessage msg)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(msg.Cookies)) headers["Cookie"] = msg.Cookies.Trim();
        if (!string.IsNullOrWhiteSpace(msg.UserAgent)) headers["User-Agent"] = msg.UserAgent.Trim();

        var fileName = string.IsNullOrWhiteSpace(msg.FileName) ? null : FileNameHelper.Sanitize(msg.FileName);
        var request = new DownloadRequest
        {
            Url = msg.Url!.Trim(),
            FileName = fileName,
            Referer = string.IsNullOrWhiteSpace(msg.Referrer) ? null : msg.Referrer.Trim(),
            Headers = headers.Count > 0 ? headers : null,
            StartImmediately = Settings.StartDownloadsImmediately
        };

        var source = msg.Source switch
        {
            "edge" => "Capturado do Edge",
            "chrome" => "Capturado do Chrome",
            _ => "Capturado do navegador"
        };

        if (Settings.BrowserAskBeforeDownload)
        {
            _ui.ShowAddDialogPrefilled(request, source);
        }
        else
        {
            AddDownloads(new[] { request });
            _ui.TrayNotify(source, fileName ?? request.Url);
        }
    }

    public void ApplySettings(AppSettings s)
    {
        _manager.UpdateSettings(s);
        _speedLimitEnabled = s.SpeedLimitEnabled;
        OnPropertyChanged(nameof(SpeedLimitEnabled));
        OnPropertyChanged(nameof(Settings));
        RefreshSpeedLimitText();
    }

    public void Toast(string title, string message, string kind = "info")
    {
        var t = new ToastItem { Title = title, Message = message, Kind = kind };
        t.CloseCommand = new RelayCommand(() => Toasts.Remove(t));
        Toasts.Insert(0, t);
        while (Toasts.Count > 4) Toasts.RemoveAt(Toasts.Count - 1);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == "error" ? 8 : 4.5) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
        timer.Start();
    }

    // ------------------------------------------------------------ interno
    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    private void AddVm(DownloadItem item)
    {
        if (_index.ContainsKey(item.Id)) return;
        var vm = new DownloadItemViewModel(item, this);
        _index[item.Id] = vm;
        Items.Add(vm);
        UpdateCounts();
    }

    private void RemoveVm(DownloadItem item)
    {
        if (_index.Remove(item.Id, out var vm))
        {
            Items.Remove(vm);
            if (ReferenceEquals(SelectedItem, vm)) SelectedItem = null;
        }
        UpdateCounts();
    }

    private void OnStatusChanged(DownloadItem item)
    {
        if (_index.TryGetValue(item.Id, out var vm))
            vm.Refresh(ReferenceEquals(vm, SelectedItem));
        UpdateCounts();
        RefreshView();
    }

    private void OnCompleted(DownloadItem item)
    {
        Toast("Download concluído", item.FileName, "success");
        _ui.TrayNotify("Download concluído", item.FileName);
    }

    private void OnFailed(DownloadItem item)
    {
        Toast("Download falhou", $"{item.FileName}\n{item.ErrorMessage}", "error");
        _ui.TrayNotify("Download falhou", $"{item.FileName}: {item.ErrorMessage}", error: true);
    }

    private void OnTick()
    {
        foreach (var vm in Items)
        {
            if (vm.Model.IsActive || vm.IsActive)
                vm.Refresh(ReferenceEquals(vm, SelectedItem));
        }
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        TotalSpeedText = FormatHelper.Speed(_manager.TotalSpeed);
        GlobalHistory = _manager.GetGlobalSpeedHistory();
        ActiveCount = Items.Count(i => i.Model.IsActive);

        int queued = Items.Count(i => i.Model.Status == DownloadStatus.Queued);
        int done = Items.Count(i => i.IsCompleted);
        StatusBarText = $"{ActiveCount} ativo(s)  •  {queued} na fila  •  {done} concluído(s)";
    }

    private void UpdateCounts()
    {
        foreach (var f in StatusFilters)
            f.Count = f.Key switch
            {
                "all" => Items.Count,
                "active" => Items.Count(i => i.Model.IsActive),
                "queued" => Items.Count(i => i.Model.Status == DownloadStatus.Queued),
                "paused" => Items.Count(i => i.Model.Status == DownloadStatus.Paused),
                "completed" => Items.Count(i => i.Model.Status == DownloadStatus.Completed),
                "failed" => Items.Count(i => i.Model.Status == DownloadStatus.Failed),
                _ => 0
            };

        foreach (var c in CategoryFilters)
            c.Count = Items.Count(i => i.Model.Category == c.Label);
    }

    private bool FilterItem(object o)
    {
        if (o is not DownloadItemViewModel vm) return false;
        var m = vm.Model;

        bool ok = _selectedFilter.Key switch
        {
            "all" => true,
            "active" => m.IsActive,
            "queued" => m.Status == DownloadStatus.Queued,
            "paused" => m.Status == DownloadStatus.Paused,
            "completed" => m.Status == DownloadStatus.Completed,
            "failed" => m.Status == DownloadStatus.Failed,
            _ => _selectedFilter.IsCategory && m.Category == _selectedFilter.Label
        };
        if (!ok) return false;

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var q = _searchText.Trim();
            return m.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   m.Url.Contains(q, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    private void RefreshView()
    {
        ItemsView.Refresh();
        int visible = ItemsView.Cast<object>().Count();
        IsEmpty = visible == 0;
        VisibleCountText = visible == 1 ? "1 item" : $"{visible} itens";
        UpdateTotals();
    }

    private void RefreshSpeedLimitText()
    {
        var s = _manager.Settings;
        SpeedLimitText = s.SpeedLimitEnabled
            ? $"Limite: {FormatHelper.Speed(s.SpeedLimitBytesPerSecond)}"
            : $"Limitar a {FormatHelper.Speed(s.SpeedLimitBytesPerSecond)}";
    }
}
