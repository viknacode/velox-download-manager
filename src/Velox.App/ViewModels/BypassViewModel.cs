using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Velox.Core.Models;
using Velox.Core.Resolvers;
using Velox.Core.Utils;

namespace Velox.App.ViewModels;

public sealed class BypassStepItem
{
    public int Index { get; init; }
    public string Resolver { get; init; } = "";
    public string Description { get; init; } = "";
    public string Url { get; init; } = "";
    public string ElapsedText { get; init; } = "";
}

/// <summary>Entrada do histórico de links decifrados (persistido em bypass-history.json).</summary>
public sealed class BypassHistoryItem : ObservableObject
{
    public string OriginalUrl { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public string Chain { get; set; } = "";
    public int StepCount { get; set; }
    public bool IsDirectFile { get; set; }
    public string? FileName { get; set; }
    public DateTime When { get; set; } = DateTime.Now;

    [System.Text.Json.Serialization.JsonIgnore] public string WhenText => When.ToString("dd/MM HH:mm");
    [System.Text.Json.Serialization.JsonIgnore] public string OriginalHost => HostOf(OriginalUrl);
    [System.Text.Json.Serialization.JsonIgnore] public string FinalHost => HostOf(FinalUrl);
    [System.Text.Json.Serialization.JsonIgnore] public string KindText => IsDirectFile ? (FileName ?? "arquivo direto") : "página";

    [System.Text.Json.Serialization.JsonIgnore] public ICommand? CopyCommand { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public ICommand? DownloadCommand { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public ICommand? OpenCommand { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public ICommand? ReuseCommand { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public ICommand? RemoveCommand { get; set; }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}

/// <summary>Decifra links encurtados / safelinks até a URL de destino, mostrando cada passo e guardando histórico.</summary>
public sealed class BypassViewModel : ObservableObject
{
    private const int MaxHistory = 200;

    private readonly MainViewModel _main;
    private readonly string _historyPath;
    private CancellationTokenSource? _cts;

    public BypassViewModel(MainViewModel main)
    {
        _main = main;
        _historyPath = Path.Combine(main.DataDirectory, "bypass-history.json");

        ResolveCommand = new AsyncRelayCommand(ResolveAsync, () => !IsBusy && Services.UrlHelper.IsHttpUrl(Url.Trim()));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        PasteCommand = new RelayCommand(Paste);
        ClearCommand = new RelayCommand(Clear);
        CopyCommand = new RelayCommand(() => Copy(FinalUrl), () => HasResult);
        OpenCommand = new RelayCommand(() => OpenInBrowser(FinalUrl), () => HasResult);
        OpenOriginalCommand = new RelayCommand(() => OpenInBrowser(Url.Trim()));
        DownloadCommand = new RelayCommand(() => Download(FinalUrl, Url.Trim()), () => HasResult);
        ClearHistoryCommand = new RelayCommand(() => { History.Clear(); SaveHistory(); }, () => History.Count > 0);

        LoadHistory();
    }

    public ICommand ResolveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenOriginalCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<BypassStepItem> Steps { get; } = new();
    public ObservableCollection<BypassHistoryItem> History { get; } = new();

    // ------------------------------------------------------------ estado
    private string _url = "";
    public string Url
    {
        get => _url;
        set
        {
            if (Set(ref _url, value)) OnPropertyChanged(nameof(CanResolve));
        }
    }

    public bool CanResolve => !IsBusy && Services.UrlHelper.IsHttpUrl(_url.Trim());

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanResolve));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private string _statusText = "Cole um link encurtado (shrinkme, gplinks, safelink…) e clique em Decifrar.";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _statusKind = "idle"; // idle | busy | ok | warn | error
    public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    private string _finalUrl = "";
    public string FinalUrl
    {
        get => _finalUrl;
        private set
        {
            if (Set(ref _finalUrl, value))
            {
                OnPropertyChanged(nameof(HasResult));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasResult => !string.IsNullOrEmpty(_finalUrl);

    private string _resultKindText = "";
    public string ResultKindText { get => _resultKindText; private set => Set(ref _resultKindText, value); }

    private bool _isDirectFile;
    public bool IsDirectFile { get => _isDirectFile; private set => Set(ref _isDirectFile, value); }

    private bool _needsBrowser;
    public bool NeedsBrowser { get => _needsBrowser; private set => Set(ref _needsBrowser, value); }

    private string _summaryText = "";
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }

    public bool HasHistory => History.Count > 0;

    // ------------------------------------------------------------ ações
    /// <summary>Define a URL e decifra imediatamente (menu da extensão, botão da toolbar com link, histórico).</summary>
    public void ResolveNow(string url)
    {
        Url = url;
        if (ResolveCommand.CanExecute(null)) ResolveCommand.Execute(null);
    }

    private async Task ResolveAsync()
    {
        var url = Url.Trim();
        if (!Services.UrlHelper.IsHttpUrl(url)) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        NeedsBrowser = false;
        FinalUrl = "";
        IsDirectFile = false;
        Steps.Clear();
        LogLines.Clear();
        SummaryText = "";
        StatusKind = "busy";
        StatusText = "Decifrando…";

        try
        {
            var result = await _main.ResolveLinkAsync(url, line => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                LogLines.Add(line);
                StatusText = line;
            }), ct);

            foreach (var s in result.Steps)
            {
                Steps.Add(new BypassStepItem
                {
                    Index = s.Index,
                    Resolver = s.Resolver,
                    Description = s.Description,
                    Url = s.Url,
                    ElapsedText = s.ElapsedMs >= 1000 ? $"{s.ElapsedMs / 1000:0.0} s" : $"{s.ElapsedMs:0} ms"
                });
            }

            var total = result.TotalMs >= 1000 ? $"{result.TotalMs / 1000:0.0} s" : $"{result.TotalMs:0} ms";
            var chain = string.Join("  →  ", new[] { HostOf(url) }.Concat(result.Steps.Select(s => HostOf(s.Url))).Distinct());

            switch (result.Status)
            {
                case ResolveStatus.Resolved:
                    FinalUrl = result.FinalUrl;
                    IsDirectFile = result.IsDirectFile;
                    ResultKindText = result.IsDirectFile
                        ? $"Arquivo direto{(result.FileName != null ? " · " + result.FileName : "")}{(result.Size is > 0 ? " · " + FormatHelper.Bytes(result.Size.Value) : "")}"
                        : "Página de destino — não é um arquivo direto (abra no navegador ou tente baixar)";
                    StatusKind = "ok";
                    StatusText = $"Decifrado em {result.Steps.Count} passo(s) · {total}";
                    SummaryText = chain;
                    AddToHistory(new BypassHistoryItem
                    {
                        OriginalUrl = url,
                        FinalUrl = result.FinalUrl,
                        Chain = chain,
                        StepCount = result.Steps.Count,
                        IsDirectFile = result.IsDirectFile,
                        FileName = result.FileName
                    });
                    break;

                case ResolveStatus.Unchanged:
                    FinalUrl = result.FinalUrl;
                    IsDirectFile = result.IsDirectFile;
                    ResultKindText = result.IsDirectFile ? "Já é um arquivo direto" : "Não parece ser um link encurtado conhecido";
                    StatusKind = "warn";
                    StatusText = "Nenhum portão reconhecido nesta URL.";
                    break;

                case ResolveStatus.NeedsBrowser:
                    NeedsBrowser = true;
                    StatusKind = "warn";
                    StatusText = result.Error ?? "Este site exige interação no navegador.";
                    break;

                default:
                    StatusKind = "error";
                    StatusText = result.Error ?? "Falha ao decifrar.";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            StatusKind = "warn";
            StatusText = "Cancelado.";
        }
        catch (Exception ex)
        {
            StatusKind = "error";
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Clear()
    {
        _cts?.Cancel();
        Url = "";
        FinalUrl = "";
        NeedsBrowser = false;
        Steps.Clear();
        LogLines.Clear();
        SummaryText = "";
        StatusKind = "idle";
        StatusText = "Cole um link encurtado (shrinkme, gplinks, safelink…) e clique em Decifrar.";
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private void Paste()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var found = Services.UrlHelper.ExtractUrls(Clipboard.GetText());
            if (found.Count > 0) Url = found[0];
        }
        catch { }
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            _main.SetClipboardIgnore(text);
            Clipboard.SetText(text);
            _main.Toast("Link copiado", text, "success");
        }
        catch { }
    }

    private static void OpenInBrowser(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Download(string finalUrl, string originalUrl)
    {
        if (string.IsNullOrEmpty(finalUrl)) return;
        _main.RequestAddDialog(new DownloadRequest { Url = finalUrl, Referer = originalUrl }, "Link decifrado");
    }

    // ------------------------------------------------------------ histórico
    private void AddToHistory(BypassHistoryItem item)
    {
        var existing = History.FirstOrDefault(h => h.OriginalUrl == item.OriginalUrl);
        if (existing != null) History.Remove(existing);
        Wire(item);
        History.Insert(0, item);
        while (History.Count > MaxHistory) History.RemoveAt(History.Count - 1);
        OnPropertyChanged(nameof(HasHistory));
        SaveHistory();
    }

    private void Wire(BypassHistoryItem item)
    {
        item.CopyCommand = new RelayCommand(() => Copy(item.FinalUrl));
        item.DownloadCommand = new RelayCommand(() => Download(item.FinalUrl, item.OriginalUrl));
        item.OpenCommand = new RelayCommand(() => OpenInBrowser(item.FinalUrl));
        item.ReuseCommand = new RelayCommand(() => ResolveNow(item.OriginalUrl));
        item.RemoveCommand = new RelayCommand(() =>
        {
            History.Remove(item);
            OnPropertyChanged(nameof(HasHistory));
            SaveHistory();
        });
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return;
            var items = JsonSerializer.Deserialize<List<BypassHistoryItem>>(File.ReadAllText(_historyPath)) ?? new();
            foreach (var it in items.Take(MaxHistory))
            {
                Wire(it);
                History.Add(it);
            }
            OnPropertyChanged(nameof(HasHistory));
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(History.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
