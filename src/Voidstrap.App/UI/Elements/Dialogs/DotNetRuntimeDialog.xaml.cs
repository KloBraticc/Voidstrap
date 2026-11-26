using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class DotNetRuntimeDialog : UserControl
{
    internal event EventHandler<int>? RuntimeChosen;
    internal event EventHandler? CloseRequested;

    public DotNetRuntimeDialog()
    {
        InitializeComponent();
        Net10Title.Text = string.Format(Strings.Menu_DotNetRuntime_Version, 10);
        Net11Title.Text = string.Format(Strings.Menu_DotNetRuntime_Version, 11);
        if (!Voidstrap.Utility.DotNetRuntimeSelection.CanUseNet11)
            Net11Unavailable.Visibility = Visibility.Visible;
        AutomationProperties.SetName(Net10Card, Net10Title.Text);
        AutomationProperties.SetName(Net11Card, Net11Title.Text);
    }

    private void Runtime_Click(object sender, RoutedEventArgs e) => RuntimeChosen?.Invoke(this, ReferenceEquals(sender, Net11Card) ? 11 : 10);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
