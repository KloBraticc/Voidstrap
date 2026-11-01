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

    private VoidEditLauncher.InstallState _voidEditState = VoidEditLauncher.InstallState.NotInstalled;

    private async void VoidEditCard_Click(object sender, RoutedEventArgs e)
    {
        VoidEditCard.IsEnabled = false;
        try
        {
            if (_voidEditState == VoidEditLauncher.InstallState.Installed)
                await VoidEditLauncher.OpenAsync();
            else
                await VoidEditLauncher.InstallAsync();
        }
        finally
        {
            await RefreshVoidEditStateAsync();
        }
    }

    private async Task RefreshVoidEditStateAsync()
    {
        _voidEditState = await VoidEditLauncher.GetStateAsync();
        VoidEditCardStatus.Text = _voidEditState switch
        {
            VoidEditLauncher.InstallState.Installed => Strings.Ui_VoidEdit_Card_Open,
            VoidEditLauncher.InstallState.UpdateAvailable => Strings.Ui_VoidEdit_Card_Update,
            _ => Strings.Ui_VoidEdit_Card_Install,
        };
        VoidEditCard.IsEnabled = true;
    }

    private void ResetDatacenters_Click(object sender, RoutedEventArgs e)
    {
        if (MatchmakerTab.DataContext is BehaviourViewModel behaviourViewModel)
        {
            behaviourViewModel.ResetDatacenters();
        }
    }
}
