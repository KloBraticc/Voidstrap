using System;
using System.Windows;
using Wpf.Ui.Controls;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class MorePage : UiPage
{
	public MorePage()
	{
		InitializeComponent();
	}

	private void OpenGlobal_Click(object sender, RoutedEventArgs e)
	{
		Open(typeof(GBSEditorPage));
	}

	private void OpenShortcuts_Click(object sender, RoutedEventArgs e)
	{
		Open(typeof(ShortcutsPage));
	}

	private void OpenNews_Click(object sender, RoutedEventArgs e)
	{
		Open(typeof(NewsPage));
	}

	private void Open(Type pageType)
	{
		if (Window.GetWindow(this) is INavigationWindow navigationWindow)
			navigationWindow.Navigate(pageType);
	}
}
