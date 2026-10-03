using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.ContextMenu;

public sealed class DiagnosticsWindow : WpfUiWindow
{
    private readonly ActivityWatcher? _activity;
    private readonly TextBox _report;
    private readonly Button _copy;
    private readonly Button _close;
    private readonly DispatcherTimer _timer;
    private IDisposable? _tracker;
    private bool _closed;

    public DiagnosticsWindow(ActivityWatcher? activity)
    {
        _activity = activity;
        Title = Strings.ResourceManager.GetString("OverlayDiagnostics.Title", Strings.Culture) ?? string.Empty;
        Width = 760;
        Height = 640;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        var root = new DockPanel();
        var title = new Wpf.Ui.Controls.TitleBar { Title = Title };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };
        _copy = new Wpf.Ui.Controls.Button { Content = Strings.Common_CopyText, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        _close = new Wpf.Ui.Controls.Button { Content = Strings.Common_Close, MinWidth = 100 };
        _copy.Click += OnCopy;
        _close.Click += OnClose;
        actions.Children.Add(_copy);
        actions.Children.Add(_close);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        _report = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(16, 8, 16, 16)
        };
        root.Children.Add(_report);
        Content = root;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_closed || _tracker != null)
            return;
        try
        {
            _tracker = RobloxWindowTracker.Acquire();
            RobloxWindowTracker.Changed += OnWindowChanged;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("DiagnosticsWindow::Tracker", ex);
        }
        RefreshReport();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e) => RefreshReport();

    private void OnWindowChanged(object? sender, RobloxWindowRect e) => RefreshReport();

    private void RefreshReport()
    {
        if (_closed)
            return;
        string report;
        try
        {
            report = OverlayDiagnostics.BuildReport(_activity);
        }
        catch (Exception ex)
        {
            report = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Strings.ResourceManager.GetString("OverlayDiagnostics.CollectionError", Strings.Culture) ?? "{0}", ex.Message);
        }
        if (_report.Text == report)
            return;
        double offset = _report.VerticalOffset;
        int start = _report.SelectionStart;
        int length = _report.SelectionLength;
        _report.Text = report;
        start = Math.Min(start, report.Length);
        _report.Select(start, Math.Min(length, report.Length - start));
        _report.ScrollToVerticalOffset(offset);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Voidstrap.Utility.ClipboardService.SetDataObject(_report.Text, true);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("DiagnosticsWindow::Copy", ex);
            Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        RobloxWindowTracker.Changed -= OnWindowChanged;
        _tracker?.Dispose();
        _tracker = null;
        _copy.Click -= OnCopy;
        _close.Click -= OnClose;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
