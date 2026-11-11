using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Dialogs;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class UninstallerDialog : WpfUiWindow
{
    private readonly UninstallerViewModel _viewModel;

    public bool Confirmed { get; private set; }

    public UninstallOptions Options { get; private set; } = UninstallOptions.Default;

    public UninstallerDialog()
    {
        _viewModel = new UninstallerViewModel();
        _viewModel.ConfirmUninstallRequest += OnConfirmUninstallRequest;
        base.DataContext = _viewModel;
        InitializeComponent();
        base.Closed += OnClosed;
    }

    private void OnConfirmUninstallRequest(object? sender, EventArgs e)
    {
        Confirmed = true;
        Options = new UninstallOptions(_viewModel.KeepData, _viewModel.KeepVersions, _viewModel.KeepLogs);
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.ConfirmUninstallRequest -= OnConfirmUninstallRequest;
        base.Closed -= OnClosed;
    }
}
