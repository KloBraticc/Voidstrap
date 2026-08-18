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
			OnPropertyChanged("UITransparency");
		}
	}

	public int PreferredTextSize
	{
		get => App.GlobalSettings.GetInt("PreferredTextSize", 1);
		set
		{
			Write("PreferredTextSize", value);
			OnPropertyChanged("PreferredTextSize");
		}
	}

	public bool ReducedMotion
	{
		get => App.GlobalSettings.GetBool("ReducedMotion", defaultValue: true);
		set
		{
			Write("ReducedMotion", value);
			OnPropertyChanged("ReducedMotion");
		}
	}

	public bool HudVisible
	{
		get => !App.GlobalSettings.GetBool("UsedHideHudShortcut");
		set
		{
			Write("UsedHideHudShortcut", !value);
			OnPropertyChanged("HudVisible");
		}
	}

	public int FramerateCap
	{
		get => App.GlobalSettings.GetInt("FramerateCap", 0);
		set
		{
			Write("FramerateCap", value);
			Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetTargetCap(App.GlobalSettings.GetInt("FramerateCap", 0));
			OnPropertyChanged("FramerateCap");
		}
	}

	public bool VignetteEnabled
	{
		get => App.GlobalSettings.GetBool("VignetteEnabled", defaultValue: true);
		set
		{
			Write("VignetteEnabled", value);
			OnPropertyChanged("VignetteEnabled");
		}
	}

	public int GraphicsQuality
	{
		get => App.GlobalSettings.GetInt("SavedQualityLevel", 0);
		set
		{
			Write("SavedQualityLevel", value);
			OnPropertyChanged("GraphicsQuality");
		}
	}

	public bool Fullscreen
	{
		get => App.GlobalSettings.GetBool("Fullscreen", defaultValue: true);
		set
		{
			Write("Fullscreen", value);
			OnPropertyChanged("Fullscreen");
		}
	}

	public float MasterVolume
	{
		get => App.GlobalSettings.GetFloat("MasterVolume", 1f);
		set
		{
			Write("MasterVolume", value);
			OnPropertyChanged("MasterVolume");
		}
	}

	public float VoiceChatVolume
	{
		get => App.GlobalSettings.GetFloat("PartyVoiceVolume", 1f);
		set
		{
			Write("PartyVoiceVolume", value);
			OnPropertyChanged("VoiceChatVolume");
		}
	}

	public float MouseSensitivity
	{
		get => App.GlobalSettings.GetFloat("MouseSensitivity", 1f);
		set
		{
			Write("MouseSensitivity", value);
			OnPropertyChanged("MouseSensitivity");
		}
	}

	public bool CameraYInverted
	{
		get => App.GlobalSettings.GetBool("CameraYInverted");
		set
		{
			Write("CameraYInverted", value);
			OnPropertyChanged("CameraYInverted");
		}
	}

	public float GamepadSensitivity
	{
		get => App.GlobalSettings.GetFloat("GamepadCameraSensitivity", 0.2f);
		set
		{
			Write("GamepadCameraSensitivity", value);
			OnPropertyChanged("GamepadSensitivity");
		}
	}

	public bool ControllerVibration
	{
		get => App.GlobalSettings.GetFloat("HapticStrength", 1f) > 0f;
		set
		{
			Write("HapticStrength", value ? 1f : 0f);
			OnPropertyChanged("ControllerVibration");
		}
	}

	public bool VREnabled
	{
		get => App.GlobalSettings.GetBool("VREnabled");
		set
		{
			Write("VREnabled", value);
			OnPropertyChanged("VREnabled");
		}
	}

	public int VRComfortSetting
	{
		get => App.GlobalSettings.GetInt("VRComfortSetting", 2);
		set
		{
			Write("VRComfortSetting", value);
			OnPropertyChanged("VRComfortSetting");
		}
	}

	public bool NetworkStatsVisible
	{
		get => App.GlobalSettings.GetBool("PerformanceStatsVisible");
		set
		{
			Write("PerformanceStatsVisible", value);
			OnPropertyChanged("NetworkStatsVisible");
		}
	}

	public bool ChatTranslationEnabled
	{
		get => App.GlobalSettings.GetBool("ChatTranslationEnabled", defaultValue: true);
		set
		{
			Write("ChatTranslationEnabled", value);
			OnPropertyChanged("ChatTranslationEnabled");
		}
	}

	public bool MicroProfilerWebServerEnabled
	{
		get => App.GlobalSettings.GetBool("MicroProfilerWebServerEnabled");
		set
		{
			Write("MicroProfilerWebServerEnabled", value);
			OnPropertyChanged("MicroProfilerWebServerEnabled");
		}
	}

	public bool OnScreenProfilerEnabled
	{
		get => App.GlobalSettings.GetBool("OnScreenProfilerEnabled");
		set
		{
			Write("OnScreenProfilerEnabled", value);
			OnPropertyChanged("OnScreenProfilerEnabled");
		}
	}

	public bool PlayerNamesEnabled
	{
		get => App.GlobalSettings.GetBool("PlayerNamesEnabled", defaultValue: true);
		set
		{
			Write("PlayerNamesEnabled", value);
			OnPropertyChanged("PlayerNamesEnabled");
		}
	}

	public bool BadgeVisible
	{
		get => App.GlobalSettings.GetBool("BadgeVisible", defaultValue: true);
		set
		{
			Write("BadgeVisible", value);
			OnPropertyChanged("BadgeVisible");
		}
	}

	public bool ChatVisible
	{
		get => App.GlobalSettings.GetBool("ChatVisible", defaultValue: true);
		set
		{
			Write("ChatVisible", value);
			OnPropertyChanged("ChatVisible");
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
		GC.SuppressFinalize(this);
	}
}
