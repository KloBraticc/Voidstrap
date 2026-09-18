using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Utility;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu.InstancePages;

public partial class InstancesPage : UiPage
{
	private readonly DispatcherTimer _refreshTimer;

	private bool _active;

	private DateTime _actionStatusUntil;

	public InstancesPage()
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
		LoadAccounts();
		Refresh();
		_refreshTimer.Start();
		await RobloxInstanceManager.EnsureCurrentAccountSavedAsync();
		if (_active)
		{
			LoadAccounts();
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

	private void LoadAccounts()
	{
		long selectedUser = AccountPicker.SelectedItem is RobloxInstanceAccount previous ? previous.UserId : 0;
		IReadOnlyList<RobloxInstanceAccount> accounts = RobloxInstanceManager.GetAccounts();
		AccountPicker.ItemsSource = accounts;
		int index = 0;
		for (int i = 0; i < accounts.Count; i++)
		{
			if (accounts[i].UserId == selectedUser)
			{
				index = i;
				break;
			}
		}
		AccountPicker.SelectedIndex = index;
		_ = RobloxInstanceManager.LoadAvatarsAsync(accounts);
		AccountHint.Text = accounts.Count > 1
			? accounts.Count - 1 + " saved account(s) available"
			: "Save accounts in the account switcher to use them here";
	}

	private void Refresh()
	{
		if (!_active)
		{
			return;
		}
		IReadOnlyList<RobloxInstanceInfo> instances = RobloxInstanceManager.GetRunningInstances();
		InstanceList.ItemsSource = instances;
		EmptyState.Visibility = instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		CloseAllButton.IsEnabled = instances.Count > 0;
		SummaryText.Text = instances.Count switch
		{
			0 => "No instances running",
			1 => "1 instance running",
			_ => instances.Count + " instances running"
		};
		bool canLaunch = MultiInstanceLock.Enabled;
		LaunchButton.IsEnabled = canLaunch;
		LaunchStudioButton.IsEnabled = canLaunch;
		AccountPicker.IsEnabled = canLaunch;
		if (!canLaunch)
		{
			ShowStatus("Multi instance launching is off, so launching from here is disabled. Turn it on from the Deployment page, General tab.");
		}
		else if (DateTime.UtcNow >= _actionStatusUntil)
		{
			StatusBar.Visibility = Visibility.Collapsed;
		}
	}

	private void ShowStatus(string message)
	{
		if (!string.Equals(StatusText.Text, message, StringComparison.Ordinal))
		{
			StatusText.Text = message;
		}
		StatusBar.Visibility = Visibility.Visible;
	}

	private void SetStatus(string message)
	{
		_actionStatusUntil = DateTime.UtcNow.AddSeconds(6.0);
		ShowStatus(message);
	}

	private async void Launch_Click(object sender, RoutedEventArgs e)
	{
		await LaunchAsync(studio: false);
	}

	private async void LaunchStudio_Click(object sender, RoutedEventArgs e)
	{
		await LaunchAsync(studio: true);
	}

	private async Task LaunchAsync(bool studio)
	{
		RobloxInstanceAccount? account = AccountPicker.SelectedItem as RobloxInstanceAccount;
		LaunchButton.IsEnabled = false;
		LaunchStudioButton.IsEnabled = false;
		SetStatus("Starting another instance...");
		try
		{
			string? error = await RobloxInstanceManager.LaunchAsync(account, studio);
			if (error != null)
			{
				SetStatus(error);
				return;
			}
			SetStatus(account != null && account.UserId != 0
				? "Starting Roblox as " + account.Title + ". It will appear here once the window opens."
				: "Starting Roblox. It will appear here once the window opens.");
		}
		finally
		{
			bool canLaunch = MultiInstanceLock.Enabled;
			LaunchButton.IsEnabled = canLaunch;
			LaunchStudioButton.IsEnabled = canLaunch;
		}
	}

	private void FocusInstance_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement element && element.Tag is int processId && !RobloxInstanceManager.Focus(processId))
		{
			SetStatus("That instance has no window to focus yet.");
		}
	}

	private void CloseInstance_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement element || element.Tag is not int processId)
		{
			return;
		}
		SetStatus(RobloxInstanceManager.Close(processId)
			? "Closed instance " + processId + "."
			: "Instance " + processId + " could not be closed.");
		Refresh();
	}

	private void CloseAll_Click(object sender, RoutedEventArgs e)
	{
		if (Frontend.ShowMessageBox("Close every running Roblox instance? Unsaved progress in those sessions will be lost.", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
		{
			return;
		}
		int closed = RobloxInstanceManager.CloseAll();
		SetStatus(closed == 0 ? "No instances were closed." : "Closed " + closed + " instance(s).");
		Refresh();
	}
}
