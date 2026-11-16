using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Voidstrap.Integrations.Keystrokes;

namespace Voidstrap.UI.Elements.Bootstrapper
{
    internal interface ICustomThemeHost
    {
        string ThemeDir { get; }

        List<string> UsedNames { get; }

        bool AllowsElement(string name);
    }

    internal interface IKeystrokeThemeHost : ICustomThemeHost
    {
        XElement? KeyStyle { get; set; }

        void AddKey(KeystrokeKeyVisual key);

        void AddCpsText(string input, TextBlock text, string format);

        void AddMouseCircle(FrameworkElement dot, double travel);
    }
}
