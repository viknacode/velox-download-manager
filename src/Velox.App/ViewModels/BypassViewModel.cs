using System.Collections.ObjectModel;
using System.Diagnostics;
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
    public string Host { get; init; } = "";
    public string ElapsedText { get; init; } = "";
}

/// <summary>Decifra links encurtados / safelinks até a URL de destino, mostrando cada passo.</summary>
public sealed class BypassViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public event Action? RequestClose;

    public BypassViewModel(MainViewModel main, string? initialUrl = null)
    {
        _main = main;
        ResolveCommand = new AsyncRelayCommand(ResolveAsync, () => !IsBusy && Services.UrlHelper.IsHttpUrl(Url.Trim()));
        CancelCommand = new RelayCommand(() => { if (IsBusy) _cts?.Cancel(); else RequestClose?.Invoke(); });
        PasteCommand = new RelayCommand(Paste);
        CopyCommand = new RelayCommand(() => Copy(FinalUrl), () => HasResult);
        OpenCommand = new RelayCommand(() => OpenInBrowser(FinalUrl), () => HasResult);
        OpenOriginalCommand = new RelayCommand(() => OpenInBrowser(Url.Trim()));
        DownloadCommand = new RelayCommand(Download, () => HasResult);

        if (!string.IsNullOrWhiteSpace(initialUrl)) Url = initialUrl;
    }

    public ICommand ResolveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenOriginalCommand { get; }
    public ICommand DownloadCommand { get; }

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<BypassStepItem> Steps { get; } = new();

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
                OnPropertyChanged(nameof(CancelLabel));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string CancelLabel => IsBusy ? "Parar" : "Fechar";

    private string _statusText = "Cole um link encurtado e clique em Decifrar.";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _statusKind = "idle"; // idle | busy | ok | warn | error
    public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    private string _finalUrl = "";
    public string FinalUrl
    {
        get => _finalUrl;
        private set
        {
            if (Set(ref _finalUrl, value)) OnPropertyChanged(nameof(HasResult));
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
                    Host = Uri.TryCreate(s.Url, UriKind.Absolute, out var u) ? u.Host : s.Url,
                    ElapsedText = s.ElapsedMs >= 1000 ? $"{s.ElapsedMs / 1000:0.0} s" : $"{s.ElapsedMs:0} ms"
                });
            }

            var total = result.TotalMs >= 1000 ? $"{result.TotalMs / 1000:0.0} s" : $"{result.TotalMs:0} ms";
            switch (result.Status)
            {
                case ResolveStatus.Resolved:
                    FinalUrl = result.FinalUrl;
                    IsDirectFile = result.IsDirectFile;
                    ResultKindText = result.IsDirectFile
                        ? $"Arquivo direto{(result.FileName != null ? " · " + result.FileName : "")}{(result.Size is > 0 ? " · " + FormatHelper.Bytes(result.Size.Value) : "")}"
                        : "Página de destino (não é um arquivo direto — abra no navegador ou tente baixar)";
                    StatusKind = "ok";
                    StatusText = $"Decifrado em {result.Steps.Count} passo(s) · {total}";
                    SummaryText = string.Join("  →  ", new[] { HostOf(url) }.Concat(result.Steps.Select(s => HostOf(s.Url))).Distinct());
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
        try
        {
            Clipboard.SetText(text);
            _main.Toast("Copiado", text, "success");
        }
        catch { }
    }

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Download()
    {
        if (!HasResult) return;
        var request = new DownloadRequest { Url = FinalUrl, Referer = Url.Trim() };
        _main.RequestAddDialog(request, "Link decifrado");
        RequestClose?.Invoke();
    }

    public void Cancel() => _cts?.Cancel();
}
