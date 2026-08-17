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

	public BehaviourPage()
	{
		base.DataContext = new BehaviourViewModel();
		InitializeComponent();
	}

	private void ResetDatacenters_Click(object sender, RoutedEventArgs e)
	{
		if (base.DataContext is BehaviourViewModel behaviourViewModel)
		{
			behaviourViewModel.ResetDatacenters();
		}
	}
}
