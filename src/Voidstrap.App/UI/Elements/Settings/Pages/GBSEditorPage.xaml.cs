using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class GBSEditorPage : UiPage
{
    private GBSEditorViewModel _viewModel = null!;

    private bool _syncingSectionBar;

    public GBSEditorPage()
    {
        SetupViewModel();
        InitializeComponent();
        base.Loaded += OnPageLoaded;
        base.Unloaded += OnPageUnloaded;
    }

    private void SetupViewModel()
    {
        _viewModel = new GBSEditorViewModel();
        base.DataContext = _viewModel;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            SetupViewModel();
            return;
        }
        _viewModel.Resume();
        UpdateActiveSectionFromScroll();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel?.Dispose();
    }

    private void SectionBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSectionBar)
            return;
        if (SectionBar.SelectedItem is FrameworkElement { Tag: FrameworkElement target })
            target.BringIntoView(new Rect(0, 0, target.ActualWidth, Math.Max(target.ActualHeight, 400)));
    }

    private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateActiveSectionFromScroll();
    }

    private void UpdateActiveSectionFromScroll()
    {
        FrameworkElement? firstItem = null;
        FrameworkElement? lastItem = null;
        FrameworkElement? activeItem = null;

        foreach (object rawItem in SectionBar.Items)
        {
            if (rawItem is not FrameworkElement { Tag: FrameworkElement target } item)
                continue;
            if (item.Visibility != Visibility.Visible || !target.IsVisible || !ContentScroll.IsAncestorOf(target))
                continue;

            firstItem ??= item;
            lastItem = item;

            if (target.TransformToAncestor(ContentScroll).Transform(new Point(0, 0)).Y <= 48.0)
                activeItem = item;
        }

        if (firstItem is null)
            return;

        if (ContentScroll.ScrollableHeight > 0 && ContentScroll.VerticalOffset >= ContentScroll.ScrollableHeight - 2.0)
            activeItem = lastItem;

        activeItem ??= firstItem;

        if (ReferenceEquals(SectionBar.SelectedItem, activeItem))
            return;

        _syncingSectionBar = true;
        try
        {
            SectionBar.SelectedItem = activeItem;
        }
        finally
        {
            _syncingSectionBar = false;
        }
    }

    private void ValidateUInt32(object sender, TextCompositionEventArgs e)
    {
        foreach (char character in e.Text)
        {
            if (!char.IsAsciiDigit(character))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void ValidateFloat(object sender, TextCompositionEventArgs e)
    {
        foreach (char character in e.Text)
        {
            if (!char.IsAsciiDigit(character) && character != '.')
            {
                e.Handled = true;
                return;
            }
        }
    }
}
