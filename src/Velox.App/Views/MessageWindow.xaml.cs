using System.Windows;
using System.Windows.Media;
using Velox.App.Services;

namespace Velox.App.Views;

public partial class MessageWindow : Window
{
    public MessageWindow(string title, string message, string confirmLabel, bool danger)
    {
        InitializeComponent();
        TitleTb.Text = title;
        MessageTb.Text = message;
        ConfirmBtn.Content = confirmLabel;

        if (danger)
        {
            ConfirmBtn.Style = (Style)FindResource("DangerButton");
            IconBg.Background = (Brush)FindResource("DangerSoftBrush");
            IconTb.Foreground = (Brush)FindResource("DangerBrush");
            IconTb.Text = "";
        }
        else
        {
            ConfirmBtn.Style = (Style)FindResource("PrimaryButton");
        }

        SourceInitialized += (_, _) => WindowEffects.Apply(this);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
