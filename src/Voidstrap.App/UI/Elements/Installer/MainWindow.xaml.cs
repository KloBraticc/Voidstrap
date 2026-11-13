using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Markup;
using System.Windows.Navigation;
using Voidstrap.Enums;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Installer.Pages;
using Voidstrap.UI.ViewModels.Installer;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.Installer;

public partial class MainWindow : WpfUiWindow, INavigationWindow
{
    internal readonly MainWindowViewModel _viewModel = new MainWindowViewModel();

    private Type _currentPage = typeof(InstallPage);

    private List<Type> _pages = CreatePages();

    private DateTimeOffset _lastNavigation = DateTimeOffset.Now;

    private DependencyObject? _linuxTextContent;

    public Func<bool>? NextPageCallback;
    public Func<System.Threading.Tasks.Task<bool>>? NextPageAsyncCallback;

    public Action? CancelCallback;
    private bool _navigating;

    public NextAction CloseAction;

    public bool Finished => _currentPage == _pages.Last();

    private static List<Type> CreatePages()
    {
        List<Type> pages = [typeof(InstallPage)];
        if (!Voidstrap.Utility.Platform.IsMacOS)
            pages.Add(typeof(ChannelPage));
        if (Voidstrap.Utility.Platform.IsWindows)
        {
            pages.Add(typeof(InstallerModsPage));
            pages.Add(typeof(InstallerAppearancePage));
            pages.Add(typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage));
            pages.Add(typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage));
        }

        pages.Add(typeof(CompletionPage));
        return pages;
    }

    public MainWindow()
    {
        SetButtonEnabled("next", state: true);
        _viewModel.CloseWindowRequest += OnCloseWindowRequest;
        _viewModel.PageRequest += OnPageRequest;
        base.DataContext = _viewModel;
        InitializeComponent();
        App.Logger.WriteLine("MainWindow", "Initializing installer window");
        base.Closing += MainWindow_Closing;
        if (Voidstrap.Utility.Platform.UsesPortableUi)
        {
            RootFrame.SizeChanged += RootFrame_SizeChanged;
        }
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            Voidstrap.UI.LinuxUiPerformance.ReducedMotionChanged += OnLinuxReducedMotionChanged;
            if (Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
                RootNavigation.TransitionDuration = 0;
        }
        base.Closed += MainWindow_Closed;
        _viewModel.SetStep(0, HeadingFor(typeof(InstallPage)));
        PaintSteps(0);
        ApplyChrome(typeof(InstallPage));
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        ApplyChrome(_currentPage);
    }

    private void OnLinuxReducedMotionChanged(object? sender, EventArgs e)
    {
        RootNavigation.TransitionDuration = 0;
    }

    private void OnCloseWindowRequest(object? sender, EventArgs e)
    {
        CloseWindow();
    }

    private async void OnPageRequest(object? sender, string type)
    {
        try
        {
            if (!_navigating && !(DateTimeOffset.Now.Subtract(_lastNavigation).TotalMilliseconds < 500.0))
            {
                if (type == "next")
                {
                    await NextPageAsync();
                }
                else if (type == "back")
                {
                    BackPage();
                }
                _lastNavigation = DateTimeOffset.Now;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::OnPageRequest", ex);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        Voidstrap.UI.LinuxUiPerformance.ReducedMotionChanged -= OnLinuxReducedMotionChanged;
        base.Closing -= MainWindow_Closing;
        base.Closed -= MainWindow_Closed;
        _viewModel.CloseWindowRequest -= OnCloseWindowRequest;
        _viewModel.PageRequest -= OnPageRequest;
        RootFrame.Navigated -= RootFrame_Navigated;
        RootFrame.SizeChanged -= RootFrame_SizeChanged;
        _linuxTextContent = null;
        RootFrame.Content = null;
        NextPageCallback = null;
        NextPageAsyncCallback = null;
        CancelCallback = null;
        DataContext = null;
    }

    private async System.Threading.Tasks.Task NextPageAsync()
    {
        if (_currentPage == _pages.Last())
            return;
        _navigating = true;
        SetButtonEnabled("next", false);
        SetButtonEnabled("back", false);
        try
        {
            bool proceed = NextPageAsyncCallback is not null
                ? await NextPageAsyncCallback()
                : NextPageCallback?.Invoke() ?? true;
            if (!proceed)
                return;
            Type type = _pages[_pages.IndexOf(_currentPage) + 1];
            Navigate(type);
            SetButtonEnabled("next", type != _pages.Last());
            SetButtonEnabled("back", type != _pages.Last());
        }
        finally
        {
            _navigating = false;
            SetButtonEnabled("back", _currentPage != _pages.First() && _currentPage != _pages.Last());
        }
    }

    private void BackPage()
    {
        if (!(_currentPage == _pages.First()))
        {
            Type type = _pages[_pages.IndexOf(_currentPage) - 1];
            Navigate(type);
            SetButtonEnabled("next", state: true);
            SetButtonEnabled("back", type != _pages.First());
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (App.LaunchSettings.WindowAuditFlag.Active)
        {
            return;
        }
        if (!Finished && Frontend.ShowMessageBox(Strings.Installer_ShouldCancel, MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        CancelCallback?.Invoke();
    }

    public void SetNextButtonText(string text)
    {
        _viewModel.SetNextButtonText(text);
    }

    public void SetButtonEnabled(string type, bool state)
    {
        _viewModel.SetButtonEnabled(type, state);
    }

    public Frame GetFrame()
    {
        return RootFrame;
    }

    public INavigation GetNavigation()
    {
        return RootNavigation;
    }

    public bool Navigate(Type pageType)
    {
        _currentPage = pageType;
        NextPageCallback = null;
        NextPageAsyncCallback = null;
        CancelCallback = null;
        int index = _pages.IndexOf(pageType);
        if (index < 0)
            index = 0;
        _viewModel.SetNextButtonText(Strings.Common_Navigation_Next);
        _viewModel.SetStep(index, HeadingFor(pageType));
        PaintSteps(index);
        ApplyChrome(pageType);
        return RootNavigation.Navigate(pageType);
    }

    private void PaintSteps(int active)
    {
        if (RootNavigation != null && active >= 0 && active < RootNavigation.Items.Count)
        {
            RootNavigation.SelectedPageIndex = active;
        }
    }

    private void ApplyChrome(Type pageType)
    {
        bool animate = IsLoaded && !(Voidstrap.Utility.Platform.IsLinux && Voidstrap.UI.LinuxUiPerformance.ReducedMotion);
        foreach (UIElement old in ArtStage.Children.Cast<UIElement>().ToList())
        {
            if (!animate || old is not Grid oldLayer || oldLayer.Tag is not InstallerArt oldArt)
            {
                ArtStage.Children.Remove(old);
                continue;
            }
            Leave(oldArt);
            DoubleAnimation fade = new(0, TimeSpan.FromMilliseconds(120)) { BeginTime = TimeSpan.FromMilliseconds(300) };
            fade.Completed += (_, _) => ArtStage.Children.Remove(oldLayer);
            oldLayer.BeginAnimation(OpacityProperty, fade);
        }
        InstallerArt art = InstallerArt.For(pageType);
        Grid layer = new() { Tag = art };
        layer.Children.Add(art.Back);
        layer.Children.Add(art.Badge);
        if (art.Front != null)
            layer.Children.Add(art.Front);
        foreach (FrameworkElement part in Parts(art))
            Rig(part);
        Rig(art.Badge).Rotate.Angle = art.BadgeAngle;
        ArtStage.Children.Add(layer);
        if (animate)
            Enter(art, TimeSpan.FromMilliseconds(ArtStage.Children.Count > 1 ? 260 : 60));
    }

    private static IEnumerable<FrameworkElement> Parts(InstallerArt art) =>
        art.Front == null ? [art.Back] : [art.Back, art.Front];

    private static (ScaleTransform Scale, RotateTransform Rotate, TranslateTransform Move) Rig(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup { Children.Count: 3 } existing)
            return ((ScaleTransform)existing.Children[0], (RotateTransform)existing.Children[1], (TranslateTransform)existing.Children[2]);
        ScaleTransform scale = new();
        RotateTransform rotate = new();
        TranslateTransform move = new();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
        return (scale, rotate, move);
    }

    private static void Play(DependencyObject target, DependencyProperty property, double from, double to, double ms, double delay, IEasingFunction? ease = null)
    {
        DoubleAnimation animation = new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        target.SetValue(property, from);
        ((IAnimatable)target).BeginAnimation(property, animation);
    }

    private static void Enter(InstallerArt art, TimeSpan delay)
    {
        double d = delay.TotalMilliseconds;
        CubicEase pop = new() { EasingMode = EasingMode.EaseOut };
        foreach (FrameworkElement part in Parts(art))
        {
            var rig = Rig(part);
            Play(rig.Scale, ScaleTransform.ScaleXProperty, 0.82, 1, 460, d, pop);
            Play(rig.Scale, ScaleTransform.ScaleYProperty, 0.82, 1, 460, d, pop);
            Play(rig.Move, TranslateTransform.YProperty, 14, 0, 460, d, pop);
            Play(part, OpacityProperty, 0, 1, 300, d);
        }
        var badge = Rig(art.Badge);
        double bd = d + 220;
        BackEase spring = new() { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        Play(art.Badge, OpacityProperty, 0, 1, 180, bd);
        Play(badge.Move, TranslateTransform.YProperty, art.BadgeRise, 0, 620, bd, spring);
        Play(badge.Rotate, RotateTransform.AngleProperty, art.BadgeAngle + 28, art.BadgeAngle, 720, bd, spring);
        Play(badge.Scale, ScaleTransform.ScaleXProperty, 0.6, 1, 620, bd, spring);
        Play(badge.Scale, ScaleTransform.ScaleYProperty, 0.6, 1, 620, bd, spring);
    }

    private static void Leave(InstallerArt art)
    {
        CubicEase ease = new() { EasingMode = EasingMode.EaseIn };
        var badge = Rig(art.Badge);
        Play(badge.Move, TranslateTransform.YProperty, 0, art.BadgeRise, 220, 0, ease);
        Play(badge.Rotate, RotateTransform.AngleProperty, art.BadgeAngle, art.BadgeAngle + 18, 220, 0, ease);
        Play(art.Badge, OpacityProperty, 1, 0, 200, 0, ease);
        foreach (FrameworkElement part in Parts(art))
        {
            var rig = Rig(part);
            Play(rig.Scale, ScaleTransform.ScaleXProperty, 1, 0.88, 260, 100, ease);
            Play(rig.Scale, ScaleTransform.ScaleYProperty, 1, 0.88, 260, 100, ease);
            Play(part, OpacityProperty, 1, 0, 220, 120, ease);
        }
    }

    private static string HeadingFor(Type pageType)
    {
        if (pageType == typeof(InstallPage))
            return Strings.Installer_Install_Title;
        if (pageType == typeof(ChannelPage))
            return "Channel";
        if (pageType == typeof(InstallerModsPage))
            return Strings.Menu_Mods_Title;
        if (pageType == typeof(InstallerAppearancePage))
            return Strings.Menu_Appearance_Title;
        if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.DownloadsPage))
            return "Manager";
        if (pageType == typeof(Voidstrap.UI.Elements.Settings.Pages.ExtensionPage))
            return "Extensions";
        if (pageType == typeof(CompletionPage))
            return Strings.Installer_Completion_Title;
        return Strings.Installer_Install_Title;
    }

    public void SetPageService(IPageService pageService)
    {
        RootNavigation.PageService = pageService;
    }

    public void ShowWindow()
    {
        Show();
    }

    public void CloseWindow()
    {
        Close();
    }

    private void RootFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (Voidstrap.Utility.Platform.UsesPortableUi && e.Content is DependencyObject content)
        {
            _linuxTextContent = content;
            Voidstrap.UI.LinuxTextGuard.AttachOwner(content, this);
        }
    }

    private void RootFrame_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && _linuxTextContent != null)
        {
            Voidstrap.UI.LinuxTextGuard.CorrectOwner(_linuxTextContent, this);
        }
    }
}
