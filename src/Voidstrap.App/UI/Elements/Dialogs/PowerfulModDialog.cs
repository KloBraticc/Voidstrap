using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

internal sealed class PowerfulModDialog : WpfUiWindow
{
    private const int CountdownSeconds = 3;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly Wpf.Ui.Controls.Button _allow = new() { Appearance = Wpf.Ui.Common.ControlAppearance.Danger, IsEnabled = false, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };

    private readonly Wpf.Ui.Controls.Button _cancel = new() { Content = Strings.Common_Cancel, MinWidth = 96, IsDefault = true, IsCancel = true };

    private int _remaining = CountdownSeconds;

    public bool Confirmed { get; private set; }

    public PowerfulModDialog(string name, string version, IEnumerable<string> permissions)
    {
        Title = Strings.Ui_VoidstrapMods_PowerfulBadge;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ExtendsContentIntoTitleBar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        StackPanel body = new() { Margin = new Thickness(18, 6, 18, 18) };
        body.Children.Add(Line(name + " " + version, 16, FontWeights.SemiBold));
        body.Children.Add(Line(string.Join(", ", permissions), 12, FontWeights.Normal));
        TextBlock warning = Line(Strings.Plugins_PowerfulModWarning, 13, FontWeights.Normal);
        warning.Margin = new Thickness(0, 12, 0, 16);
        body.Children.Add(warning);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_allow);
        buttons.Children.Add(_cancel);
        body.Children.Add(buttons);

        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new Wpf.Ui.Controls.TitleBar { Title = Title, Padding = new Thickness(8), ShowMinimize = false, ShowMaximize = false, ForceShutdown = false, MinimizeToTray = false });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        UpdateAllowLabel();
        _allow.Click += Allow_Click;
        _timer.Tick += Timer_Tick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private static TextBlock Line(string text, double size, FontWeight weight)
    {
        TextBlock block = new() { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return block;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _timer.Start();

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            _allow.IsEnabled = true;
        }
        UpdateAllowLabel();
    }

    private void UpdateAllowLabel() =>
        _allow.Content = _remaining > 0 ? Strings.Common_Yes + " (" + _remaining + ")" : Strings.Common_Yes;

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _allow.Click -= Allow_Click;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
