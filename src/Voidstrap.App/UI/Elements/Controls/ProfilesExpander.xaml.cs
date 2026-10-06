using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.Resources;
using Voidstrap.UI.ViewModels.Settings;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Controls;

public enum ProfileKind
{
	Settings,
	Global
}

public partial class ProfilesExpander : UserControl
{
	private bool _busy;

	public ProfilesExpander()
	{
		InitializeComponent();
		ProfilesRoot.Header = Strings.SettingsProfiles_Title;
	}

	public ProfileKind Kind
	{
		get => _kind;
		set
		{
			_kind = value;
			ProfilesRoot.Header = value == ProfileKind.Global ? Strings.GlobalProfiles_Title : Strings.SettingsProfiles_Title;
		}
	}

	private ProfileKind _kind;

	public event EventHandler? Applied;

	private bool IsGlobal => Kind == ProfileKind.Global;

	private string DirectoryPath => IsGlobal ? GlobalSettingsProfiles.DirectoryPath : SettingsProfiles.DirectoryPath;

	private void OnProfilesExpanded(object sender, RoutedEventArgs e)
	{
		if (!ReferenceEquals(sender, e.OriginalSource) || _busy)
			return;
		try
		{
			RefreshProfiles(ProfileSelector.SelectedItem as string);
		}
		catch (Exception ex)
		{
			ShowProfileError(ex);
		}
	}

	private void RefreshProfiles(string? selectedName = null)
	{
		string[] names = SettingsProfiles.List(DirectoryPath);
		ProfileSelector.ItemsSource = names;
		ProfileSelector.SelectedItem = names.FirstOrDefault(name => string.Equals(name, selectedName, StringComparison.OrdinalIgnoreCase));
		if (ProfileSelector.SelectedIndex < 0 && names.Length > 0)
			ProfileSelector.SelectedIndex = 0;
		ProfileSelector.IsEnabled = names.Length > 0;
		UpdateButtons();
		ProfileStatus.Text = names.Length == 0 ? Strings.SettingsProfiles_Empty : string.Format(Strings.SettingsProfiles_Location, DirectoryPath);
	}

	private void UpdateButtons()
	{
		bool selected = ProfileSelector.SelectedItem is string;
		ApplyProfileButton.IsEnabled = selected;
		DeleteProfileButton.IsEnabled = selected;
		SaveProfileButton.IsEnabled = !string.IsNullOrWhiteSpace(ProfileName.Text);
	}

	private void OnProfileNameChanged(object sender, TextChangedEventArgs e)
	{
		if (SaveProfileButton != null)
			SaveProfileButton.IsEnabled = !string.IsNullOrWhiteSpace(ProfileName.Text);
	}

