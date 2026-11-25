using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class BehaviourPage : UiPage
{

    private int _settingsRevision;
    private bool _refreshingControllers;

    public BehaviourPage()
    {
        base.DataContext = new BehaviourViewModel();
        InitializeComponent();
        _settingsRevision = App.Settings.Revision;
        base.Loaded += OnPageLoaded;
        base.Unloaded += OnPageUnloaded;
    }

    public void CustomIntegrationSelection(object sender, SelectionChangedEventArgs e)
    {
        if (RobloxIntegrations.DataContext is IntegrationsViewModel viewModel)
        {
            viewModel.SelectedCustomIntegration = (CustomIntegration)((ListBox)sender).SelectedItem;
            viewModel.OnPropertyChanged("SelectedCustomIntegration");
        }
    }

    private void OpenAdvancedGeneral_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is BehaviourViewModel viewModel)
            NavigationService?.Navigate(new AdvancedGeneralPage(viewModel));
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ActivityWatcher? watcher = Watcher.Current?.ActivityWatcher;
        RobloxIntegrations.DataContext = new IntegrationsViewModel(watcher ?? new ActivityWatcher(), watcher == null);
        if (base.DataContext is BehaviourViewModel behaviourViewModel)
        {
            if (App.Settings.Revision != _settingsRevision)
            {
                behaviourViewModel.OnPropertyChanged(string.Empty);
            }
            behaviourViewModel.RefreshExcludedGames();
            behaviourViewModel.RefreshWindowIconOverrides();
            if (behaviourViewModel.Mtu.RefreshCommand.CanExecute(null))
                behaviourViewModel.Mtu.RefreshCommand.Execute(null);
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        (RobloxIntegrations.DataContext as IDisposable)?.Dispose();
        RobloxIntegrations.DataContext = null;
        _settingsRevision = App.Settings.Revision;
        if (base.DataContext is BehaviourViewModel behaviourViewModel)
            behaviourViewModel.Mtu.RefreshCommand.Cancel();
    }

    private void OpenAdvancedMatchmaker_Click(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is BehaviourViewModel behaviourViewModel)
        {
            NavigationService?.Navigate(new AdvancedMatchmakerPage(behaviourViewModel));
        }
    }

    private async void ControllerPreview_Click(object sender, RoutedEventArgs e)
    {
        ControllerPreviewButton.IsEnabled = false;
        SetControllerPreviewStatus(nameof(Strings.Ui_Controller_Previewing));
        try
        {
            DualSenseFeedback.Presets presets = DualSenseFeedback.ReadPresets(App.Settings.Prop);
            DualSenseFeedback.PreviewResult result = await Task.Run(() => DualSenseFeedback.PreviewAsync(presets));
            SetControllerPreviewStatus(result switch
            {
                DualSenseFeedback.PreviewResult.Sent => nameof(Strings.Ui_Controller_Previewed),
                DualSenseFeedback.PreviewResult.NoPreset => nameof(Strings.Ui_Controller_SelectPreset),
                DualSenseFeedback.PreviewResult.NoController => nameof(Strings.Ui_Controller_NoController),
                _ => nameof(Strings.Ui_Controller_PreviewFailed)
            });
        }
        finally
        {
            ControllerPreviewButton.IsEnabled = true;
        }
    }

    private async void DeploymentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ControllerTab != null && ReferenceEquals(sender, e.OriginalSource)
            && ReferenceEquals(DeploymentTabs.SelectedItem, ControllerTab))
            await RefreshControllersAsync();
    }

    private async void ControllerRefresh_Click(object sender, RoutedEventArgs e) => await RefreshControllersAsync();

    private async Task RefreshControllersAsync()
    {
        if (_refreshingControllers || DataContext is not BehaviourViewModel viewModel)
            return;
        _refreshingControllers = true;
        try
        {
            await viewModel.RefreshControllerDevicesAsync();
        }
        finally
        {
            _refreshingControllers = false;
        }
    }

    private void SetControllerPreviewStatus(string resourceKey)
    {
        ControllerPreviewStatus.SetBinding(TextBlock.TextProperty, new Binding($"[{resourceKey}]") { Source = LocalizedStrings.Instance });
    }
}
