using System.Windows;

namespace Voidstrap.UI.Elements.Editor;

internal static class VisualStudioTheme
{
    private static readonly Uri Source = new("/Voidstrap;component/UI/Elements/Editor/VisualStudioTheme.xaml", UriKind.Relative);

    public static void Apply(FrameworkElement element)
    {
        ResourceDictionary theme = new() { Source = Source };
        element.Resources.MergedDictionaries.Add(theme);
        EditorAppearance.AttachPanel(element);
    }
}
