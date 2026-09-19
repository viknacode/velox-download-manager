using System.Windows.Controls;

namespace Velox.App.Views;

public partial class BypassView : UserControl
{
    public BypassView()
    {
        InitializeComponent();
    }

    public void FocusInput()
    {
        UrlBox.Focus();
        UrlBox.SelectAll();
    }
}
