using System.Windows;
using System.Windows.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class LearnMoreDialog : UserControl
{
    public event EventHandler? CloseRequested;

    public LearnMoreDialog()
    {
        InitializeComponent();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
            Utilities.OpenWebLink(url);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
