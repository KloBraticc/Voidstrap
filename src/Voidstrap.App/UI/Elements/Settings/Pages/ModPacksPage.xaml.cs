using System.ComponentModel;
using System.Windows;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ModPacksPage : UiPage
{
    private static CommunityModsViewModel Mods => CommunityModsViewModel.Shared;

    public ModPacksPage()
    {
        DataContext = Mods;
        InitializeComponent();
    }

    internal bool IsModPackSelected => Mods.SelectedMod != null;

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Mods.PropertyChanged -= OnModsPropertyChanged;
        Mods.PropertyChanged += OnModsPropertyChanged;
        Breadcrumb.RemoveBackRequestedHandler(this, OnBreadcrumbBack);
        Breadcrumb.AddBackRequestedHandler(this, OnBreadcrumbBack);
        UpdateBreadcrumb();
        PublishPresence();
        ScrollToTop();
        AttachStickyScroll();
        try
        {
            await Mods.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("ModPacksPage::Loaded", ex);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        Mods.PropertyChanged -= OnModsPropertyChanged;
        Breadcrumb.RemoveBackRequestedHandler(this, OnBreadcrumbBack);
        DetachStickyScroll();
        _sourceSwitchVersion++;
        SourceIndicatorShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        Voidstrap.Integrations.VoidstrapPresence.Clear(nameof(ModPacksPage));
    }

    private void OnModsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommunityModsViewModel.SelectedMod))
            return;
        UpdateBreadcrumb();
        PublishPresence();
        ScrollToTop();
    }

    private readonly System.Windows.Media.TranslateTransform _stickyShift = new();

    private System.Windows.Controls.ScrollViewer? _stickyViewer;

    private void AttachStickyScroll()
    {
        DetachStickyScroll();
        SourceSwitch.RenderTransform = _stickyShift;
        CategoryPanel.RenderTransform = _stickyShift;
        _stickyViewer = FindScrollViewer(this) ?? FindAncestorScrollViewer();
        if (_stickyViewer == null)
            return;
        _stickyViewer.ScrollChanged += OnStickyScrollChanged;
        UpdateSticky();
    }

    private void DetachStickyScroll()
    {
        if (_stickyViewer != null)
            _stickyViewer.ScrollChanged -= OnStickyScrollChanged;
        _stickyViewer = null;
        _stickyShift.Y = 0;
    }

    private void OnStickyScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e) => UpdateSticky();

    private void UpdateSticky()
    {
        if (_stickyViewer == null || !BrowseRoot.IsVisible || !SourceSwitch.IsVisible)
        {
            _stickyShift.Y = 0;
            return;
        }
        double current = _stickyShift.Y;
        double top = SourceSwitch.TranslatePoint(new Point(0, 0), _stickyViewer).Y - current;
        double bottom = (CategoryPanel.IsVisible ? CategoryPanel.TranslatePoint(new Point(0, CategoryPanel.ActualHeight), BrowseRoot).Y : SourceSwitch.TranslatePoint(new Point(0, SourceSwitch.ActualHeight), BrowseRoot).Y) - current;
        double room = Math.Max(0, BrowseRoot.ActualHeight - bottom);
        _stickyShift.Y = Math.Clamp(8 - top, 0, room);
    }

    private System.Windows.Controls.ScrollViewer? FindAncestorScrollViewer()
    {
        DependencyObject? node = System.Windows.Media.VisualTreeHelper.GetParent(this);
        while (node != null)
        {
            if (node is System.Windows.Controls.ScrollViewer viewer)
                return viewer;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private bool _compactDetail;

    private void DetailRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 840.0;
        if (compact == _compactDetail)
            return;
        _compactDetail = compact;
        DetailSidebarColumn.Width = new GridLength(compact ? 0.0 : 300.0);
        System.Windows.Controls.Grid.SetRow(DetailSidebar, compact ? 1 : 0);
        System.Windows.Controls.Grid.SetColumn(DetailSidebar, compact ? 0 : 1);
        DetailMain.Margin = compact ? new Thickness(0.0) : new Thickness(0.0, 0.0, 20.0, 0.0);
        DetailSidebar.Margin = compact ? new Thickness(0.0, 20.0, 0.0, 0.0) : new Thickness(0.0, 38.0, 0.0, 0.0);
    }

    private void ScrollToTop()
    {
        if (FindScrollViewer(this) is { } inner)
        {
            inner.ScrollToTop();
            return;
        }
        DependencyObject? node = this;
        while (node != null)
        {
            if (node is System.Windows.Controls.ScrollViewer viewer)
            {
                viewer.ScrollToTop();
                return;
            }
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.ScrollViewer viewer)
                return viewer;
            if (FindScrollViewer(child) is { } found)
                return found;
        }
        return null;
    }

    private void UpdateBreadcrumb()
    {
        Breadcrumb.SetRootTitle(this, Mods.SelectedMod == null ? null : Voidstrap.Resources.Strings.Ui_Mods_ModPacks);
        if (Mods.SelectedMod is { } mod)
            Voidstrap.UI.BreadcrumbHeader.Set(this, mod.Name);
        else
            Voidstrap.UI.BreadcrumbHeader.SetLocalized(this, nameof(Voidstrap.Resources.Strings.Ui_Mods_ModPacks));
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.RefreshGlobalSearchBoxVisibility();
    }

    private void OnBreadcrumbBack(object sender, RoutedEventArgs e)
    {
        if (Mods.SelectedMod != null && Mods.BackCommand.CanExecute(null))
            Mods.BackCommand.Execute(null);
    }

    private void PublishPresence()
    {
        Voidstrap.Integrations.CommunityMods.CommunityModEntry? mod = Mods.SelectedMod;
        if (mod != null)
        {
            string byline = string.IsNullOrWhiteSpace(mod.Author)
                ? (string.IsNullOrWhiteSpace(mod.SourceName) ? "" : Voidstrap.Utility.RpcText.Mark("from") + " " + mod.SourceName)
                : Voidstrap.Utility.RpcText.Mark("by") + " " + mod.Author;
            string state =
                !string.IsNullOrWhiteSpace(mod.Summary) ? mod.Summary
                : byline.Length > 0
                    ? Voidstrap.Utility.RpcText.Mark(string.IsNullOrWhiteSpace(mod.Author) ? "Mod pack" : "Made") + " " + byline
                : Voidstrap.Utility.RpcText.Mark("A community mod pack");
            Voidstrap.Integrations.VoidstrapPresence.Set(
                new Voidstrap.Integrations.VoidstrapPresenceContext(
                    nameof(ModPacksPage),
                    Voidstrap.Utility.RpcText.Mark("Viewing") + " " + mod.Name,
                    state,
                    Voidstrap.Integrations.VoidstrapPresence.WebIcon(mod.IconUrl, Voidstrap.Utility.Branding.RpcLogoUrl),
                    mod.Name + (byline.Length > 0 ? " " + byline : ""),
                    Voidstrap.Utility.RpcText.Mark("View mod"),
                    mod.ProfileUrl
                )
            );
        }
        else
        {
            Voidstrap.Integrations.VoidstrapPresence.Set(
                new Voidstrap.Integrations.VoidstrapPresenceContext(
                    nameof(ModPacksPage),
                    Voidstrap.Utility.RpcText.Mark("Mods") + ": " + Voidstrap.Utility.RpcText.Mark("Mod Packs"),
                    Voidstrap.Utility.RpcText.Mark("Browsing community mod packs")
                )
            );
        }
    }

    private async void CommunityMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Voidstrap.Integrations.CommunityMods.CommunityModEntry entry })
            return;
        try
        {
            await Mods.OpenDetailAsync(entry);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("ModPacksPage::CommunityMod", ex);
        }
    }

    private const double SourceIndicatorStep = 139;

    private static readonly Duration SourceSlideDuration = new(TimeSpan.FromMilliseconds(220));

    private int _sourceSwitchVersion;

    private static double SourceOffset(string source) =>
        source == Voidstrap.Integrations.CommunityMods.MarketplaceCatalog.SourceName ? SourceIndicatorStep : 0;

    private void SourceIndicator_Loaded(object sender, RoutedEventArgs e)
    {
        SourceIndicatorShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        SourceIndicatorShift.X = SourceOffset(Mods.SelectedSource);
    }

    private async void CommunitySource_Click(object sender, RoutedEventArgs e)
    {
        const string LOG_IDENT = "ModPacksPage::CommunitySource";
        if (sender is not FrameworkElement { Tag: string source })
            return;
        int version = ++_sourceSwitchVersion;
        double target = SourceOffset(source);
        double from = SourceIndicatorShift.X;
        SourceIndicatorShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        SourceIndicatorShift.X = target;
        if (Math.Abs(from - target) > 0.5)
        {
            System.Windows.Media.Animation.DoubleAnimation slide = new(from, target, SourceSlideDuration)
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            };
            slide.Freeze();
            SourceIndicatorShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
            try
            {
                await Task.Delay(SourceSlideDuration.TimeSpan);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }
        if (version == _sourceSwitchVersion)
            Mods.SelectedSource = source;
    }

    private void CommunityCategoryChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Voidstrap.Integrations.CommunityMods.CommunityModCategory category })
            Mods.SetCategorySelected(category, !category.IsSelected);
    }

    private void CommunityModView_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CommunityMod_Click(sender, e);
    }

    private async void CommunityModInstall_Click(object sender, RoutedEventArgs e)
    {
        const string LOG_IDENT = "ModPacksPage::CommunityModInstall";
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: Voidstrap.Integrations.CommunityMods.CommunityModEntry entry })
            return;
        CommunityModsViewModel mods = Mods;
        try
        {
            await mods.OpenDetailAsync(entry);
            if (!mods.CanInstall)
                return;
            System.Windows.Input.ICommand install = mods.ManagedInstallVisibility == Visibility.Visible ? mods.InstallCommand : mods.InstallFleasionCommand;
            if (install.CanExecute(null))
                install.Execute(null);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
        }
    }
}
