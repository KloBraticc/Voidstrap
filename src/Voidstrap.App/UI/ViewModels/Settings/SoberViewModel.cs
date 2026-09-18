using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Core;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed class SoberViewModel : NotifyPropertyChangedViewModel
{
	public sealed record Choice<T>(T Value, string Display);

	public IReadOnlyList<Choice<bool?>> BooleanChoices { get; } =
	[
		new Choice<bool?>(null, Strings.Menu_Sober_Default),
		new Choice<bool?>(true, Strings.Menu_Sober_Enabled),
		new Choice<bool?>(false, Strings.Menu_Sober_Disabled)
	];

	public IReadOnlyList<Choice<SoberGraphicsOptimizationMode?>> GraphicsChoices { get; } =
	[
		new Choice<SoberGraphicsOptimizationMode?>(null, Strings.Menu_Sober_Default),
		new Choice<SoberGraphicsOptimizationMode?>(SoberGraphicsOptimizationMode.Quality, Strings.Menu_Sober_GraphicsMode_Quality),
		new Choice<SoberGraphicsOptimizationMode?>(SoberGraphicsOptimizationMode.Balanced, Strings.Menu_Sober_GraphicsMode_Balanced),
		new Choice<SoberGraphicsOptimizationMode?>(SoberGraphicsOptimizationMode.Performance, Strings.Menu_Sober_GraphicsMode_Performance)
	];

	public IReadOnlyList<Choice<SoberTouchMode?>> TouchChoices { get; } =
	[
		new Choice<SoberTouchMode?>(null, Strings.Menu_Sober_Default),
		new Choice<SoberTouchMode?>(SoberTouchMode.Off, Strings.Menu_Sober_TouchMode_Off),
		new Choice<SoberTouchMode?>(SoberTouchMode.On, Strings.Menu_Sober_TouchMode_On),
		new Choice<SoberTouchMode?>(SoberTouchMode.FakeOff, Strings.Menu_Sober_TouchMode_FakeOff)
	];

	private bool _uninstalling;
	private bool _installed;
	private string _status = "Checking for Sober...";

	public SoberViewModel()
	{
		_ = RefreshInstallationAsync();
	}

	public string InstallationStatus
	{
		get => _status;
		private set
		{
			if (string.Equals(_status, value, StringComparison.Ordinal))
				return;
			_status = value;
			OnPropertyChanged(nameof(InstallationStatus));
		}
	}

	public bool CanUninstall
	{
		get => !_uninstalling && _installed;
	}

	public string UninstallDescription => InstallationStatus;

	private async Task RefreshInstallationAsync()
	{
		try
		{
			SoberInstallationState state = await new Voidstrap.Platform.Linux.LinuxSoberInstaller(new Voidstrap.Core.SystemProcessService())
				.DetectAsync()
				.ConfigureAwait(true);

			_installed = state.Status == SoberInstallationStatus.Installed;
			InstallationStatus = state.Message;
		}
		catch (Exception ex)
		{
			_installed = false;
			InstallationStatus = "Sober could not be detected: " + ex.Message;
		}
		finally
		{
			OnPropertyChanged(nameof(CanUninstall));
			OnPropertyChanged(nameof(UninstallDescription));
		}
	}

	private void RefreshUninstallState()
	{
		OnPropertyChanged(nameof(CanUninstall));
		OnPropertyChanged(nameof(UninstallDescription));
	}

	public ICommand UninstallSoberCommand => new RelayCommand(UninstallSober);

	private void UninstallSober()
	{
		if (_uninstalling)
			return;

		MessageBoxResult answer = Frontend.ShowMessageBox(
			"Remove Sober and all of its data? Roblox will need to be downloaded again.",
			MessageBoxImage.Warning,
			MessageBoxButton.YesNo);

		if (answer != MessageBoxResult.Yes)
			return;

		_uninstalling = true;
		RefreshUninstallState();
		InstallationStatus = "Removing Sober...";

		_ = RunUninstallAsync();
	}

	private async Task RunUninstallAsync()
	{
		try
		{
			Voidstrap.Platform.OperationResult result = await new Voidstrap.Platform.Linux.LinuxSoberInstaller(new Voidstrap.Core.SystemProcessService())
				.UninstallAsync()
				.ConfigureAwait(true);

			Frontend.ShowMessageBox(
				result.Succeeded ? "Sober has been removed." : result.Failure?.Message ?? "Sober could not be removed",
				result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Error,
				MessageBoxButton.OK);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("SoberViewModel::Uninstall", ex);
		}
		finally
		{
			_uninstalling = false;
			await RefreshInstallationAsync().ConfigureAwait(true);
		}
	}

	public bool AutoFullscreen
	{
		get => App.Settings.Prop.SoberAutoFullscreen;
		set
		{
			if (App.Settings.Prop.SoberAutoFullscreen == value)
				return;

			App.Settings.Prop.SoberAutoFullscreen = value;
			OnPropertyChanged(nameof(AutoFullscreen));
		}
	}

	public IReadOnlyList<Choice<int>> SharpnessChoices { get; } =
	[
		new Choice<int>(0, "Off"),
		new Choice<int>(30, "Light"),
		new Choice<int>(50, "Medium"),
		new Choice<int>(75, "Strong"),
		new Choice<int>(100, "Maximum")
	];

	public Choice<int> SharpnessChoice
	{
		get
		{
			int current = App.Settings.Prop.SoberSharpness;

			foreach (Choice<int> choice in SharpnessChoices)
			{
				if (choice.Value == current)
					return choice;
			}

			return SharpnessChoices[0];
		}
		set
		{
			int resolved = value?.Value ?? 0;

			if (App.Settings.Prop.SoberSharpness == resolved)
				return;

			App.Settings.Prop.SoberSharpness = resolved;
			OnPropertyChanged(nameof(SharpnessChoice));
		}
	}

	public Choice<bool?> AllowGamepadPermissionChoice
	{
		get => ResolveChoice(BooleanChoices, AllowGamepadPermission);
		set => AllowGamepadPermission = value?.Value;
	}

	public Choice<bool?> CloseOnLeaveChoice
	{
		get => ResolveChoice(BooleanChoices, CloseOnLeave);
		set => CloseOnLeave = value?.Value;
	}



	public Choice<bool?> EnableGameModeChoice
	{
		get => ResolveChoice(BooleanChoices, EnableGameMode);
		set => EnableGameMode = value?.Value;
	}

	public Choice<bool?> EnableHiDpiChoice
	{
		get => ResolveChoice(BooleanChoices, EnableHiDpi);
		set => EnableHiDpi = value?.Value;
	}

	public Choice<bool?> EnableMobileHomeScreenChoice
	{
		get => ResolveChoice(BooleanChoices, EnableMobileHomeScreen);
		set => EnableMobileHomeScreen = value?.Value;
	}

	public Choice<SoberGraphicsOptimizationMode?> GraphicsOptimizationModeChoice
	{
		get => ResolveChoice(GraphicsChoices, GraphicsOptimizationMode);
		set => GraphicsOptimizationMode = value?.Value;
	}


	public Choice<SoberTouchMode?> TouchModeChoice
	{
		get => ResolveChoice(TouchChoices, TouchMode);
		set => TouchMode = value?.Value;
	}

	public Choice<bool?> UseConsoleExperienceChoice
	{
		get => ResolveChoice(BooleanChoices, UseConsoleExperience);
		set => UseConsoleExperience = value?.Value;
	}

	public Choice<bool?> UseLibsecretChoice
	{
		get => ResolveChoice(BooleanChoices, UseLibsecret);
		set => UseLibsecret = value?.Value;
	}

	public Choice<bool?> UseOpenGlChoice
	{
		get => ResolveChoice(BooleanChoices, UseOpenGl);
		set => UseOpenGl = value?.Value;
	}

	private static Choice<T> ResolveChoice<T>(IReadOnlyList<Choice<T>> choices, T value)
	{
		foreach (Choice<T> choice in choices)
		{
			if (EqualityComparer<T>.Default.Equals(choice.Value, value))
				return choice;
		}

		return choices[0];
	}

	public bool? AllowGamepadPermission
	{
		get => App.Settings.Prop.SoberAllowGamepadPermission;
		set => SetValue(App.Settings.Prop.SoberAllowGamepadPermission, value, updated => App.Settings.Prop.SoberAllowGamepadPermission = updated);
	}

	public bool? CloseOnLeave
	{
		get => App.Settings.Prop.SoberCloseOnLeave;
		set => SetValue(App.Settings.Prop.SoberCloseOnLeave, value, updated => App.Settings.Prop.SoberCloseOnLeave = updated);
	}



	public bool? EnableGameMode
	{
		get => App.Settings.Prop.SoberEnableGameMode;
		set => SetValue(App.Settings.Prop.SoberEnableGameMode, value, updated => App.Settings.Prop.SoberEnableGameMode = updated);
	}

	public bool? EnableHiDpi
	{
		get => App.Settings.Prop.SoberEnableHiDpi;
		set => SetValue(App.Settings.Prop.SoberEnableHiDpi, value, updated => App.Settings.Prop.SoberEnableHiDpi = updated);
	}

	public bool? EnableMobileHomeScreen
	{
		get => App.Settings.Prop.SoberEnableMobileHomeScreen;
		set => SetValue(App.Settings.Prop.SoberEnableMobileHomeScreen, value, updated => App.Settings.Prop.SoberEnableMobileHomeScreen = updated);
	}

	public SoberGraphicsOptimizationMode? GraphicsOptimizationMode
	{
		get => App.Settings.Prop.SoberGraphicsOptimizationMode;
		set => SetValue(App.Settings.Prop.SoberGraphicsOptimizationMode, value, updated => App.Settings.Prop.SoberGraphicsOptimizationMode = updated);
	}


	public SoberTouchMode? TouchMode
	{
		get => App.Settings.Prop.SoberTouchMode;
		set => SetValue(App.Settings.Prop.SoberTouchMode, value, updated => App.Settings.Prop.SoberTouchMode = updated);
	}

	public bool? UseConsoleExperience
	{
		get => App.Settings.Prop.SoberUseConsoleExperience;
		set => SetValue(App.Settings.Prop.SoberUseConsoleExperience, value, updated => App.Settings.Prop.SoberUseConsoleExperience = updated);
	}

	public bool? UseLibsecret
	{
		get => App.Settings.Prop.SoberUseLibsecret;
		set => SetValue(App.Settings.Prop.SoberUseLibsecret, value, updated => App.Settings.Prop.SoberUseLibsecret = updated);
	}

	public bool? UseOpenGl
	{
		get => App.Settings.Prop.SoberUseOpenGl;
		set => SetValue(App.Settings.Prop.SoberUseOpenGl, value, updated => App.Settings.Prop.SoberUseOpenGl = updated);
	}

	private void SetValue<T>(T current, T value, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
	{
		if (EqualityComparer<T>.Default.Equals(current, value))
		{
			return;
		}

		apply(value);
		App.Settings.SaveDeferred();
		OnPropertyChanged(propertyName);
	}
}
