using System.Windows;
using System.Windows.Controls;
using Voidstrap.Models;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class NewsPostDialog : UserControl
{
    public event EventHandler? CloseRequested;

    public NewsPostDialog(NewsPost post)
    {
        DataContext = post;
        InitializeComponent();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
            Utilities.OpenWebLink(url);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
