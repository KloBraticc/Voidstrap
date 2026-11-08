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
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel?.Dispose();
    }

    private void SectionBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionBar.SelectedItem is FrameworkElement { Tag: FrameworkElement target })
            target.BringIntoView(new Rect(0, 0, target.ActualWidth, Math.Max(target.ActualHeight, 400)));
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
