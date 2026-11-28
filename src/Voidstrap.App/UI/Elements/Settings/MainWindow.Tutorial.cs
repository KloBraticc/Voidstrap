using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Settings.Pages;

namespace Voidstrap.UI.Elements.Settings;

public partial class MainWindow
{
    private enum TutorialStep { Welcome, Home, Sidebar, SidebarToggle, Library, Mods, Breadcrumb, Overlays, RobloxVersion }

    private TutorialStep? _tutorialStep;
    private bool _tutorialOfferPending;
    private bool _tutorialTextRefreshQueued;
    private readonly TranslateTransform _tutorialHighlightOffset = new();
    private TransformGroup? _tutorialBreadcrumbTransform;

    private void OfferBasicTutorial()
    {
        if (_isClosed || App.State.Prop.BasicTutorialCompleted || ModalOpen)
        {
            _tutorialOfferPending = false;
            return;
        }
        if (RootFrame.Content is not Page)
        {
            _tutorialOfferPending = true;
            return;
        }
        _tutorialOfferPending = false;
        OpenTutorial();
    }

    private void AppMenuTutorial_Click(object sender, RoutedEventArgs e) => OpenTutorial();

    private void OpenTutorial()
    {
        if (_tutorialStep.HasValue)
            return;

        _tutorialOfferPending = false;
        RootFrame.Navigated += TutorialPage_Navigated;
        LaunchTargetOverlay.LayoutUpdated += TutorialOverlay_LayoutUpdated;
        ShowTutorialStep(TutorialStep.Welcome);
        OpenSimpleModal(TutorialPanel, TutorialPanelScale);
        TutorialNext.Focus();
    }

    private void ShowTutorialStep(TutorialStep step)
    {
        TutorialHighlight.Visibility = Visibility.Collapsed;
        if (step is TutorialStep.Home or TutorialStep.Breadcrumb)
        {
            _tutorialBreadcrumbTransform ??= new TransformGroup { Children = { _tutorialHighlightOffset, PageHeaderShift } };
            TutorialHighlight.RenderTransform = _tutorialBreadcrumbTransform;
        }
        else
        {
            TutorialHighlight.RenderTransform = _tutorialHighlightOffset;
        }

        bool showLaunchPicker = step == TutorialStep.RobloxVersion;
        if (showLaunchPicker || _tutorialStep == TutorialStep.RobloxVersion)
            SetTutorialLaunchPicker(showLaunchPicker);
        _tutorialStep = step;
        bool welcome = step == TutorialStep.Welcome;
        TutorialPanel.HorizontalAlignment = welcome ? HorizontalAlignment.Center : showLaunchPicker ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TutorialPanel.Width = showLaunchPicker ? 380 : 440;
        TutorialPanel.VerticalAlignment = welcome ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        TutorialPanel.Margin = welcome ? new Thickness(8) : new Thickness(12);
        if (welcome && TutorialBack.IsKeyboardFocusWithin)
            TutorialNext.Focus();
        TutorialBack.Visibility = welcome ? Visibility.Collapsed : Visibility.Visible;
        BindTutorialText(TutorialSkip, ContentControl.ContentProperty, welcome ? nameof(Strings.Common_Yes) : nameof(Strings.Ui_Bootstrapper_Skip));
        BindTutorialText(TutorialNext, ContentControl.ContentProperty, step switch
        {
            TutorialStep.Welcome => nameof(Strings.ContextMenu_GameBrowser_Continue),
            TutorialStep.RobloxVersion => nameof(Strings.Tutorial_Finish),
            _ => nameof(Strings.Common_Navigation_Next),
        });

        (string heading, string description) = step switch
        {
            TutorialStep.Home => (nameof(Strings.Ui_Nav_Home), nameof(Strings.Tutorial_Home)),
            TutorialStep.Sidebar => (nameof(Strings.Ui_Main_PalettePages), nameof(Strings.Tutorial_Sidebar)),
            TutorialStep.SidebarToggle => (nameof(Strings.Ui_Main_ToggleSidebar), nameof(Strings.Tutorial_SidebarToggle)),
            TutorialStep.Library => (nameof(Strings.Ui_Pages_Library), nameof(Strings.Tutorial_Library)),
            TutorialStep.Mods => (nameof(Strings.Ui_Nav_VoidstrapMods), nameof(Strings.Tutorial_Mods)),
            TutorialStep.Breadcrumb => (nameof(Strings.Tutorial_BreadcrumbTitle), nameof(Strings.Tutorial_Breadcrumb)),
            TutorialStep.Overlays => (nameof(Strings.Ui_Mods_Overlays), nameof(Strings.Tutorial_Overlays)),
            TutorialStep.RobloxVersion => (nameof(Strings.Ui_Main_ChooseRobloxVersion), nameof(Strings.Tutorial_RobloxVersion)),
            _ => (nameof(Strings.Installer_Welcome_Title), nameof(Strings.Tutorial_Welcome)),
        };
        BindTutorialText(TutorialHeading, TextBlock.TextProperty, heading);
        BindTutorialText(TutorialDescription, TextBlock.TextProperty, description);

        SyncTutorialPage(step);
        QueueTutorialTextRefresh();
        LaunchTargetOverlay.InvalidateArrange();
    }

