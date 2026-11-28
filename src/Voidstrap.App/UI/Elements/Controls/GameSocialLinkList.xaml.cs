using System.Windows;
using System.Windows.Controls;

namespace Voidstrap.UI.Elements.Controls;

public partial class GameSocialLinkList : ItemsControl
{
    public GameSocialLinkList()
    {
        InitializeComponent();
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
            Utilities.OpenWebLink(uri.AbsoluteUri);
    }
}
