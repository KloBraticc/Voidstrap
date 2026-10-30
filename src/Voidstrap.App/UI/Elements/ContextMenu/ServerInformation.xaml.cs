using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.ContextMenu;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ServerInformation : WpfUiWindow
{
    private readonly ServerInformationViewModel _viewModel;

    public ServerInformation(Watcher watcher)
    {
        _viewModel = new ServerInformationViewModel(watcher);
        base.DataContext = _viewModel;
        InitializeComponent();
        _body = base.Content as FrameworkElement;
        if (_body != null)
        {
            _body.IsVisibleChanged += OnBodyVisibleChanged;
            _viewModel.SetActive(_body.IsVisible);
        }
        base.Closed += ServerInformation_Closed;
    }

    private readonly FrameworkElement? _body;

    private void OnBodyVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _viewModel.SetActive(e.NewValue is true);
    }

    // IsCancel alone only closes modal dialogs, this window is shown normally or hosted in a dock panel,
    // where closing the view closes the panel around it
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ServerInformation_Closed(object? sender, EventArgs e)
    {
        base.Closed -= ServerInformation_Closed;
        if (_body != null)
            _body.IsVisibleChanged -= OnBodyVisibleChanged;
        _viewModel.Dispose();
        base.DataContext = null;
    }
}
