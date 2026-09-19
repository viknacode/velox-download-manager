using System.Diagnostics;
using Velox.Core.Engine;
using Velox.Core.Models;

namespace Velox.Core.Services;

/// <summary>
/// Orquestra a fila de downloads: concorrência, persistência, limite de banda,
/// estatísticas periódicas e retentativas automáticas.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private readonly object _lock = new();
    private readonly List<DownloadItem> _items = new();
    private readonly Dictionary<Guid, DownloadTask> _tasks = new();
    private readonly StateStore _store;
    private readonly SettingsStore _settingsStore;
    private readonly BandwidthLimiter _limiter = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly double[] _globalHistory = new double[120];
    private int _globalCount, _globalHead;
    private HttpClient _http;
    private Task? _loop;
    private int _savePending;
    private bool _disposed;

    public AppSettings Settings { get; private set; }
    public double TotalSpeed { get; private set; }
    public string DataDirectory { get; }
    public string? SettingsLoadError { get; }

    public event Action<DownloadItem>? ItemAdded;
    public event Action<DownloadItem>? ItemRemoved;
    public event Action<DownloadItem>? ItemStatusChanged;
    public event Action<DownloadItem>? ItemCompleted;
    public event Action<DownloadItem>? ItemFailed;
    public event Action? Tick;

    public DownloadManager(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeloxDM");
        Directory.CreateDirectory(DataDirectory);

        _settingsStore = new SettingsStore(Path.Combine(DataDirectory, "settings.json"));
        _store = new StateStore(Path.Combine(DataDirectory, "downloads.json"));

        Settings = _settingsStore.Load();
        SettingsLoadError = _settingsStore.LoadError;
        _http = HttpClientFactory.Create(Settings);
        ApplyLimiter();

        foreach (var item in _store.Load())
        {
            item.DownloadedBytes = item.GetDownloadedBytes();
            if (item.Status is DownloadStatus.Queued or DownloadStatus.Connecting
                or DownloadStatus.Downloading or DownloadStatus.Verifying)
            {
                item.Status = Settings.AutoResumeOnStartup ? DownloadStatus.Queued : DownloadStatus.Paused;
            }
            _items.Add(item);
        }
    }

    public void Start()
    {
        _loop ??= Task.Run(LoopAsync);
        Pump();
    }

    // ------------------------------------------------------------------ consulta

    public IReadOnlyList<DownloadItem> GetItems()
    {
        lock (_lock) return _items.ToList();
    }

    public DownloadItem? Find(Guid id)
    {
        lock (_lock) return _items.FirstOrDefault(i => i.Id == id);
    }

    public double[] GetGlobalSpeedHistory()
    {
        lock (_globalHistory)
        {
            var r = new double[_globalCount];
            int start = (_globalHead - _globalCount + _globalHistory.Length) % _globalHistory.Length;
            for (int i = 0; i < _globalCount; i++) r[i] = _globalHistory[(start + i) % _globalHistory.Length];
            return r;
        }
    }

    public int ActiveCount
    {
        get { lock (_lock) return _tasks.Count; }
    }

    public Task<ProbeResult> ProbeAsync(string url, IDictionary<string, string>? headers = null,
        string? referer = null, CancellationToken ct = default)
        => UrlProber.ProbeAsync(_http, url, headers, referer, ct);

    // ------------------------------------------------------------------ operações

    public DownloadItem Add(DownloadRequest request)
    {
        var url = request.Url.Trim();
        var dir = string.IsNullOrWhiteSpace(request.Directory) ? Settings.DownloadDirectory : request.Directory!;

        var name = request.FileName;
        if (string.IsNullOrWhiteSpace(name))
            name = request.Probe?.FileName ?? FileNameHelper.FromUrl(url);
        name = FileNameHelper.Sanitize(name!);

        var category = request.Category ?? CategoryDetector.Detect(name, request.Probe?.ContentType);
        if (Settings.OrganizeByCategory && string.IsNullOrWhiteSpace(request.Directory))
            dir = Path.Combine(dir, category);

        lock (_lock)
        {
            name = FileNameHelper.MakeUnique(dir, name,
                full => _items.Any(i => string.Equals(i.FullPath, full, StringComparison.OrdinalIgnoreCase)));
        }

        var item = new DownloadItem
        {
            Url = url,
            FileName = name,
            Directory = dir,
            MaxConnections = Math.Clamp(request.MaxConnections ?? Settings.ConnectionsPerDownload, 1, 64),
            Referer = request.Referer,
            Headers = request.Headers != null ? new Dictionary<string, string>(request.Headers) : new(),
            ExpectedHash = string.IsNullOrWhiteSpace(request.ExpectedHash) ? null : request.ExpectedHash.Trim(),
            Category = category,
            TotalSize = request.Probe?.Size ?? -1,
            SupportsResume = request.Probe?.SupportsResume ?? false,
            ContentType = request.Probe?.ContentType,
            ETag = request.Probe?.ETag,
            Status = request.StartImmediately ? DownloadStatus.Queued : DownloadStatus.Paused
        };

        lock (_lock) _items.Add(item);

        ItemAdded?.Invoke(item);
        RequestSave();
        Pump();
        return item;
    }

    /// <summary>Inicia, retoma ou tenta novamente um download.</summary>
    public void Resume(Guid id)
    {
        var item = Find(id);
        if (item == null) return;

        lock (_lock)
        {
            if (item.IsActive || item.Status is DownloadStatus.Completed or DownloadStatus.Queued) return;
            item.Status = DownloadStatus.Queued;
            item.ErrorMessage = null;
        }

        ItemStatusChanged?.Invoke(item);
        RequestSave();
        Pump();
    }

    public void Pause(Guid id)
    {
        var item = Find(id);
        if (item == null) return;

        DownloadTask? task;
        bool changed = false;
        lock (_lock)
        {
            _tasks.TryGetValue(id, out task);
            if (task == null && item.Status == DownloadStatus.Queued)
            {
                item.Status = DownloadStatus.Paused;
                changed = true;
            }
        }

        task?.Pause();
        if (changed)
        {
            ItemStatusChanged?.Invoke(item);
            RequestSave();
        }
    }

    public async Task RemoveAsync(Guid id, bool deleteFile)
    {
        var item = Find(id);
        if (item == null) return;

        item.AutoRetryCount = int.MaxValue; // impede re-enfileiramento automático
        DownloadTask? task;
        lock (_lock) _tasks.TryGetValue(id, out task);

        if (task != null)
        {
            task.Pause();
            try { await task.Completion.ConfigureAwait(false); } catch { }
        }

        lock (_lock)
        {
            _items.Remove(item);
            _tasks.Remove(id);
        }

        try
        {
            if (File.Exists(item.TempPath)) File.Delete(item.TempPath);
            if (deleteFile && File.Exists(item.FullPath)) File.Delete(item.FullPath);
        }
        catch { }

        ItemRemoved?.Invoke(item);
        RequestSave();
        Pump();
    }

    /// <summary>Descarta o progresso e baixa novamente do zero.</summary>
    public async Task RestartAsync(Guid id)
    {
        var item = Find(id);
        if (item == null) return;

        DownloadTask? task;
        lock (_lock) _tasks.TryGetValue(id, out task);
        if (task != null)
        {
            task.Pause();
            try { await task.Completion.ConfigureAwait(false); } catch { }
        }

        lock (item.SegmentsLock)
        {
            item.Segments.Clear();
            item.DownloadedBytes = 0;
            item.ElapsedSeconds = 0;
            item.CompletedAt = null;
            item.ComputedHash = null;
            item.RetryCount = 0;
            item.AutoRetryCount = 0;
            item.ErrorMessage = null;
            item.Status = DownloadStatus.Queued;
        }

        try { if (File.Exists(item.TempPath)) File.Delete(item.TempPath); } catch { }

        ItemStatusChanged?.Invoke(item);
        RequestSave();
        Pump();
    }

    public void PauseAll()
    {
        List<DownloadItem> items;
        lock (_lock) items = _items.Where(i => i.IsActive || i.Status == DownloadStatus.Queued).ToList();
        foreach (var i in items) Pause(i.Id);
    }

    public void ResumeAll()
    {
        List<DownloadItem> items;
        lock (_lock) items = _items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed).ToList();
        foreach (var i in items) Resume(i.Id);
    }

    public async Task ClearCompletedAsync()
    {
        List<DownloadItem> items;
        lock (_lock) items = _items.Where(i => i.Status == DownloadStatus.Completed).ToList();
        foreach (var i in items) await RemoveAsync(i.Id, deleteFile: false).ConfigureAwait(false);
    }

    public void SetSpeedLimit(bool enabled, long? bytesPerSecond = null)
    {
        Settings.SpeedLimitEnabled = enabled;
        if (bytesPerSecond is > 0) Settings.SpeedLimitBytesPerSecond = bytesPerSecond.Value;
        ApplyLimiter();
        _settingsStore.Save(Settings);
    }

    public void UpdateSettings(AppSettings updated)
    {
        bool rebuildHttp = updated.UserAgent != Settings.UserAgent || updated.ProxyUrl != Settings.ProxyUrl;
        Settings = updated;
        _settingsStore.Save(updated);

        if (rebuildHttp)
        {
            // o cliente antigo continua vivo para tarefas em andamento; será coletado depois
            _http = HttpClientFactory.Create(updated);
        }

        ApplyLimiter();
        Pump();
    }

    // ------------------------------------------------------------------ interno

    private void ApplyLimiter()
    {
        _limiter.LimitBytesPerSecond = Settings.SpeedLimitEnabled ? Math.Max(0, Settings.SpeedLimitBytesPerSecond) : 0;
    }

    private void Pump()
    {
        if (_disposed) return;

        var started = new List<DownloadTask>();
        lock (_lock)
        {
            int max = Math.Max(1, Settings.MaxConcurrentDownloads);
            if (_tasks.Count >= max) return;

            foreach (var item in _items.Where(i => i.Status == DownloadStatus.Queued).OrderBy(i => i.CreatedAt))
            {
                if (_tasks.Count >= max) break;
                if (_tasks.ContainsKey(item.Id)) continue;

                var task = new DownloadTask(item, _http, Settings, _limiter);
                _tasks[item.Id] = task;
                started.Add(task);
            }
        }

        foreach (var task in started)
        {
            ItemStatusChanged?.Invoke(task.Item);
            _ = task.Completion.ContinueWith(_ => OnTaskFinished(task), TaskScheduler.Default);
        }
    }

    private void OnTaskFinished(DownloadTask task)
    {
        var item = task.Item;
        lock (_lock)
        {
            if (_tasks.TryGetValue(item.Id, out var current) && ReferenceEquals(current, task))
                _tasks.Remove(item.Id);
        }

        ItemStatusChanged?.Invoke(item);

        if (item.Status == DownloadStatus.Completed)
        {
            ItemCompleted?.Invoke(item);
        }
        else if (item.Status == DownloadStatus.Failed)
        {
            ItemFailed?.Invoke(item);

            if (Settings.AutoRetryFailed && item.LastErrorRetryable &&
                item.AutoRetryCount < Settings.MaxAutoRetries && !_cts.IsCancellationRequested)
            {
                item.AutoRetryCount++;
                var delay = TimeSpan.FromSeconds(Math.Min(120, 10 * Math.Pow(2, item.AutoRetryCount - 1)));
                _ = Task.Delay(delay, _cts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    var fresh = Find(item.Id);
                    if (fresh != null && fresh.Status == DownloadStatus.Failed) Resume(item.Id);
                }, TaskScheduler.Default);
            }
        }

        _ = SaveNowAsync();
        Pump();
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();
        double last = 0;
        int n = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                double now = sw.Elapsed.TotalSeconds;
                double dt = now - last;
                last = now;

                DownloadTask[] tasks;
                lock (_lock) tasks = _tasks.Values.ToArray();

                double total = 0;
                foreach (var t in tasks)
                {
                    t.Tick(dt);
                    total += t.Item.Speed;
                }

                TotalSpeed = total;
                lock (_globalHistory)
                {
                    _globalHistory[_globalHead] = total;
                    _globalHead = (_globalHead + 1) % _globalHistory.Length;
                    if (_globalCount < _globalHistory.Length) _globalCount++;
                }

                if (++n % 6 == 0 && tasks.Length > 0) RequestSave();

                try { Tick?.Invoke(); } catch { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RequestSave()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(700, _cts.Token).ConfigureAwait(false); } catch { }
            Interlocked.Exchange(ref _savePending, 0);
            await SaveNowAsync().ConfigureAwait(false);
        });
    }

    private Task SaveNowAsync()
    {
        DownloadItem[] snapshot;
        lock (_lock) snapshot = _items.ToArray();
        return _store.SaveAsync(snapshot);
    }

    /// <summary>Pausa tudo, marca os ativos para retomada automática e salva o estado.</summary>
    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true;

        List<DownloadTask> tasks;
        lock (_lock) tasks = _tasks.Values.ToList();

        foreach (var t in tasks) t.Pause();
        _cts.Cancel();

        try
        {
            await Task.WhenAll(tasks.Select(t => t.Completion)).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch { }

        foreach (var t in tasks)
            if (t.Item.Status == DownloadStatus.Paused) t.Item.Status = DownloadStatus.Queued;

        DownloadItem[] snapshot;
        lock (_lock) snapshot = _items.ToArray();
        _store.Save(snapshot);
    }

    public void Dispose()
    {
        if (!_disposed) ShutdownAsync().GetAwaiter().GetResult();
        _cts.Dispose();
    }
}
