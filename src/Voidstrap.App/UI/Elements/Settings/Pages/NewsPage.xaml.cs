using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class NewsPage : UiPage
{
    private readonly NewsViewModel _viewModel = new();
    private readonly DispatcherTimer _rotation = new() { Interval = TimeSpan.FromSeconds(7) };
    private bool _hoveringFeatured;

    public NewsPage()
    {
        DataContext = _viewModel;
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _rotation.Tick -= Rotation_Tick;
        _rotation.Tick += Rotation_Tick;
        IsVisibleChanged -= Page_IsVisibleChanged;
        IsVisibleChanged += Page_IsVisibleChanged;
        _rotation.IsEnabled = IsVisible;
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _rotation.Stop();
        _rotation.Tick -= Rotation_Tick;
        IsVisibleChanged -= Page_IsVisibleChanged;
    }

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _rotation.IsEnabled = IsVisible;
    }

    private void Rotation_Tick(object? sender, EventArgs e)
    {
        if (!_hoveringFeatured)
            _viewModel.StepFeatured(1);
    }

    private void Featured_MouseEnter(object sender, MouseEventArgs e) => _hoveringFeatured = true;

    private void Featured_MouseLeave(object sender, MouseEventArgs e) => _hoveringFeatured = false;
}
