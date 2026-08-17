using System;
using System.Diagnostics;
using System.Windows;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class MobilePage : UiPage
{
	private const string RemoteDesktopUrl = "https://remotedesktop.google.com/access";
	private bool _opened;

	public MobilePage()
	{
		InitializeComponent();
		Loaded += OnPageLoaded;
		Unloaded += OnPageUnloaded;
	}

	private void OnPageLoaded(object sender, RoutedEventArgs e)
	{
		if (_opened)
		{
			return;
		}
		_opened = true;
		InstallerProgressBar.Value = 100.0;
		InstallerCard.Visibility = Visibility.Collapsed;
		CompletionCard.Visibility = Visibility.Visible;
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = RemoteDesktopUrl,
				UseShellExecute = true
			});
			Frontend.ShowMessageBox("Chrome Remote Desktop is open. Follow the official setup instructions to configure this PC, then open the same page on your tablet or phone.");
		}
		catch (Exception)
		{
			StatusText.Text = "Chrome Remote Desktop could not be opened. Copy this address into your browser: " + RemoteDesktopUrl;
			InstallerCard.Visibility = Visibility.Visible;
			CompletionCard.Visibility = Visibility.Collapsed;
		}
	}

	private void OnPageUnloaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnPageLoaded;
		Unloaded -= OnPageUnloaded;
	}
}