    private void QueueTutorialTextRefresh()
    {
        if (!Voidstrap.Utility.Platform.UsesPortableUi || _tutorialTextRefreshQueued)
            return;
        _tutorialTextRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _tutorialTextRefreshQueued = false;
            if (!_isClosed && _tutorialStep.HasValue)
                Voidstrap.UI.LinuxTextGuard.Refresh(TutorialDescription);
        }));
    }

    private void NavigateTutorialPage(Type pageType)
    {
        if (RootFrame.Content?.GetType() != pageType)
            RootNavigation.Navigate(pageType);
    }

    private void SyncTutorialPage(TutorialStep step)
    {
        switch (step)
        {
            case TutorialStep.Home:
            case TutorialStep.Sidebar:
            case TutorialStep.SidebarToggle:
                NavigateTutorialPage(typeof(HomePage));
                break;
            case TutorialStep.Library:
                ShowLibraryPage();
                break;
            case TutorialStep.Mods:
                ShowVoidstrapModsPage();
                break;
            case TutorialStep.Overlays:
                if (RootFrame.Content is ModsPage mods)
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        if (_tutorialStep == TutorialStep.Overlays && ReferenceEquals(RootFrame.Content, mods))
                            mods.RevealOverlaysCard();
                    }));
                else
                    NavigateTutorialPage(typeof(ModsPage));
                break;
            case TutorialStep.Breadcrumb:
                if (RootFrame.Content is BehaviourPage deployment)
                    QueueTutorialAdvancedGeneral(deployment);
                else if (RootFrame.Content is not AdvancedGeneralPage)
                    NavigateTutorialPage(typeof(BehaviourPage));
                break;
        }
    }

    private void QueueTutorialAdvancedGeneral(BehaviourPage deployment)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_tutorialStep == TutorialStep.Breadcrumb && ReferenceEquals(RootFrame.Content, deployment))
                deployment.OpenAdvancedGeneral();
        }));
    }

    private void SetTutorialLaunchPicker(bool visible)
    {
        if (visible)
        {
            PopulateLaunchTargets();
            SetModalScale(LaunchTargetPanelScale, 1.0);
            LaunchTargetPanelSlide.BeginAnimation(TranslateTransform.XProperty, null);
            LaunchTargetPanelSlide.X = 0;
            LaunchTargetPanel.IsHitTestVisible = false;
            KeyboardNavigation.SetTabNavigation(LaunchTargetPanel, KeyboardNavigationMode.None);
            LaunchTargetPanel.Visibility = Visibility.Visible;
        }
        else
        {
            LaunchTargetPanel.Visibility = Visibility.Collapsed;
            LaunchTargetPanel.ClearValue(IsHitTestVisibleProperty);
            LaunchTargetPanel.ClearValue(KeyboardNavigation.TabNavigationProperty);
        }
    }

    private void TutorialPage_Navigated(object sender, NavigationEventArgs e)
    {
        if (_tutorialStep is { } step)
            SyncTutorialPage(step);
    }

    private void TutorialOverlay_LayoutUpdated(object? sender, EventArgs e)
    {
        FrameworkElement? target = (_tutorialStep, RootFrame.Content) switch
        {
            (TutorialStep.Sidebar, HomePage) => SidebarHost,
            (TutorialStep.SidebarToggle, HomePage) => SidebarToggle,
            (TutorialStep.Library, LibraryPage) or (TutorialStep.Mods, VoidstrapModsPage) => PagesMenuButton,
            (TutorialStep.Home, HomePage) or (TutorialStep.Breadcrumb, AdvancedGeneralPage) => PageBreadcrumb,
            (TutorialStep.Overlays, ModsPage mods) => mods.OverlaysCard,
            (TutorialStep.RobloxVersion, _) => LaunchTargetHeading,
            _ => null,
        };
        if (target == null || !target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0
            || LaunchTargetOverlay.ActualWidth <= 0 || LaunchTargetOverlay.ActualHeight <= 0
            || PresentationSource.FromVisual(target) == null || PresentationSource.FromVisual(LaunchTargetOverlay) == null)
        {
            if (TutorialHighlight.Visibility != Visibility.Collapsed)
                TutorialHighlight.Visibility = Visibility.Collapsed;
            return;
        }

        Point origin = target.TranslatePoint(new Point(), LaunchTargetOverlay);
        if (ReferenceEquals(target, PageBreadcrumb))
            origin.X -= PageHeaderShift.X;
        double left = Math.Max(0, origin.X - 4);
        double top = Math.Max(0, origin.Y - 4);
        double width = Math.Max(0, Math.Min(LaunchTargetOverlay.ActualWidth, origin.X + target.ActualWidth + 4) - left);
        double height = Math.Max(0, Math.Min(LaunchTargetOverlay.ActualHeight, origin.Y + target.ActualHeight + 4) - top);
        if (_tutorialHighlightOffset.X != left)
            _tutorialHighlightOffset.X = left;
        if (_tutorialHighlightOffset.Y != top)
            _tutorialHighlightOffset.Y = top;
        if (TutorialHighlight.Width != width)
            TutorialHighlight.Width = width;
        if (TutorialHighlight.Height != height)
            TutorialHighlight.Height = height;
        if (TutorialHighlight.Visibility != Visibility.Visible)
            TutorialHighlight.Visibility = Visibility.Visible;
    }

    private void TutorialBack_Click(object sender, RoutedEventArgs e)
    {
        if (_tutorialStep is { } step && step > TutorialStep.Welcome)
            ShowTutorialStep(AdjacentTutorialStep(step, -1));
    }

    private void TutorialNext_Click(object sender, RoutedEventArgs e)
    {
        if (_tutorialStep is not { } step)
            return;
        if (step == TutorialStep.RobloxVersion)
        {
            CloseSimpleModal();
            NavigateTutorialPage(typeof(HomePage));
        }
        else
            ShowTutorialStep(AdjacentTutorialStep(step, 1));
    }

    private static TutorialStep AdjacentTutorialStep(TutorialStep step, int direction)
    {
        TutorialStep adjacent = step + direction;
        return adjacent == TutorialStep.Overlays && PlatformFeatureVisibility.Overlay != Visibility.Visible
            ? adjacent + direction
            : adjacent;
    }

    private void TutorialSkip_Click(object sender, RoutedEventArgs e) => CloseSimpleModal();

    private void CompleteTutorial()
    {
        ResetTutorial();
        if (!App.State.Prop.BasicTutorialCompleted)
        {
            App.State.Prop.BasicTutorialCompleted = true;
            App.State.SaveDeferred();
        }
    }

    private void ResetTutorial()
    {
        RootFrame.Navigated -= TutorialPage_Navigated;
        LaunchTargetOverlay.LayoutUpdated -= TutorialOverlay_LayoutUpdated;
        if (_tutorialStep == TutorialStep.RobloxVersion)
            SetTutorialLaunchPicker(false);
        _tutorialOfferPending = false;
        _tutorialTextRefreshQueued = false;
        _tutorialStep = null;
        TutorialHighlight.Visibility = Visibility.Collapsed;
        TutorialHighlight.ClearValue(RenderTransformProperty);
    }

    private static void BindTutorialText(FrameworkElement element, DependencyProperty property, string resource) =>
        element.SetBinding(property, new Binding($"[{resource}]") { Source = LocalizedStrings.Instance, Mode = BindingMode.OneWay });
}
