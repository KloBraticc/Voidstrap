using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.Elements.Dialogs;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ChannelPage : UiPage{

	private CancellationTokenSource _versionCts;
	private bool _resetInProgress;

	public ChannelPage()
	{
		InitializeComponent();
		base.DataContext = new ChannelViewModel();
	}

	private void OnChannelPageLoaded(object sender, RoutedEventArgs e)
	{
		if (base.DataContext is not ChannelViewModel)
		{
			base.DataContext = new ChannelViewModel();
		}
		if (_versionCts != null)
		{
			return;
		}
		_versionCts = new CancellationTokenSource();
		_ = AutoUpdateRobloxVersionAsync(_versionCts.Token);
	}

	private void OnChannelPageUnloaded(object sender, RoutedEventArgs e)
	{
		try
		{
			_versionCts?.Cancel();
			_versionCts?.Dispose();
		}
		catch
		{
		}
		_versionCts = null;
		if (base.DataContext is ChannelViewModel viewModel)
		{
			viewModel.Dispose();
			base.DataContext = null;
		}
	}

	private async Task AutoUpdateRobloxVersionAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				await GetRobloxVersionAPPAsync(token);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception)
			{
			}
			try
			{
				await Task.Delay(30000, token);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	private async Task GetRobloxVersionAPPAsync(CancellationToken token)
	{
		try
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "LocalStorage");
			if (!Directory.Exists(path))
			{
				RobloxVersionAPP.Header = "Not Installed";
				return;
			}
			string[] files = Directory.EnumerateFiles(path, "memProfStorage*.json", SearchOption.TopDirectoryOnly)
				.Select(file => new FileInfo(file))
				.Where(file => file.Length <= 1024 * 1024)
				.OrderByDescending(file => file.LastWriteTimeUtc)
				.Take(20)
				.Select(file => file.FullName)
				.ToArray();
			if (files.Length == 0)
			{
				RobloxVersionAPP.Header = "Not Installed";
				return;
			}
			string version = null;
			string[] array = files;
			foreach (string path2 in array)
			{
				try
				{
					token.ThrowIfCancellationRequested();
					Match match = Regex.Match(await ReadLocalTextBoundedAsync(path2, 1024 * 1024, token), "\"AppVersion\"\\s*:\\s*\"([^\"]+)\"");
					if (match.Success)
					{
						version = match.Groups[1].Value;
						break;
					}
				}
				catch (IOException)
				{
				}
			}
			if (!string.IsNullOrWhiteSpace(version))
			{
				RobloxVersionAPP.Header = "Roblox " + version;
			}
			else
			{
				RobloxVersionAPP.Header = "Not Installed";
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex2)
		{
			RobloxVersionAPP.Header = "Roblox Version Error: " + ex2.Message;
		}
	}

	private static async Task<string> ReadLocalTextBoundedAsync(string path, int maxBytes, CancellationToken token)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length < 0 || stream.Length > maxBytes)
			throw new InvalidDataException("Roblox version file is too large");
		byte[] bytes = new byte[checked((int)stream.Length)];
		await stream.ReadExactlyAsync(bytes, token);
		return Encoding.UTF8.GetString(bytes);
	}

	private void ApplyNow_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			string sourcePath = App.Settings.FileLocation;
			string sourcePath2 = Paths.Mods;
			foreach (string item in (from d in Directory.GetDirectories(folderPath)
				where d.EndsWith("strap", StringComparison.OrdinalIgnoreCase) && !d.EndsWith("Voidstrap", StringComparison.OrdinalIgnoreCase)
				select d).ToList())
			{
				string text = Path.Combine(item, "Settings.json");
				string text2 = Path.Combine(item, "Modifications");
				BackupIfExists(text);
				BackupIfExists(text2);
				SafeCopy(sourcePath, text);
				SafeCopy(sourcePath2, text2);
			}
			Frontend.ShowMessageBox("Voidstrap Settings/Mods Synced");
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Error: " + ex.Message);
		}
	}

	private void SafeCopy(string sourcePath, string destPath)
	{
		if (File.Exists(sourcePath))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(destPath));
			File.Copy(sourcePath, destPath, overwrite: true);
		}
		else if (Directory.Exists(sourcePath))
		{
			CopyDirectory(sourcePath, destPath);
		}
	}

	private void CopyDirectory(string sourceDir, string destDir)
	{
		Directory.CreateDirectory(destDir);
		string[] files = Directory.GetFiles(sourceDir);
		foreach (string text in files)
		{
			string destFileName = Path.Combine(destDir, Path.GetFileName(text));
			File.Copy(text, destFileName, overwrite: true);
		}
		files = Directory.GetDirectories(sourceDir);
		foreach (string text2 in files)
		{
			string destDir2 = Path.Combine(destDir, Path.GetFileName(text2));
			CopyDirectory(text2, destDir2);
		}
	}

	private void BackupIfExists(string path)
	{
		if (File.Exists(path))
		{
			File.Move(path, path + ".bak", overwrite: true);
		}
		else if (Directory.Exists(path))
		{
			string text = path + "_bak";
			if (Directory.Exists(text))
			{
				Directory.Delete(text, recursive: true);
			}
			Directory.Move(path, text);
		}
	}

	private async void Check_Click(object sender, RoutedEventArgs e)
	{
		_ = 2;
		try
		{
			string currentVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
			CancellationToken token = _versionCts?.Token ?? CancellationToken.None;
			var release = await App.GetLatestRelease(true) ?? throw new InvalidDataException("Release information is unavailable");
			string text = release.TagName;
				if (!TryCompareVersions(text, currentVersion, out bool newer))
				{
					Frontend.ShowMessageBox("Could not compare versions. This build reports " + currentVersion + " and the latest release is tagged " + text + ".");
					return;
				}
				if (newer)
			{
				Frontend.ShowMessageBox("A new version (" + text + ") is available!");
				if (!await Voidstrap.Extensions.GithubUpdater.DownloadAndInstallUpdate(release.TagName))
				{
					throw new InvalidDataException("The update could not be installed");
				}
				if (!App.RestartApplication(["-settings"]))
				{
					throw new InvalidOperationException("The updated application could not be restarted");
				}
			}
			else
			{
				Frontend.ShowMessageBox("You are already running the latest version of Voidstrap (" + currentVersion + ").");
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Error checking for updates:\n" + ex.Message);
		}
	}

	private static bool TryCompareVersions(string latest, string current, out bool newer)
	{
		newer = false;
		if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current))
		{
			return false;
		}
		if (!Version.TryParse(latest.TrimStart('v', 'V'), out Version? remote) || !Version.TryParse(current, out Version? local))
		{
			return false;
		}
		newer = remote > local;
		return true;
	}

	private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
	{
		System.Windows.Controls.Button resetButton = (System.Windows.Controls.Button)sender;
		if (_resetInProgress)
		{
			return;
		}
		if (Frontend.ShowMessageBox("Erase all Voidstrap settings, FastFlags, modifications, accounts, themes, caches, and saved data, then return to onboarding? This cannot be undone.", MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
		{
			return;
		}

		_resetInProgress = true;
		resetButton.IsEnabled = false;
		try
		{
			App.PendingSettingTasks.Clear();
			using Process process = Process.Start(new ProcessStartInfo
			{
				FileName = Paths.Application,
				Arguments = "-factoryreset " + Environment.ProcessId,
				UseShellExecute = true
			}) ?? throw new InvalidOperationException("Voidstrap could not start the factory reset");
			Application.Current.Shutdown();
		}
		catch (Exception ex)
		{
			_resetInProgress = false;
			resetButton.IsEnabled = true;
			App.Logger.WriteException("ChannelPage::ResetSettingsButton_Click", ex);
			Frontend.ShowMessageBox("Factory reset could not start: " + ex.Message, MessageBoxImage.Hand);
		}
	}

	private void OpenChannelListDialog_Click(object sender, RoutedEventArgs e)
	{
		ChannelListsDialog channelListsDialog = new ChannelListsDialog();
		channelListsDialog.Owner = Window.GetWindow((DependencyObject)(object)this);
		channelListsDialog.ShowDialog();
	}

	private void LogsButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Directory.CreateDirectory(Paths.Logs);
			Process.Start(new ProcessStartInfo
			{
				FileName = Paths.Logs,
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ChannelPage::LogsButton_Click", ex);
		}
	}

	private void UninstallButton_Click(object sender, RoutedEventArgs e)
	{
		UninstallerDialog uninstallerDialog = new UninstallerDialog();
		uninstallerDialog.Owner = Window.GetWindow((DependencyObject)(object)this);
		uninstallerDialog.ShowDialog();
		if (uninstallerDialog.Confirmed)
		{
			Voidstrap.Installer.DoUninstall(uninstallerDialog.KeepData);
			Frontend.ShowMessageBox(Strings.Bootstrapper_SuccessfullyUninstalled, MessageBoxImage.Asterisk);
			App.Terminate();
		}
	}

	private void DonateButton_Click(object sender, RoutedEventArgs e)
	{
		string fileName = App.WebsiteBaseUrl + "/donate/donate";
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = fileName,
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Wasnt able to open: " + ex.Message);
		}
	}
}
