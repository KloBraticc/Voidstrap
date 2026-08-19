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
	private string _runtimeStatus = "Checking for Sober";
	private bool _isBusy;
	private bool _canInstall;

	public sealed record Choice<T>(T Value, string Display);

	public SoberViewModel()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
			_ = RefreshRuntimeAsync();
		else
			_runtimeStatus = "Sober is only available on Linux";
	}

	public Visibility RuntimeVisibility => Voidstrap.Utility.Platform.IsLinux ? Visibility.Visible : Visibility.Collapsed;

	public string RuntimeStatus
	{
		get => _runtimeStatus;
		private set
		{
			if (string.Equals(_runtimeStatus, value, StringComparison.Ordinal))
				return;
			_runtimeStatus = value;
			OnPropertyChanged();
		}
	}

	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (_isBusy == value)
				return;
			_isBusy = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsIdle));
			OnPropertyChanged(nameof(CanInstall));
		}
	}

	public bool IsIdle => !_isBusy;

	public bool CanInstall
	{
		get => _canInstall && !_isBusy;
		private set
		{
			if (_canInstall == value)
				return;
			_canInstall = value;
			OnPropertyChanged();
		}
	}

	public ICommand InstallRuntimeCommand => new AsyncRelayCommand(InstallRuntimeAsync);

	public ICommand RefreshRuntimeCommand => new AsyncRelayCommand(RefreshRuntimeAsync);

	private async Task RefreshRuntimeAsync()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || IsBusy)
			return;

		IsBusy = true;
		try
		{
			SoberInstallationState state = await new LinuxSoberInstaller(new SystemProcessService()).DetectAsync();
			RuntimeStatus = state.Message;
			CanInstall = state.Status != SoberInstallationStatus.FlatpakMissing;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("SoberViewModel::RefreshRuntime", "Sober detection failed: " + ex.Message);
			RuntimeStatus = "Sober could not be detected";
			CanInstall = true;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task InstallRuntimeAsync()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || IsBusy)
			return;

		IsBusy = true;
		RuntimeStatus = "Installing Sober, this can take a while";
		try
		{
			OperationResult result = await new LinuxSoberInstaller(new SystemProcessService()).InstallAsync();
			if (!result.Succeeded)
			{
				string message = result.Failure?.Message ?? "Sober could not be installed";
				App.Logger.WriteLine("SoberViewModel::InstallRuntime", message);
				RuntimeStatus = message;
				return;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("SoberViewModel::InstallRuntime", "Sober install failed: " + ex.Message);
			RuntimeStatus = "Sober could not be installed: " + ex.Message;
			return;
		}
		finally
		{
			IsBusy = false;
		}

		await RefreshRuntimeAsync();
	}

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

	public bool? DiscordRpcEnabled
	{
		get => App.Settings.Prop.SoberDiscordRpcEnabled;
		set => SetValue(App.Settings.Prop.SoberDiscordRpcEnabled, value, updated => App.Settings.Prop.SoberDiscordRpcEnabled = updated);
	}

	public bool? DiscordRpcShowJoinButton
	{
		get => App.Settings.Prop.SoberDiscordRpcShowJoinButton;
		set => SetValue(App.Settings.Prop.SoberDiscordRpcShowJoinButton, value, updated => App.Settings.Prop.SoberDiscordRpcShowJoinButton = updated);
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

	public bool? ServerLocationIndicatorEnabled
	{
		get => App.Settings.Prop.SoberServerLocationIndicatorEnabled;
		set => SetValue(App.Settings.Prop.SoberServerLocationIndicatorEnabled, value, updated => App.Settings.Prop.SoberServerLocationIndicatorEnabled = updated);
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
