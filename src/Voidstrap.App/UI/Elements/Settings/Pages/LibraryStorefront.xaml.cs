using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Voidstrap.Models.Entities;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class LibraryStorefront : UserControl
{
    private readonly DispatcherTimer _rotation = new() { Interval = TimeSpan.FromSeconds(6) };

    public LibraryStorefront()
    {
        InitializeComponent();
    }

    private LibraryStorefrontViewModel? Storefront => (DataContext as LibraryViewModel)?.Storefront;

    private async void Control_Loaded(object sender, RoutedEventArgs e)
    {
        _rotation.Tick -= Rotation_Tick;
        _rotation.Tick += Rotation_Tick;
        IsVisibleChanged -= Control_IsVisibleChanged;
        IsVisibleChanged += Control_IsVisibleChanged;
        _rotation.IsEnabled = IsVisible;
        if (Storefront != null)
            await Storefront.LoadAsync();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e)
    {
        _rotation.Stop();
        _rotation.Tick -= Rotation_Tick;
        IsVisibleChanged -= Control_IsVisibleChanged;
    }

    private void Control_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _rotation.IsEnabled = IsVisible;
    }

    private void Rotation_Tick(object? sender, EventArgs e)
    {
        Storefront?.NextFeatured();
    }

    private void TabRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StoreGameEntry game } && Storefront != null)
            Storefront.PreviewGame = game;
    }
}
