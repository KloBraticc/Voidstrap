using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Core;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed class VinegarViewModel : NotifyPropertyChangedViewModel
{
	public sealed record Choice<T>(T Value, string Display);

	public IReadOnlyList<Choice<bool?>> BooleanChoices { get; } =
	[
		new Choice<bool?>(null, Strings.Menu_Sober_Default),
		new Choice<bool?>(true, Strings.Menu_Sober_Enabled),
		new Choice<bool?>(false, Strings.Menu_Sober_Disabled)
	];

	public IReadOnlyList<Choice<VinegarRenderer?>> RendererChoices { get; } =
	[
		new Choice<VinegarRenderer?>(null, Strings.Menu_Sober_Default),
		new Choice<VinegarRenderer?>(VinegarRenderer.Dxvk, "DXVK"),
		new Choice<VinegarRenderer?>(VinegarRenderer.DxvkSarek, "DXVK Sarek"),
		new Choice<VinegarRenderer?>(VinegarRenderer.Vulkan, "Vulkan"),
		new Choice<VinegarRenderer?>(VinegarRenderer.D3D11, "Direct3D 11"),
		new Choice<VinegarRenderer?>(VinegarRenderer.D3D11FL10, "Direct3D 11 feature level 10")
	];

	private bool _busy;
	private bool _installed;
	private string _status = "Checking for Vinegar...";

	public VinegarViewModel()
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

	public bool IsInstalled
	{
		get => _installed;
		private set
		{
			if (_installed == value)
				return;
			_installed = value;
			OnPropertyChanged(nameof(IsInstalled));
			OnPropertyChanged(nameof(ActionLabel));
			OnPropertyChanged(nameof(ActionAppearance));
		}
	}

	public bool CanRunAction => !_busy;

	public string ActionLabel => _installed ? "Uninstall" : "Install";

	public Wpf.Ui.Common.ControlAppearance ActionAppearance => _installed
		? Wpf.Ui.Common.ControlAppearance.Danger
		: Wpf.Ui.Common.ControlAppearance.Primary;

	public ICommand RunActionCommand => new RelayCommand(RunAction);

	private void SetBusy(bool busy)
	{
		_busy = busy;
		OnPropertyChanged(nameof(CanRunAction));
	}

	private async Task RefreshInstallationAsync()
	{
		try
		{
			VinegarInstallationState state = await new LinuxVinegarInstaller(new SystemProcessService())
				.DetectAsync()
				.ConfigureAwait(true);

			IsInstalled = state.Status == VinegarInstallationStatus.Installed;
			InstallationStatus = state.Message;
		}
		catch (Exception ex)
		{
			IsInstalled = false;
			InstallationStatus = "Vinegar could not be detected: " + ex.Message;
		}
	}

	private void RunAction()
	{
		if (_busy)
			return;

		if (_installed)
		{
			MessageBoxResult answer = Frontend.ShowMessageBox(
				"Remove Vinegar and all of its data? Roblox Studio will need to be downloaded again.",
				MessageBoxImage.Warning,
				MessageBoxButton.YesNo);

			if (answer != MessageBoxResult.Yes)
				return;
		}

		SetBusy(true);
		_ = RunActionAsync();
	}

	private async Task RunActionAsync()
	{
		bool uninstalling = _installed;
		try
		{
			InstallationStatus = uninstalling ? "Removing Vinegar..." : "Installing Vinegar, this can take a while...";

			LinuxVinegarInstaller installer = new(new SystemProcessService());
			OperationResult result = uninstalling
				? await installer.UninstallAsync().ConfigureAwait(true)
				: await installer.InstallAsync().ConfigureAwait(true);

			if (!result.Succeeded)
			{
				Frontend.ShowMessageBox(
					result.Failure?.Message ?? (uninstalling ? "Vinegar could not be removed" : "Vinegar could not be installed"),
					MessageBoxImage.Error);
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
		}
		finally
		{
			SetBusy(false);
			await RefreshInstallationAsync().ConfigureAwait(true);
		}
	}

	public bool ApplyFastFlags
	{
		get => App.Settings.Prop.VinegarApplyFastFlags;
		set
		{
			if (App.Settings.Prop.VinegarApplyFastFlags == value)
				return;
			App.Settings.Prop.VinegarApplyFastFlags = value;
			OnPropertyChanged(nameof(ApplyFastFlags));
		}
	}

	public Choice<VinegarRenderer?> RendererChoice
	{
		get => ResolveChoice(RendererChoices, App.Settings.Prop.VinegarRenderer);
		set
		{
			VinegarRenderer? resolved = value?.Value;
			if (App.Settings.Prop.VinegarRenderer == resolved)
				return;
			App.Settings.Prop.VinegarRenderer = resolved;
			OnPropertyChanged(nameof(RendererChoice));
		}
	}

	public Choice<bool?> EnableGameModeChoice
	{
		get => ResolveChoice(BooleanChoices, App.Settings.Prop.VinegarEnableGameMode);
		set
		{
			bool? resolved = value?.Value;
			if (App.Settings.Prop.VinegarEnableGameMode == resolved)
				return;
			App.Settings.Prop.VinegarEnableGameMode = resolved;
			OnPropertyChanged(nameof(EnableGameModeChoice));
		}
	}

	public Choice<bool?> DiscordRpcChoice
	{
		get => ResolveChoice(BooleanChoices, App.Settings.Prop.VinegarDiscordRpcEnabled);
		set
		{
			bool? resolved = value?.Value;
			if (App.Settings.Prop.VinegarDiscordRpcEnabled == resolved)
				return;
			App.Settings.Prop.VinegarDiscordRpcEnabled = resolved;
			OnPropertyChanged(nameof(DiscordRpcChoice));
		}
	}

	public IReadOnlyList<Choice<string>> GpuChoices
	{
		get
		{
			List<Choice<string>> choices = [new Choice<string>(string.Empty, "Automatic")];
			foreach (LinuxGpuCard card in LinuxGpuCatalog.Cards)
				choices.Add(new Choice<string>(card.Address, card.DisplayName));

			string stored = App.Settings.Prop.VinegarGpu;
			if (stored.Length > 0 && !choices.Any(choice => string.Equals(choice.Value, stored, StringComparison.OrdinalIgnoreCase)))
				choices.Add(new Choice<string>(stored, stored + " (not detected)"));

			return choices;
		}
	}

	public Choice<string> GpuChoice
	{
		get
		{
			string stored = App.Settings.Prop.VinegarGpu;
			foreach (Choice<string> choice in GpuChoices)
			{
				if (string.Equals(choice.Value, stored, StringComparison.OrdinalIgnoreCase))
					return choice;
			}

			return GpuChoices[0];
		}
		set
		{
			string resolved = value?.Value ?? string.Empty;
			if (string.Equals(App.Settings.Prop.VinegarGpu, resolved, StringComparison.Ordinal))
				return;
			App.Settings.Prop.VinegarGpu = resolved;
			OnPropertyChanged(nameof(GpuChoice));
		}
	}

	public string VirtualDesktop
	{
		get => App.Settings.Prop.VinegarVirtualDesktop;
		set => SetText(nameof(VirtualDesktop), value, static resolved => App.Settings.Prop.VinegarVirtualDesktop = resolved, App.Settings.Prop.VinegarVirtualDesktop);
	}

	public string Launcher
	{
		get => App.Settings.Prop.VinegarLauncher;
		set => SetText(nameof(Launcher), value, static resolved => App.Settings.Prop.VinegarLauncher = resolved, App.Settings.Prop.VinegarLauncher);
	}

	public string ForcedVersion
	{
		get => App.Settings.Prop.VinegarForcedVersion;
		set => SetText(nameof(ForcedVersion), value, static resolved => App.Settings.Prop.VinegarForcedVersion = resolved, App.Settings.Prop.VinegarForcedVersion);
	}

	public string Channel
	{
		get => App.Settings.Prop.VinegarChannel;
		set => SetText(nameof(Channel), value, static resolved => App.Settings.Prop.VinegarChannel = resolved, App.Settings.Prop.VinegarChannel);
	}

	public string WineRoot
	{
		get => App.Settings.Prop.VinegarWineRoot;
		set => SetText(nameof(WineRoot), value, static resolved => App.Settings.Prop.VinegarWineRoot = resolved, App.Settings.Prop.VinegarWineRoot);
	}

	private void SetText(string propertyName, string? value, Action<string> assign, string current)
	{
		string resolved = (value ?? string.Empty).Trim();
		if (string.Equals(current, resolved, StringComparison.Ordinal))
			return;
		assign(resolved);
		OnPropertyChanged(propertyName);
	}

	private static Choice<T> ResolveChoice<T>(IReadOnlyList<Choice<T>> choices, T current)
	{
		foreach (Choice<T> choice in choices)
		{
			if (EqualityComparer<T>.Default.Equals(choice.Value, current))
				return choice;
		}

		return choices[0];
	}
}
