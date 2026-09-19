using System.Windows;
using Velox.App.Services;
using Velox.App.ViewModels;

namespace Velox.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel main)
    {
        InitializeComponent();
        var vm = new SettingsViewModel(main);
        vm.RequestClose += ok =>
        {
            DialogResult = ok;
            Close();
        };
        DataContext = vm;
        MaxHeight = Math.Max(520, SystemParameters.WorkArea.Height - 40);
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
    }
}
