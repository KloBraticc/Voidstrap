using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Utility;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu.InstancePages;

public partial class InstanceAccountsPage : UiPage
{
	private readonly DispatcherTimer _refreshTimer;

	private bool _active;

	public InstanceAccountsPage()
	{
		InitializeComponent();
		_refreshTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(1500.0)
		};
		_refreshTimer.Tick += RefreshTimer_Tick;
	}

	private async void UiPage_Loaded(object sender, RoutedEventArgs e)
	{
		_active = true;
		Refresh();
		_refreshTimer.Start();
		if (await RobloxInstanceManager.EnsureCurrentAccountSavedAsync() && _active)
		{
			Refresh();
		}
	}

	private void UiPage_Unloaded(object sender, RoutedEventArgs e)
	{
		_active = false;
		_refreshTimer.Stop();
	}

	private void RefreshTimer_Tick(object? sender, EventArgs e)
	{
		Refresh();
	}

	private void Refresh()
	{
		if (!_active)
		{
			return;
		}
		List<RobloxInstanceAccount> accounts = [.. RobloxInstanceManager.GetAccounts().Where(account => account.UserId != 0)];
		if (AccountList.ItemsSource is List<RobloxInstanceAccount> current && current.Count == accounts.Count
			&& current.Select(item => item.UserId).SequenceEqual(accounts.Select(item => item.UserId)))
		{
			AccountList.IsEnabled = MultiInstanceLock.Enabled;
			return;
		}
		AccountList.ItemsSource = accounts;
		_ = RobloxInstanceManager.LoadAvatarsAsync(accounts);
		EmptyState.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		AccountList.IsEnabled = MultiInstanceLock.Enabled;
		if (!MultiInstanceLock.Enabled)
		{
			StatusText.Text = "Multi instance launching is off, so launching from here is disabled. Turn it on from the Deployment page, General tab.";
		}
	}

	private async void LaunchAccount_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement element || element.Tag is not RobloxInstanceAccount account)
		{
			return;
		}
		element.IsEnabled = false;
		StatusText.Text = "Starting Roblox as " + account.Title + "...";
		try
		{
			string? error = await RobloxInstanceManager.LaunchAsync(account, studio: false);
			StatusText.Text = error ?? "Started Roblox as " + account.Title + ".";
		}
		finally
		{
			element.IsEnabled = true;
		}
	}
}
