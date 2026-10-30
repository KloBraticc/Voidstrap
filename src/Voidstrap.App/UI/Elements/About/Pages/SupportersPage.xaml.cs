using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.UI.ViewModels.About;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.About.Pages;

public partial class SupportersPage : UiPage
{
    private readonly SupportersViewModel _viewModel = new SupportersViewModel();

    private Window? _owner;

    public SupportersPage()
    {
        base.DataContext = _viewModel;
        InitializeComponent();
        Loaded += UiPage_Loaded;
    }

    private void UiPage_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.UpdateColumns(ActualWidth);
        Window? window = Window.GetWindow(this);
        if (window == null || ReferenceEquals(window, _owner))
        {
            return;
        }

        if (_owner != null)
        {
            _owner.Closed -= Owner_Closed;
        }
        _owner = window;
        _owner.Closed += Owner_Closed;
    }

    private void Owner_Closed(object? sender, EventArgs e)
    {
        if (_owner != null)
        {
            _owner.Closed -= Owner_Closed;
            _owner = null;
        }
        _viewModel.Dispose();
    }

    private void UiPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.WindowResizeEvent?.Invoke(sender, e);
    }
}
