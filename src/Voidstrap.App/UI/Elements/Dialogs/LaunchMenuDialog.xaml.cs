using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Installer;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class LaunchMenuDialog : WpfUiWindow
{
    private readonly LaunchMenuViewModel _viewModel;

    public NextAction CloseAction;
    private static readonly TimeSpan ChevronDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan MenuCloseDelay = TimeSpan.FromMilliseconds(220);
    private System.Windows.Controls.ContextMenu? _launchTargetMenu;
    private DispatcherTimer? _menuCloseTimer;

    public LaunchMenuDialog()
    {
        _viewModel = new LaunchMenuViewModel();
        _viewModel.CloseWindowRequest += OnCloseWindowRequest;
        base.DataContext = _viewModel;
        InitializeComponent();
        base.Closed += OnClosed;
        base.PreviewMouseDown += OnPreviewMouseDown;
        base.Deactivated += OnDeactivated;
    }

    private void LaunchTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launchTargetMenu is { } existingMenu)
        {
            existingMenu.IsOpen = false;
            return;
        }
        object? current = _viewModel.CurrentTarget();
        System.Windows.Controls.ContextMenu menu = new()
        {
            PlacementTarget = LaunchTargetButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            HorizontalOffset = LaunchTargetButton.ActualWidth,
            VerticalOffset = 4,
        };
        foreach (object target in _viewModel.LaunchTargets())
        {
            StackPanel header = new() { Orientation = Orientation.Horizontal };
            ImageSource? icon = LaunchMenuViewModel.TargetIcon(target);
            if (icon != null)
                header.Children.Add(new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), Source = icon });
            header.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, Text = LaunchMenuViewModel.TargetTitle(target) ?? "" });
            MenuItem item = new()
            {
                Header = header,
                Tag = target,
                IsCheckable = true,
                IsChecked = ReferenceEquals(target, current),
                StaysOpenOnClick = true,
            };
            item.Click += LaunchTargetItem_Click;
            menu.Items.Add(item);
        }
        menu.PreviewKeyDown += LaunchTargetMenu_PreviewKeyDown;
        menu.Closed += LaunchTargetMenu_Closed;
        menu.Opened += LaunchTargetMenu_Opened;
        _launchTargetMenu = menu;
        menu.IsOpen = true;
        RotateChevron(180);
    }

    private void RotateChevron(double angle)
    {
        DoubleAnimation animation = new(angle, ChevronDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        LaunchTargetChevronRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
    }

    private void LaunchTargetItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: object target } chosen || _menuCloseTimer != null)
            return;
        _viewModel.SelectTarget(target);
        if (chosen.Parent is ItemsControl menu)
        {
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                item.IsChecked = ReferenceEquals(item, chosen);
        }
        _menuCloseTimer = new DispatcherTimer { Interval = MenuCloseDelay };
        _menuCloseTimer.Tick += MenuCloseTimer_Tick;
        _menuCloseTimer.Start();
    }

    private void MenuCloseTimer_Tick(object? sender, EventArgs e)
    {
        StopMenuCloseTimer();
        if (_launchTargetMenu != null)
            _launchTargetMenu.IsOpen = false;
    }

    private void StopMenuCloseTimer()
    {
        if (_menuCloseTimer == null)
            return;
        _menuCloseTimer.Stop();
        _menuCloseTimer.Tick -= MenuCloseTimer_Tick;
        _menuCloseTimer = null;
    }

    private void LaunchTargetMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.ContextMenu menu)
            menu.HorizontalOffset = LaunchTargetButton.ActualWidth - menu.ActualWidth;
    }

    private void LaunchTargetMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not System.Windows.Controls.ContextMenu menu)
            return;
        e.Handled = true;
        menu.IsOpen = false;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_launchTargetMenu?.IsOpen != true)
            return;
        if (new Rect(LaunchTargetButton.RenderSize).Contains(e.GetPosition(LaunchTargetButton)))
            return;
        if (!_launchTargetMenu.IsMouseOver)
            _launchTargetMenu.IsOpen = false;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (new Rect(LaunchTargetButton.RenderSize).Contains(Mouse.GetPosition(LaunchTargetButton)))
            return;
        if (_launchTargetMenu != null)
            _launchTargetMenu.IsOpen = false;
    }

    private void LaunchTargetMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu)
            return;
        menu.PreviewKeyDown -= LaunchTargetMenu_PreviewKeyDown;
        menu.Closed -= LaunchTargetMenu_Closed;
        menu.Opened -= LaunchTargetMenu_Opened;
        StopMenuCloseTimer();
        if (ReferenceEquals(_launchTargetMenu, menu))
            _launchTargetMenu = null;
        RotateChevron(0);
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            item.Click -= LaunchTargetItem_Click;
    }

    private void OnCloseWindowRequest(object? sender, NextAction closeAction)
    {
        CloseAction = closeAction;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopMenuCloseTimer();
        _viewModel.CloseWindowRequest -= OnCloseWindowRequest;
        base.Closed -= OnClosed;
        base.PreviewMouseDown -= OnPreviewMouseDown;
        base.Deactivated -= OnDeactivated;
    }
}
