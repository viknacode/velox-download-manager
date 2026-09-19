using System.Windows;
using Velox.App.Services;
using Velox.App.ViewModels;

namespace Velox.App.Views;

public partial class BypassWindow : Window
{
    private readonly BypassViewModel _vm;

    public BypassWindow(MainViewModel main, string? url)
    {
        InitializeComponent();
        _vm = new BypassViewModel(main, url);
        _vm.RequestClose += Close;
        DataContext = _vm;

        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
            // veio com link (extensão / menu): já começa a decifrar
            if (!string.IsNullOrWhiteSpace(url) && _vm.ResolveCommand.CanExecute(null))
                _vm.ResolveCommand.Execute(null);
        };
        Closed += (_, _) => _vm.Cancel();
    }

    /// <summary>Recebe um novo link enquanto a janela está aberta (extensão / menu de contexto).</summary>
    public void SetUrl(string url)
    {
        _vm.Url = url;
        if (_vm.ResolveCommand.CanExecute(null)) _vm.ResolveCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
