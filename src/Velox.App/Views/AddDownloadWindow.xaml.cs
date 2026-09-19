using System.Windows;
using Velox.App.Services;
using Velox.App.ViewModels;
using Velox.Core.Models;

namespace Velox.App.Views;

public partial class AddDownloadWindow : Window
{
    private readonly AddDownloadViewModel _vm;

    public AddDownloadWindow(MainViewModel main, IReadOnlyList<string>? urls,
        DownloadRequest? prefill = null, string? sourceLabel = null)
    {
        InitializeComponent();
        _vm = new AddDownloadViewModel(main, urls, prefill, sourceLabel);
        _vm.RequestClose += _ => Close();
        DataContext = _vm;

        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 60);
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        Loaded += (_, _) =>
        {
            if ((urls == null || urls.Count == 0) && prefill == null)
            {
                // sugere link da área de transferência
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var found = UrlHelper.ExtractUrls(Clipboard.GetText());
                        if (found.Count == 1) _vm.Url = found[0];
                        else if (found.Count > 1)
                        {
                            _vm.IsBatch = true;
                            _vm.BatchUrls = string.Join(Environment.NewLine, found);
                        }
                    }
                }
                catch { }
            }
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
        Closed += (_, _) => _vm.Cancel();
    }

    public void ApplyUrls(IReadOnlyList<string> urls)
    {
        if (urls.Count == 1 && !_vm.IsBatch) _vm.Url = urls[0];
        else
        {
            _vm.IsBatch = true;
            var existing = UrlHelper.ExtractUrls(_vm.BatchUrls);
            foreach (var u in urls) if (!existing.Contains(u)) existing.Add(u);
            _vm.BatchUrls = string.Join(Environment.NewLine, existing);
        }
    }

    public void ApplyPrefill(DownloadRequest request, string? sourceLabel) => _vm.Prefill(request, sourceLabel);
}
