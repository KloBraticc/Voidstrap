using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class BehaviourPage : UiPage
{

    private int _settingsRevision;

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
}
