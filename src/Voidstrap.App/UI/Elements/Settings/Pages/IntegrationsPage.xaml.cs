using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class IntegrationsPage : UiPage
{
    private readonly IntegrationsViewModel _viewModel;
    private Window? _ownerWindow;

    public IntegrationsPage()
    {
        ActivityWatcher? activeWatcher = Watcher.Current?.ActivityWatcher;
        _viewModel = new IntegrationsViewModel(activeWatcher ?? new ActivityWatcher(), activeWatcher == null);
        base.DataContext = _viewModel;
        InitializeComponent();
        MatchmakerTab.DataContext = new BehaviourViewModel();
        VoidEditPanel.DataContext = new ClipperViewModel();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Window? owner = Window.GetWindow(this);
        if (!ReferenceEquals(_ownerWindow, owner))
        {
            if (_ownerWindow != null)
            {
                _ownerWindow.Closed -= OnOwnerWindowClosed;
            }
            _ownerWindow = owner;
            if (_ownerWindow != null)
            {
                _ownerWindow.Closed += OnOwnerWindowClosed;
            }
        }
        _viewModel.OnPropertyChanged("UncapFpsToggleEnabled");
        _viewModel.OnPropertyChanged("UncapFPS");
        StartRpcPreview();
        _ = RefreshVoidEditStateAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        StopRpcPreview();
        if (_ownerWindow != null)
        {
            _ownerWindow.Closed -= OnOwnerWindowClosed;
            _ownerWindow = null;
        }
    }

    private void OnOwnerWindowClosed(object? sender, EventArgs e)
    {
        if (_ownerWindow != null)
        {
            _ownerWindow.Closed -= OnOwnerWindowClosed;
            _ownerWindow = null;
        }
        Loaded -= OnPageLoaded;
        Unloaded -= OnPageUnloaded;
        StopRpcPreview();
        _viewModel.Dispose();
        DataContext = null;
    }

    private void ValidateUInt32(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !uint.TryParse(e.Text, out var _);
    }

    private async void OpenVoidEdit_Click(object sender, RoutedEventArgs e)
    {
        VoidEditOpenButton.IsEnabled = false;
        VoidEditInstallButton.IsEnabled = false;
        try
        {
            await VoidEditLauncher.OpenAsync();
        }
        finally
        {
            VoidEditOpenButton.IsEnabled = true;
            await RefreshVoidEditStateAsync();
        }
    }

    private async void InstallVoidEdit_Click(object sender, RoutedEventArgs e)
    {
        VoidEditOpenButton.IsEnabled = false;
        VoidEditInstallButton.IsEnabled = false;
        try
        {
            await VoidEditLauncher.InstallAsync();
        }
        finally
        {
            VoidEditOpenButton.IsEnabled = true;
            await RefreshVoidEditStateAsync();
        }
    }

    private async Task RefreshVoidEditStateAsync()
    {
        VoidEditLauncher.InstallState state = await VoidEditLauncher.GetStateAsync();
        VoidEditInstallButton.Content = state switch
        {
            VoidEditLauncher.InstallState.Installed => Strings.Ui_VoidstrapMods_LibraryInstalled,
            VoidEditLauncher.InstallState.UpdateAvailable => Strings.Ui_VoidstrapMods_LibraryUpdate,
            _ => Strings.Ui_VoidstrapMods_LibraryInstall,
        };
        VoidEditInstallButton.IsEnabled = state != VoidEditLauncher.InstallState.Installed;
    }

    private void ResetDatacenters_Click(object sender, RoutedEventArgs e)
    {
        if (MatchmakerTab.DataContext is BehaviourViewModel behaviourViewModel)
        {
            behaviourViewModel.ResetDatacenters();
        }
    }
}
