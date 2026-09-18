using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class BehaviourPage : UiPage{

	private int _settingsRevision;

	public BehaviourPage()
	{
		base.DataContext = new BehaviourViewModel();
		InitializeComponent();
		_settingsRevision = App.Settings.Revision;
		base.Loaded += OnPageLoaded;
		base.Unloaded += OnPageUnloaded;
	}

	private void OnPageLoaded(object sender, RoutedEventArgs e)
	{
		if (base.DataContext is BehaviourViewModel behaviourViewModel)
		{
			if (App.Settings.Revision != _settingsRevision)
			{
				behaviourViewModel.OnPropertyChanged(string.Empty);
			}
			behaviourViewModel.RefreshExcludedGames();
		}
	}

	private void OnPageUnloaded(object sender, RoutedEventArgs e)
	{
		_settingsRevision = App.Settings.Revision;
	}

	private void ResetDatacenters_Click(object sender, RoutedEventArgs e)
	{
		if (base.DataContext is BehaviourViewModel behaviourViewModel)
		{
			behaviourViewModel.ResetDatacenters();
		}
	}
}
