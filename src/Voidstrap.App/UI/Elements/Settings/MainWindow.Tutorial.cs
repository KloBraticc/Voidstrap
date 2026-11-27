using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Navigation;
using System.Windows.Threading;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Settings.Pages;

namespace Voidstrap.UI.Elements.Settings;

public partial class MainWindow
{
    private enum TutorialStep { Welcome, Home, Sidebar, SidebarToggle, Library, Mods, Breadcrumb }

    private TutorialStep? _tutorialStep;

    private void OfferBasicTutorial()
    {
        if (!_isClosed && !App.State.Prop.BasicTutorialCompleted && !ModalOpen && RootFrame.Content is Page)
            OpenTutorial();
    }

    private void AppMenuTutorial_Click(object sender, RoutedEventArgs e) => OpenTutorial();

    private void OpenTutorial()
    {
        if (_tutorialStep.HasValue)
            return;

        RootFrame.Navigated += TutorialPage_Navigated;
        ShowTutorialStep(TutorialStep.Welcome);
        OpenSimpleModal(TutorialPanel, TutorialPanelScale);
        TutorialNext.Focus();
    }

    private void ShowTutorialStep(TutorialStep step)
    {
        _tutorialStep = step;
        bool welcome = step == TutorialStep.Welcome;
        TutorialPanel.HorizontalAlignment = welcome ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        TutorialPanel.VerticalAlignment = welcome ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        TutorialPanel.Margin = welcome ? new Thickness(8) : new Thickness(12);
        TutorialBack.Visibility = welcome ? Visibility.Collapsed : Visibility.Visible;
        BindTutorialText(TutorialSkip, ContentControl.ContentProperty, welcome ? nameof(Strings.Common_Yes) : nameof(Strings.Ui_Bootstrapper_Skip));
        BindTutorialText(TutorialNext, ContentControl.ContentProperty, step switch
        {
            TutorialStep.Welcome => nameof(Strings.ContextMenu_GameBrowser_Continue),
            TutorialStep.Breadcrumb => nameof(Strings.Ui_Home_DoneEditingLayout),
            _ => nameof(Strings.Common_Navigation_Next),
        });

        (string heading, string description) = step switch
        {
            TutorialStep.Home => (nameof(Strings.Ui_Nav_Home), nameof(Strings.Tutorial_Home)),
            TutorialStep.Sidebar => (nameof(Strings.Ui_Main_PalettePages), nameof(Strings.Tutorial_Sidebar)),
            TutorialStep.SidebarToggle => (nameof(Strings.Ui_Main_ToggleSidebar), nameof(Strings.Tutorial_SidebarToggle)),
            TutorialStep.Library => (nameof(Strings.Ui_Pages_Library), nameof(Strings.Tutorial_Library)),
            TutorialStep.Mods => (nameof(Strings.Ui_Nav_VoidstrapMods), nameof(Strings.Tutorial_Mods)),
            TutorialStep.Breadcrumb => (nameof(Strings.Ui_Bootstrapper_AdvancedGeneral), nameof(Strings.Tutorial_Breadcrumb)),
            _ => (nameof(Strings.Installer_Welcome_Title), nameof(Strings.Tutorial_Welcome)),
        };
        BindTutorialText(TutorialHeading, TextBlock.TextProperty, heading);
        BindTutorialText(TutorialDescription, TextBlock.TextProperty, description);

        switch (step)
        {
            case TutorialStep.Home:
            case TutorialStep.Sidebar:
            case TutorialStep.SidebarToggle:
                if (RootFrame.Content is not HomePage)
                    NavigateTopNav(typeof(HomePage));
                break;
            case TutorialStep.Library:
                ShowLibraryPage();
                break;
            case TutorialStep.Mods:
                ShowVoidstrapModsPage();
                break;
            case TutorialStep.Breadcrumb:
                if (RootFrame.Content is BehaviourPage deployment)
                    deployment.OpenAdvancedGeneral();
                else
                    NavigateTopNav(typeof(BehaviourPage));
                break;
        }
        QueueTutorialHighlight();
    }

    private void TutorialPage_Navigated(object sender, NavigationEventArgs e)
    {
        if (_tutorialStep == TutorialStep.Breadcrumb && e.Content is BehaviourPage deployment)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_tutorialStep == TutorialStep.Breadcrumb && ReferenceEquals(RootFrame.Content, deployment))
                    deployment.OpenAdvancedGeneral();
            }));
        }
        QueueTutorialHighlight();
    }

    private void QueueTutorialHighlight() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateTutorialHighlight));

    private void TutorialPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_tutorialStep.HasValue)
            QueueTutorialHighlight();
    }

    private void UpdateTutorialHighlight()
    {
        FrameworkElement? target = _tutorialStep switch
        {
            TutorialStep.Sidebar => SidebarHost,
            TutorialStep.SidebarToggle => SidebarToggle,
            TutorialStep.Library or TutorialStep.Mods => PagesMenuButton,
            TutorialStep.Home or TutorialStep.Breadcrumb => PageBreadcrumb,
            _ => null,
        };
        if (target == null || !target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0)
        {
            TutorialHighlight.Visibility = Visibility.Collapsed;
            return;
        }

        Point origin = target.TranslatePoint(new Point(), LaunchTargetOverlay);
        double left = Math.Max(0, origin.X - 4);
        double top = Math.Max(0, origin.Y - 4);
        TutorialHighlight.Margin = new Thickness(left, top, 0, 0);
        TutorialHighlight.Width = Math.Max(0, Math.Min(LaunchTargetOverlay.ActualWidth, origin.X + target.ActualWidth + 4) - left);
        TutorialHighlight.Height = Math.Max(0, Math.Min(LaunchTargetOverlay.ActualHeight, origin.Y + target.ActualHeight + 4) - top);
        TutorialHighlight.Visibility = Visibility.Visible;
    }

    private void TutorialBack_Click(object sender, RoutedEventArgs e)
    {
        if (_tutorialStep is { } step && step > TutorialStep.Welcome)
            ShowTutorialStep(step - 1);
    }

    private void TutorialNext_Click(object sender, RoutedEventArgs e)
    {
        if (_tutorialStep is not { } step)
            return;
        if (step == TutorialStep.Breadcrumb)
            CloseSimpleModal();
        else
            ShowTutorialStep(step + 1);
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
        _tutorialStep = null;
        TutorialHighlight.Visibility = Visibility.Collapsed;
    }

    private static void BindTutorialText(FrameworkElement element, DependencyProperty property, string resource) =>
        element.SetBinding(property, new Binding($"[{resource}]") { Source = LocalizedStrings.Instance, Mode = BindingMode.OneWay });
}
