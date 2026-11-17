using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Installer;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class LaunchMenuDialog : WpfUiWindow
{
    private readonly LaunchMenuViewModel _viewModel;

    public NextAction CloseAction;

    public LaunchMenuDialog()
    {
        _viewModel = new LaunchMenuViewModel();
        _viewModel.CloseWindowRequest += OnCloseWindowRequest;
        base.DataContext = _viewModel;
        InitializeComponent();
        base.Closed += OnClosed;
    }

    private void LaunchTargetButton_Click(object sender, RoutedEventArgs e)
    {
        object? current = _viewModel.CurrentTarget();
        System.Windows.Controls.ContextMenu menu = new()
        {
            PlacementTarget = LaunchTargetButton,
            Placement = PlacementMode.Bottom,
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
            };
            item.Click += LaunchTargetItem_Click;
            menu.Items.Add(item);
        }
        menu.Closed += LaunchTargetMenu_Closed;
        menu.IsOpen = true;
    }

    private void LaunchTargetItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: object target })
            _viewModel.SelectTarget(target);
    }

    private void LaunchTargetMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu)
            return;
        menu.Closed -= LaunchTargetMenu_Closed;
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
        _viewModel.CloseWindowRequest -= OnCloseWindowRequest;
        base.Closed -= OnClosed;
    }
}