	private void OnProfileNameKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter || !SaveProfileButton.IsEnabled)
			return;
		e.Handled = true;
		SaveProfile_Click(sender, e);
	}

	private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (ApplyProfileButton == null || DeleteProfileButton == null)
			return;
		if (ProfileSelector.SelectedItem is string name)
			ProfileName.Text = name;
		UpdateButtons();
	}

	private void SetBusy(bool busy, Window? owner)
	{
		_busy = busy;
		ProfilesPanel.IsEnabled = !busy;
		if (owner != null)
			owner.IsEnabled = !busy;
	}

	private async void SaveProfile_Click(object sender, RoutedEventArgs e)
	{
		if (_busy)
			return;
		Window? owner = Window.GetWindow(this);
		try
		{
			string name = ProfileName.Text.Trim();
			string path = SettingsProfiles.GetPath(DirectoryPath, name);
			if (File.Exists(path) && Frontend.ShowMessageBox(string.Format(Strings.SettingsProfiles_Overwrite, name), MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
				return;
			SetBusy(true, owner);
			if (IsGlobal)
			{
				if (DataContext is GBSEditorViewModel global)
					global.Flush();
				GlobalSettingsProfiles.Save(name);
			}
			else
			{
				if (owner?.DataContext is MainWindowViewModel mainViewModel && !await mainViewModel.TrySaveSettingsAsync(false))
					return;
				App.Settings.RefreshFromDisk();
				SettingsProfiles.Save(name);
			}
			RefreshProfiles(name);
			ProfileStatus.Text = string.Format(Strings.SettingsProfiles_SavedStatus, name);
		}
		catch (Exception ex)
		{
			SetBusy(false, owner);
			ShowProfileError(ex);
		}
		finally
		{
			SetBusy(false, owner);
		}
	}

	private async void ApplyProfile_Click(object sender, RoutedEventArgs e)
	{
		if (_busy || ProfileSelector.SelectedItem is not string name)
			return;
		Window? owner = Window.GetWindow(this);
		try
		{
			string confirm = IsGlobal ? Strings.GlobalProfiles_ApplyConfirm : Strings.SettingsProfiles_ApplyConfirm;
			if (Frontend.ShowMessageBox(string.Format(confirm, name), MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
				return;
			SetBusy(true, owner);
			if (IsGlobal)
				await ApplyGlobalAsync(name);
			else
				await ApplySettingsAsync(name);
		}
		catch (Exception ex)
		{
			SetBusy(false, owner);
			ShowProfileError(ex);
		}
		finally
		{
			SetBusy(false, owner);
		}
	}

	private async Task ApplyGlobalAsync(string name)
	{
		GlobalSettingsProfiles.Profile profile = await Task.Run(() => GlobalSettingsProfiles.Read(name));
		GBSEditorViewModel? global = DataContext as GBSEditorViewModel;
		global?.Flush();
		GlobalSettingsProfiles.Apply(profile);
		global?.Reload();
		Applied?.Invoke(this, EventArgs.Empty);
		ProfileStatus.Text = string.Format(Strings.GlobalProfiles_Applied, name);
	}

	private async Task ApplySettingsAsync(string name)
	{
		SettingsProfiles.Profile profile = await Task.Run(() => SettingsProfiles.Read(name));
		if (!IsLoaded)
			return;
		SettingsProfiles.Apply(profile);
		App.PendingSettingTasks.Clear();
		RestartNotificationService.ClearAll();
		Applied?.Invoke(this, EventArgs.Empty);
		if (!App.RestartApplication(["-settings", "-elevatedwait", Environment.ProcessId.ToString()], closeRuntime: false))
		{
			RestartNotificationService.Require("settingsProfile", Strings.SettingsProfiles_Title, Strings.SettingsProfiles_RestartFailed, Strings.SettingsProfiles_Restart, RestartTarget.Application);
			ProfileStatus.Text = Strings.SettingsProfiles_RestartFailed;
		}
	}

	private void DeleteProfile_Click(object sender, RoutedEventArgs e)
	{
		if (_busy || ProfileSelector.SelectedItem is not string name)
			return;
		try
		{
			if (Frontend.ShowMessageBox(string.Format(Strings.SettingsProfiles_DeleteConfirm, name), MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
				return;
			SettingsProfiles.Delete(DirectoryPath, name);
			ProfileName.Text = string.Empty;
			RefreshProfiles();
			ProfileStatus.Text = string.Format(Strings.SettingsProfiles_Deleted, name);
		}
		catch (Exception ex)
		{
			ShowProfileError(ex);
		}
	}

	private void OpenProfilesFolder_Click(object sender, RoutedEventArgs e)
	{
		if (!PlatformShell.TryOpenFolder(DirectoryPath))
			ShowProfileError(new DirectoryNotFoundException(DirectoryPath));
	}

	private void ShowProfileError(Exception ex)
	{
		App.Logger.WriteException("SettingsProfiles", ex);
		ProfileStatus.Text = string.Format(Strings.SettingsProfiles_Error, ex.Message);
		Frontend.ShowMessageBox(ProfileStatus.Text, MessageBoxImage.Warning);
	}
}
