using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Voidstrap.Models;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Installer;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class LaunchMenuDialog : WpfUiWindow
{
    private readonly LaunchMenuViewModel _viewModel;

    public NextAction CloseAction;
    private static readonly TimeSpan ChevronDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan MenuCloseDelay = TimeSpan.FromMilliseconds(220);
    private System.Windows.Controls.ContextMenu? _launchTargetMenu;
    private System.Windows.Controls.ContextMenu? _accountMenu;
    private System.Windows.Controls.MenuItem? _verifyAllAccountsItem;
    private System.Windows.Controls.MenuItem? _accountStatusItem;
    private DispatcherTimer? _menuCloseTimer;

    public LaunchMenuDialog()
    {
        _viewModel = new LaunchMenuViewModel();
        _viewModel.CloseWindowRequest += OnCloseWindowRequest;
        base.DataContext = _viewModel;
        InitializeComponent();
        base.Loaded += OnLoaded;
        base.Closed += OnClosed;
        base.PreviewMouseDown += OnPreviewMouseDown;
        base.Deactivated += OnDeactivated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.LoadAccountsAsync();
    }

    private void AccountSelectorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_accountMenu is { } existingMenu)
        {
            existingMenu.IsOpen = false;
            return;
        }
        if (!_viewModel.CanOpenAccounts)
            return;

        System.Windows.Controls.ContextMenu menu = new()
        {
            PlacementTarget = AccountSelectorButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
        };
        foreach (AccountChoice choice in _viewModel.OtherAccounts)
        {
            Grid header = new() { Margin = new Thickness(0, 1, 16, 1) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid avatar = new() { Width = 32, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
            avatar.Children.Add(new Ellipse { Fill = (Brush)FindResource("ControlFillColorSecondaryBrush") });
            avatar.Children.Add(
                new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = SymbolRegular.PersonCircle24,
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                }
            );
            Image avatarImage = new()
            {
                Stretch = Stretch.UniformToFill,
                Clip = new EllipseGeometry(new Point(16, 16), 16, 16),
            };
            RenderOptions.SetBitmapScalingMode(avatarImage, BitmapScalingMode.HighQuality);
            DynamicRenderSystem.SetLazyImageSource(avatarImage, choice.AvatarUrl);
            avatar.Children.Add(avatarImage);
            header.Children.Add(avatar);

            StackPanel accountDetails = new() { VerticalAlignment = VerticalAlignment.Center };
            accountDetails.Children.Add(
                new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    Text = string.IsNullOrWhiteSpace(choice.Account.DisplayName) ? choice.Username : choice.Account.DisplayName,
                }
            );
            accountDetails.Children.Add(
                new TextBlock
                {
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                    Text = "@" + choice.Username,
                }
            );
            if (choice.Account.Expired)
                accountDetails.Children.Add(new TextBlock { FontSize = 10, Text = Strings.Accounts_Expired });
            Grid.SetColumn(accountDetails, 1);
            header.Children.Add(accountDetails);

            System.Windows.Controls.MenuItem item = new()
            {
                Header = header,
                Tag = choice,
                IsEnabled = _viewModel.CanSwitchAccounts && !choice.Account.Expired,
                StaysOpenOnClick = true,
            };
            item.Click += AccountMenuItem_Click;
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = Strings.Accounts_Empty, IsEnabled = false });

        Separator separator = new()
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            BorderThickness = new Thickness(0, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        separator.Style = (Style)FindResource(System.Windows.Controls.MenuItem.SeparatorStyleKey);
        separator.SetResourceReference(Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        menu.Items.Add(separator);
        _verifyAllAccountsItem = new System.Windows.Controls.MenuItem
        {
            Header = Strings.Accounts_CheckAll,
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.ShieldCheckmark24 },
            IsEnabled = _viewModel.CanVerifyAccounts,
            StaysOpenOnClick = true,
        };
        _verifyAllAccountsItem.Click += VerifyAllAccounts_Click;
        menu.Items.Add(_verifyAllAccountsItem);
        _accountStatusItem = new System.Windows.Controls.MenuItem
        {
            Header = "",
            IsEnabled = false,
            Visibility = Visibility.Collapsed,
        };
        menu.Items.Add(_accountStatusItem);
        menu.Closed += AccountMenu_Closed;
        _accountMenu = menu;
        menu.IsOpen = true;
    }

    private async void VerifyAllAccounts_Click(object sender, RoutedEventArgs e)
    {
        if (_verifyAllAccountsItem != null)
            _verifyAllAccountsItem.IsEnabled = false;
        if (_accountStatusItem != null)
        {
            _accountStatusItem.Header = Strings.Accounts_SignIn_Verifying;
            _accountStatusItem.Visibility = Visibility.Visible;
        }
        await _viewModel.VerifyAllAccountsAsync();
        if (_verifyAllAccountsItem != null)
            _verifyAllAccountsItem.IsEnabled = _viewModel.CanVerifyAccounts;
        if (_accountStatusItem != null)
            _accountStatusItem.Header = _viewModel.AccountStatus;
    }

    private async void AccountMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: AccountChoice choice })
            return;
        if (_accountMenu != null)
            _accountMenu.IsOpen = false;
        await _viewModel.SwitchAccountAsync(choice.Account);
    }

    private void AccountMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu)
            return;
        menu.Closed -= AccountMenu_Closed;
        foreach (System.Windows.Controls.MenuItem item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.Click -= AccountMenuItem_Click;
            item.Click -= VerifyAllAccounts_Click;
        }
        if (ReferenceEquals(_accountMenu, menu))
        {
            _accountMenu = null;
            _verifyAllAccountsItem = null;
            _accountStatusItem = null;
        }
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
        if (
            _launchTargetMenu?.IsOpen == true
            && !new Rect(LaunchTargetButton.RenderSize).Contains(e.GetPosition(LaunchTargetButton))
            && !_launchTargetMenu.IsMouseOver
        )
            _launchTargetMenu.IsOpen = false;
        if (
            _accountMenu?.IsOpen == true
            && !new Rect(AccountSelectorButton.RenderSize).Contains(e.GetPosition(AccountSelectorButton))
            && !_accountMenu.IsMouseOver
        )
            _accountMenu.IsOpen = false;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (
            _launchTargetMenu != null
            && !new Rect(LaunchTargetButton.RenderSize).Contains(Mouse.GetPosition(LaunchTargetButton))
            && !_launchTargetMenu.IsMouseOver
        )
            _launchTargetMenu.IsOpen = false;
        if (
            _accountMenu != null
            && !new Rect(AccountSelectorButton.RenderSize).Contains(Mouse.GetPosition(AccountSelectorButton))
            && !_accountMenu.IsMouseOver
        )
            _accountMenu.IsOpen = false;
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
        base.Loaded -= OnLoaded;
        _viewModel.CloseWindowRequest -= OnCloseWindowRequest;
        _viewModel.Dispose();
        base.Closed -= OnClosed;
        base.PreviewMouseDown -= OnPreviewMouseDown;
        base.Deactivated -= OnDeactivated;
    }
}
