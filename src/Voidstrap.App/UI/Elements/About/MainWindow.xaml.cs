using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Voidstrap.UI.Elements.Base;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.About;

public partial class MainWindow : WpfUiWindow, INavigationWindow
{

    public MainWindow()
    {
        InitializeComponent();
        Voidstrap.UI.NavigationSoundSuppressor.Attach(RootFrame);
        App.Logger.WriteLine("MainWindow", "Initializing about window");
        if (Locale.CurrentCulture.Name.StartsWith("tr", StringComparison.Ordinal))
            TranslatorsText.FontSize = 9.0;
    }

    protected override void OnClosed(EventArgs e)
    {
        Voidstrap.UI.NavigationSoundSuppressor.Detach(RootFrame);
        base.OnClosed(e);
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
        return RootNavigation.Navigate(pageType);
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

}
