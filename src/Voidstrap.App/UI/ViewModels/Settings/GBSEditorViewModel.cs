using System;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Voidstrap.UI.ViewModels.ContextMenu;

namespace Voidstrap.UI.ViewModels.Settings;

public class GBSEditorViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private readonly DispatcherTimer _saveTimer;

	private bool _pendingSave;

	private bool _disposed;

	public bool IsDisposed => _disposed;

	public ICommand ResetToDefaultsCommand { get; }

	public bool SettingsFileReadOnly
	{
		get
		{
			try
			{
				return App.GlobalSettings.GetReadOnly();
			}
			catch
			{
				return false;
			}
		}
		set
		{
			try
			{
				if (App.GlobalSettings.GetReadOnly() == value)
					return;

				App.GlobalSettings.SetReadOnly(value);
			}
			catch (Exception ex)
			{
				Voidstrap.Utility.SettingChangeNotifier.Report(
					"GBSEditorViewModel::SettingsFileReadOnly",
					"The Roblox settings file access could not be changed.",
					ex);
			}

			OnPropertyChanged(nameof(SettingsFileReadOnly));
		}
	}

	public float UITransparency
	{
		get => App.GlobalSettings.GetFloat("PreferredTransparency", 1f);
		set
		{
			Write("PreferredTransparency", value);
			OnPropertyChanged(nameof(UITransparency));
		}
	}

	public int PreferredTextSize
	{
		get => App.GlobalSettings.GetInt("PreferredTextSize", 1);
		set
		{
			Write("PreferredTextSize", value);
			OnPropertyChanged(nameof(PreferredTextSize));
		}
	}

	public bool ReducedMotion
	{
		get => App.GlobalSettings.GetBool("ReducedMotion", defaultValue: true);
		set
		{
			Write("ReducedMotion", value);
			OnPropertyChanged(nameof(ReducedMotion));
		}
	}

	public bool HudVisible
	{
		get => !App.GlobalSettings.GetBool("UsedHideHudShortcut");
		set
		{
			Write("UsedHideHudShortcut", !value);
			OnPropertyChanged(nameof(HudVisible));
		}
	}

	public int FramerateCap
	{
		get => App.GlobalSettings.GetInt("FramerateCap", 0);
		set
		{
			Write("FramerateCap", value);
			Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetTargetCap(App.GlobalSettings.GetInt("FramerateCap", 0));
			OnPropertyChanged(nameof(FramerateCap));
		}
	}

	public bool VignetteEnabled
	{
		get => App.GlobalSettings.GetBool("VignetteEnabled", defaultValue: true);
		set
		{
			Write("VignetteEnabled", value);
			OnPropertyChanged(nameof(VignetteEnabled));
		}
	}

	public int GraphicsQuality
	{
		get => App.GlobalSettings.GetInt("SavedQualityLevel", 0);
		set
		{
			Write("SavedQualityLevel", value);
			OnPropertyChanged(nameof(GraphicsQuality));
		}
	}

	public bool Fullscreen
	{
		get => App.GlobalSettings.GetBool("Fullscreen", defaultValue: true);
		set
		{
			Write("Fullscreen", value);
			OnPropertyChanged(nameof(Fullscreen));
		}
	}

	public float MasterVolume
	{
		get => App.GlobalSettings.GetFloat("MasterVolume", 1f);
		set
		{
			Write("MasterVolume", value);
			OnPropertyChanged(nameof(MasterVolume));
		}
	}

	public float VoiceChatVolume
	{
		get => App.GlobalSettings.GetFloat("PartyVoiceVolume", 1f);
		set
		{
			Write("PartyVoiceVolume", value);
			OnPropertyChanged(nameof(VoiceChatVolume));
		}
	}

	public float MouseSensitivity
	{
		get => App.GlobalSettings.GetFloat("MouseSensitivity", 1f);
		set
		{
			Write("MouseSensitivity", value);
			OnPropertyChanged(nameof(MouseSensitivity));
		}
	}

	public bool CameraYInverted
	{
		get => App.GlobalSettings.GetBool("CameraYInverted");
		set
		{
			Write("CameraYInverted", value);
			OnPropertyChanged(nameof(CameraYInverted));
		}
	}

	public float GamepadSensitivity
	{
		get => App.GlobalSettings.GetFloat("GamepadCameraSensitivity", 0.2f);
		set
		{
			Write("GamepadCameraSensitivity", value);
			OnPropertyChanged(nameof(GamepadSensitivity));
		}
	}

	public bool ControllerVibration
	{
		get => App.GlobalSettings.GetFloat("HapticStrength", 1f) > 0f;
		set
		{
			Write("HapticStrength", value ? 1f : 0f);
			OnPropertyChanged(nameof(ControllerVibration));
		}
	}

	public bool VREnabled
	{
		get => App.GlobalSettings.GetBool("VREnabled");
		set
		{
			Write("VREnabled", value);
			OnPropertyChanged(nameof(VREnabled));
		}
	}

	public int VRComfortSetting
	{
		get => App.GlobalSettings.GetInt("VRComfortSetting", 2);
		set
		{
			Write("VRComfortSetting", value);
			OnPropertyChanged(nameof(VRComfortSetting));
		}
	}

	public bool NetworkStatsVisible
	{
		get => App.GlobalSettings.GetBool("PerformanceStatsVisible");
		set
		{
			Write("PerformanceStatsVisible", value);
			OnPropertyChanged(nameof(NetworkStatsVisible));
		}
	}

	public bool ChatTranslationEnabled
	{
		get => App.GlobalSettings.GetBool("ChatTranslationEnabled", defaultValue: true);
		set
		{
			Write("ChatTranslationEnabled", value);
			OnPropertyChanged(nameof(ChatTranslationEnabled));
		}
	}

	public bool MicroProfilerWebServerEnabled
	{
		get => App.GlobalSettings.GetBool("MicroProfilerWebServerEnabled");
		set
		{
			Write("MicroProfilerWebServerEnabled", value);
			OnPropertyChanged(nameof(MicroProfilerWebServerEnabled));
		}
	}

	public bool OnScreenProfilerEnabled
	{
		get => App.GlobalSettings.GetBool("OnScreenProfilerEnabled");
		set
		{
			Write("OnScreenProfilerEnabled", value);
			OnPropertyChanged(nameof(OnScreenProfilerEnabled));
		}
	}

	public bool PlayerNamesEnabled
	{
		get => App.GlobalSettings.GetBool("PlayerNamesEnabled", defaultValue: true);
		set
		{
			Write("PlayerNamesEnabled", value);
			OnPropertyChanged(nameof(PlayerNamesEnabled));
		}
	}

	public bool BadgeVisible
	{
		get => App.GlobalSettings.GetBool("BadgeVisible", defaultValue: true);
		set
		{
			Write("BadgeVisible", value);
			OnPropertyChanged(nameof(BadgeVisible));
		}
	}

	public bool ChatVisible
	{
		get => App.GlobalSettings.GetBool("ChatVisible", defaultValue: true);
		set
		{
			Write("ChatVisible", value);
			OnPropertyChanged(nameof(ChatVisible));
		}
	}

	public GBSEditorViewModel()
	{
		ResetToDefaultsCommand = new RelayCommand(ResetToDefaults);
		_saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400.0) };
		_saveTimer.Tick += OnSaveTimerTick;
	}

	private void Write(string name, object value)
	{
		if (!App.GlobalSettings.SetProperty(name, value))
		{
			return;
		}

		_pendingSave = true;
		_saveTimer.Stop();
		_saveTimer.Start();
	}

	private void OnSaveTimerTick(object? sender, EventArgs e)
	{
		_saveTimer.Stop();
		Flush();
	}

	public void Flush()
	{
		if (!_pendingSave)
		{
			return;
		}

		_pendingSave = false;
		try
		{
			if (!App.GlobalSettings.Save())
			{
				Voidstrap.Utility.SettingChangeNotifier.Report(
					"GBSEditorViewModel::Flush",
					"The Roblox settings file could not be updated.",
					new IOException("The Roblox settings file could not be written"));
			}
		}
		catch (Exception ex)
		{
			Voidstrap.Utility.SettingChangeNotifier.Report(
				"GBSEditorViewModel::Flush",
				DescribeSaveFailure(ex),
				ex);
		}
	}

	private static string DescribeSaveFailure(Exception exception)
	{
		if (exception is UnauthorizedAccessException)
		{
			return "The Roblox settings file is not writable. Check its permissions and try again.";
		}
		if (exception is IOException)
		{
			return "The Roblox settings file is in use. Close Roblox and try again.";
		}
		return "The Roblox settings file could not be updated. " + exception.Message;
	}

	public void ResetToDefaults()
	{
		try
		{
			App.GlobalSettings.ResetProperties();
			_pendingSave = true;
			_saveTimer.Stop();
			Flush();
			OnPropertyChanged(string.Empty);
		}
		catch (Exception ex)
		{
			Voidstrap.Utility.SettingChangeNotifier.Report(
				"GBSEditorViewModel::ResetToDefaults",
				DescribeSaveFailure(ex),
				ex);
		}
	}

	private int _suspendedVersion = -1;

	public void Resume()
	{
		if (!_disposed)
		{
			return;
		}

		_disposed = false;
		_saveTimer.Tick += OnSaveTimerTick;
		if (App.GlobalSettings.Version != _suspendedVersion)
		{
			OnPropertyChanged(string.Empty);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_saveTimer.Stop();
		_saveTimer.Tick -= OnSaveTimerTick;
		Flush();
		_suspendedVersion = App.GlobalSettings.Version;
		GC.SuppressFinalize(this);
	}
}
