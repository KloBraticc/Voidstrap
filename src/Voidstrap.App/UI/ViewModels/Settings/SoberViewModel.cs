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
