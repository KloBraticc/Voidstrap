using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Installer;
using Wpf.Ui.Common;
using WpfContextMenu = System.Windows.Controls.ContextMenu;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class LaunchMenuDialog : WpfUiWindow
{
    private readonly LaunchMenuViewModel _viewModel;

    public NextAction CloseAction;
    private static readonly TimeSpan ChevronDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan MenuCloseDelay = TimeSpan.FromMilliseconds(220);
    private WpfContextMenu? _launchTargetMenu;
    private WpfContextMenu? _accountMenu;
    private System.Windows.Controls.MenuItem? _verifyAllAccountsItem;
    private System.Windows.Controls.MenuItem? _accountStatusItem;
    private DispatcherTimer? _menuCloseTimer;

    public LaunchMenuDialog()
    {
        _viewModel = new LaunchMenuViewModel();
        _viewModel.PrepareAccounts();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.CloseWindowRequest += OnCloseWindowRequest;
        base.DataContext = _viewModel;
        InitializeComponent();
        base.Loaded += OnLoaded;
        base.Closing += OnClosing;
        base.Closed += OnClosed;
        base.PreviewMouseDown += OnPreviewMouseDown;
        base.Deactivated += OnDeactivated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshAccounts();
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

        WpfContextMenu menu = new()
        {
            PlacementTarget = AccountSelectorButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = true,
            Style = (Style)FindResource("LaunchAccountMenuStyle"),
        };
        PopulateAccountMenu(menu);
        menu.Opened += AccountMenu_Opened;
        menu.PreviewKeyDown += AccountMenu_PreviewKeyDown;
        menu.Closed += AccountMenu_Closed;
        _accountMenu = menu;
        menu.IsOpen = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LaunchMenuViewModel.AccountSelectorVisibility)
            && _viewModel.AccountSelectorVisibility != Visibility.Visible && _accountMenu is { } openMenu)
            openMenu.IsOpen = false;
        if (e.PropertyName == nameof(LaunchMenuViewModel.OtherAccounts) && !_viewModel.IsAccountsBusy && _accountMenu is { } menu)
            PopulateAccountMenu(menu);
    }

    private void PopulateAccountMenu(WpfContextMenu menu)
    {
        DetachAccountMenuItems(menu);
        menu.Items.Clear();
        DataTemplate textTemplate = (DataTemplate)FindResource("LaunchAccountMenuText");
        menu.Items.Add(new MenuItem { Header = _viewModel.CurrentAccountTitle, HeaderTemplate = textTemplate, IsEnabled = false });
        menu.Items.Add(CreateAccountMenuSeparator());
        foreach (AccountChoice choice in _viewModel.OtherAccounts)
            menu.Items.Add(CreateAccountMenuItem(choice));
        if (menu.Items.Count == 2)
            menu.Items.Add(new MenuItem { Header = Strings.Accounts_Empty, HeaderTemplate = textTemplate, IsEnabled = false });

        menu.Items.Add(CreateAccountMenuSeparator());
        _verifyAllAccountsItem = new System.Windows.Controls.MenuItem
        {
            Header = Strings.Accounts_CheckAll,
            HeaderTemplate = textTemplate,
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = SymbolRegular.ShieldCheckmark24 },
            IsEnabled = _viewModel.CanVerifyAccounts,
            StaysOpenOnClick = true,
        };
        _verifyAllAccountsItem.Click += VerifyAllAccounts_Click;
        menu.Items.Add(_verifyAllAccountsItem);
        _accountStatusItem = new System.Windows.Controls.MenuItem
        {
            Header = _viewModel.AccountStatus,
            HeaderTemplate = textTemplate,
            IsEnabled = false,
            Visibility = string.IsNullOrEmpty(_viewModel.AccountStatus) ? Visibility.Collapsed : Visibility.Visible,
        };
        menu.Items.Add(_accountStatusItem);
    }

    private Separator CreateAccountMenuSeparator()
    {
        Separator separator = new()
        {
            Margin = new Thickness(4, 2, 4, 2),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Style = (Style)FindResource(MenuItem.SeparatorStyleKey),
        };
        separator.SetResourceReference(Control.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        return separator;
    }

    private MenuItem CreateAccountMenuItem(AccountChoice choice)
    {
        Grid header = new() { Margin = new Thickness(0, 1, 16, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid avatar = new() { Width = 32, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        avatar.Children.Add(new Ellipse { Fill = (Brush)FindResource("ControlFillColorSecondaryBrush") });
        Image avatarImage = new()
        {
            Stretch = Stretch.UniformToFill,
            Clip = new EllipseGeometry(new Point(16, 16), 16, 16),
        };
        RenderOptions.SetBitmapScalingMode(avatarImage, BitmapScalingMode.HighQuality);
        DynamicRenderSystem.SetLazyImageSource(avatarImage, choice.AvatarUrl);

        Wpf.Ui.Controls.SymbolIcon placeholder = new()
        {
            Symbol = SymbolRegular.PersonCircle24,
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
        };
        Style placeholderStyle = new(typeof(Wpf.Ui.Controls.SymbolIcon), (Style)FindResource(typeof(Wpf.Ui.Controls.SymbolIcon)));
        placeholderStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        DataTrigger missingAvatar = new() { Binding = new Binding(nameof(Image.Source)) { Source = avatarImage }, Value = null };
        missingAvatar.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        placeholderStyle.Triggers.Add(missingAvatar);
        placeholder.Style = placeholderStyle;
        avatar.Children.Add(placeholder);
        avatar.Children.Add(avatarImage);
        header.Children.Add(avatar);

        StackPanel accountDetails = new() { VerticalAlignment = VerticalAlignment.Center };
        accountDetails.Children.Add(
            new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                MaxWidth = 210,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = string.IsNullOrWhiteSpace(choice.Account.DisplayName) ? choice.Username : choice.Account.DisplayName,
                ToolTip = choice.Account.DisplayName,
            }
        );
        accountDetails.Children.Add(
            new TextBlock
            {
                FontSize = 11,
                MaxWidth = 210,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextFillColorTertiaryBrush"),
                Text = "@" + choice.Username,
            }
        );
        accountDetails.Children.Add(new TextBlock
        {
            FontSize = 10,
            Text = Strings.Accounts_Expired,
            Visibility = choice.Account.Expired ? Visibility.Visible : Visibility.Collapsed,
        });
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
        return item;
    }

    private void AccountMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is WpfContextMenu menu)
            menu.HorizontalOffset = Math.Min(0, AccountSelectorButton.ActualWidth - menu.ActualWidth);
    }

    private void AccountMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not WpfContextMenu menu)
            return;
        e.Handled = true;
        menu.IsOpen = false;
    }

    private async void VerifyAllAccounts_Click(object sender, RoutedEventArgs e)
    {
        if (_accountMenu is not { } menu || _verifyAllAccountsItem is not { } verifyItem || _accountStatusItem is not { } statusItem)
            return;
        verifyItem.IsEnabled = false;
        statusItem.Header = Strings.Accounts_SignIn_Verifying;
        statusItem.Visibility = Visibility.Visible;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is AccountChoice)
                item.IsEnabled = false;
        }

        await _viewModel.VerifyAllAccountsAsync();
        if (ReferenceEquals(_accountMenu, menu))
            PopulateAccountMenu(menu);
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
        if (sender is not WpfContextMenu menu)
            return;
        menu.Opened -= AccountMenu_Opened;
        menu.PreviewKeyDown -= AccountMenu_PreviewKeyDown;
        menu.Closed -= AccountMenu_Closed;
        DetachAccountMenuItems(menu);
        if (ReferenceEquals(_accountMenu, menu))
        {
            _accountMenu = null;
            _verifyAllAccountsItem = null;
            _accountStatusItem = null;
        }
    }

    private void DetachAccountMenuItems(WpfContextMenu menu)
    {
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            item.Click -= AccountMenuItem_Click;
            item.Click -= VerifyAllAccounts_Click;
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
        WpfContextMenu menu = new()
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
        if (sender is WpfContextMenu menu)
            menu.HorizontalOffset = LaunchTargetButton.ActualWidth - menu.ActualWidth;
    }

    private void LaunchTargetMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not WpfContextMenu menu)
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
        if (sender is not WpfContextMenu menu)
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.CanLeaveLaunchMenu)
            e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopMenuCloseTimer();
        if (_accountMenu is { } accountMenu)
            accountMenu.IsOpen = false;
        if (_launchTargetMenu is { } launchTargetMenu)
            launchTargetMenu.IsOpen = false;
        base.Loaded -= OnLoaded;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.CloseWindowRequest -= OnCloseWindowRequest;
        _viewModel.Dispose();
        base.Closing -= OnClosing;
        base.Closed -= OnClosed;
        base.PreviewMouseDown -= OnPreviewMouseDown;
        base.Deactivated -= OnDeactivated;
    }
}
