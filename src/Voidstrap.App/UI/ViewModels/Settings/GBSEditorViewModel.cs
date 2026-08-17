using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Xml.Linq;
using Voidstrap.UI.ViewModels.ContextMenu;

namespace Voidstrap.UI.ViewModels.Settings;

public class GBSEditorViewModel : NotifyPropertyChangedViewModel
{

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

	private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "GlobalBasicSettings_13.xml");

	private XDocument? _doc;

	private XElement? _props;

	private DateTime _lastLoadedWriteTime = DateTime.MinValue;

	public ICommand ResetToDefaultsCommand { get; }

	public float UITransparency
	{
		get
		{
			return GetFloat("PreferredTransparency", 1f);
		}
		set
		{
			SetFloat("PreferredTransparency", value);
			OnPropertyChanged("UITransparency");
		}
	}

	public int PreferredTextSize
	{
		get
		{
			return GetInt("PreferredTextSize", 1);
		}
		set
		{
			SetInt("PreferredTextSize", value, "token");
			OnPropertyChanged("PreferredTextSize");
		}
	}

	public bool ReducedMotion
	{
		get
		{
			return GetBool("ReducedMotion", defaultValue: true);
		}
		set
		{
			SetBool("ReducedMotion", value);
			OnPropertyChanged("ReducedMotion");
		}
	}

	public bool HudVisible
	{
		get
		{
			return !GetBool("UsedHideHudShortcut");
		}
		set
		{
			SetBool("UsedHideHudShortcut", !value);
			OnPropertyChanged("HudVisible");
		}
	}

	public int FramerateCap
	{
		get
		{
			return GetInt("FramerateCap", 0);
		}
		set
		{
			SetInt("FramerateCap", value);
			Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetTargetCap(value);
			LoadSettings();
			OnPropertyChanged("FramerateCap");
		}
	}

	public bool VignetteEnabled
	{
		get
		{
			return GetBool("VignetteEnabled", defaultValue: true);
		}
		set
		{
			SetBool("VignetteEnabled", value);
			OnPropertyChanged("VignetteEnabled");
		}
	}

	public int GraphicsQuality
	{
		get
		{
			return GetInt("SavedQualityLevel", 0);
		}
		set
		{
			SetInt("SavedQualityLevel", value, "token");
			OnPropertyChanged("GraphicsQuality");
		}
	}

	public bool Fullscreen
	{
		get
		{
			return GetBool("Fullscreen", defaultValue: true);
		}
		set
		{
			SetBool("Fullscreen", value);
			OnPropertyChanged("Fullscreen");
		}
	}

	public float MasterVolume
	{
		get
		{
			return GetFloat("MasterVolume", 1f);
		}
		set
		{
			SetFloat("MasterVolume", value);
			OnPropertyChanged("MasterVolume");
		}
	}

	public float VoiceChatVolume
	{
		get
		{
			return GetFloat("PartyVoiceVolume", 1f);
		}
		set
		{
			SetFloat("PartyVoiceVolume", value);
			OnPropertyChanged("VoiceChatVolume");
		}
	}

	public float MouseSensitivity
	{
		get
		{
			return GetFloat("MouseSensitivity", 1f);
		}
		set
		{
			SetFloat("MouseSensitivity", value);
			OnPropertyChanged("MouseSensitivity");
		}
	}

	public bool CameraYInverted
	{
		get
		{
			return GetBool("CameraYInverted");
		}
		set
		{
			SetBool("CameraYInverted", value);
			OnPropertyChanged("CameraYInverted");
		}
	}

	public float GamepadSensitivity
	{
		get
		{
			return GetFloat("GamepadCameraSensitivity", 0.2f);
		}
		set
		{
			SetFloat("GamepadCameraSensitivity", value);
			OnPropertyChanged("GamepadSensitivity");
		}
	}

	public bool ControllerVibration
	{
		get
		{
			return GetFloat("HapticStrength", 1f) > 0f;
		}
		set
		{
			SetFloat("HapticStrength", value ? 1f : 0f);
			OnPropertyChanged("ControllerVibration");
		}
	}

	public bool VREnabled
	{
		get
		{
			return GetBool("VREnabled");
		}
		set
		{
			SetBool("VREnabled", value);
			OnPropertyChanged("VREnabled");
		}
	}

	public int VRComfortSetting
	{
		get
		{
			return GetInt("VRComfortSetting", 2);
		}
		set
		{
			SetInt("VRComfortSetting", value, "token");
			OnPropertyChanged("VRComfortSetting");
		}
	}

	public bool NetworkStatsVisible
	{
		get
		{
			return GetBool("PerformanceStatsVisible");
		}
		set
		{
			SetBool("PerformanceStatsVisible", value);
			OnPropertyChanged("NetworkStatsVisible");
		}
	}

	public bool ChatTranslationEnabled
	{
		get
		{
			return GetBool("ChatTranslationEnabled", defaultValue: true);
		}
		set
		{
			SetBool("ChatTranslationEnabled", value);
			OnPropertyChanged("ChatTranslationEnabled");
		}
	}

	public bool MicroProfilerWebServerEnabled
	{
		get
		{
			return GetBool("MicroProfilerWebServerEnabled");
		}
		set
		{
			SetBool("MicroProfilerWebServerEnabled", value);
			OnPropertyChanged("MicroProfilerWebServerEnabled");
		}
	}

	public bool OnScreenProfilerEnabled
	{
		get
		{
			return GetBool("OnScreenProfilerEnabled");
		}
		set
		{
			SetBool("OnScreenProfilerEnabled", value);
			OnPropertyChanged("OnScreenProfilerEnabled");
		}
	}

	public bool PlayerNamesEnabled
	{
		get
		{
			return GetBool("PlayerNamesEnabled", defaultValue: true);
		}
		set
		{
			SetBool("PlayerNamesEnabled", value);
			OnPropertyChanged("PlayerNamesEnabled");
		}
	}

	public bool BadgeVisible
	{
		get
		{
			return GetBool("BadgeVisible", defaultValue: true);
		}
		set
		{
			SetBool("BadgeVisible", value);
			OnPropertyChanged("BadgeVisible");
		}
	}

	public bool ChatVisible
	{
		get
		{
			return GetBool("ChatVisible", defaultValue: true);
		}
		set
		{
			SetBool("ChatVisible", value);
			OnPropertyChanged("ChatVisible");
		}
	}

	public GBSEditorViewModel()
	{
		ResetToDefaultsCommand = new RelayCommand(ResetToDefaults);
		LoadSettings();
	}

	private void LoadSettings()
	{
		try
		{
			if (!File.Exists(_settingsPath))
			{
				_doc = null;
				_props = null;
			}
			else
			{
				_lastLoadedWriteTime = File.GetLastWriteTimeUtc(_settingsPath);
				_doc = XDocument.Load(_settingsPath);
				_props = _doc.Descendants("Properties").FirstOrDefault();
			}
		}
		catch
		{
			_doc = null;
			_props = null;
		}
	}

	private void ReloadIfChanged()
	{
		try
		{
			if (File.Exists(_settingsPath) && File.GetLastWriteTimeUtc(_settingsPath) != _lastLoadedWriteTime)
			{
				LoadSettings();
				OnPropertyChanged(string.Empty);
			}
		}
		catch
		{
		}
	}

	private void SaveSettings()
	{
		if (_doc is null)
		{
			throw new InvalidOperationException("The Roblox settings document is unavailable");
		}

		string directory = Path.GetDirectoryName(_settingsPath) ?? throw new InvalidOperationException("The Roblox settings directory is unavailable");
		Directory.CreateDirectory(directory);
		string tempPath = Path.Combine(directory, Path.GetFileName(_settingsPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
		string backupPath = tempPath + ".bak";
		bool existed = File.Exists(_settingsPath);
		FileAttributes originalAttributes = existed ? File.GetAttributes(_settingsPath) : FileAttributes.Normal;
		bool replaced = false;
		bool created = false;
		try
		{
			if (existed && originalAttributes.HasFlag(FileAttributes.ReadOnly))
			{
				File.SetAttributes(_settingsPath, originalAttributes & ~FileAttributes.ReadOnly);
			}
			_doc.Save(tempPath);
			if (existed)
			{
				File.Replace(tempPath, _settingsPath, backupPath, true);
				replaced = true;
			}
			else
			{
				File.Move(tempPath, _settingsPath);
				created = true;
			}
			File.SetAttributes(_settingsPath, originalAttributes);
			_lastLoadedWriteTime = File.GetLastWriteTimeUtc(_settingsPath);
		}
		catch
		{
			if (replaced && File.Exists(backupPath))
			{
				File.Replace(backupPath, _settingsPath, null, true);
			}
			else if (created && File.Exists(_settingsPath))
			{
				File.Delete(_settingsPath);
			}
			throw;
		}
		finally
		{
			if (File.Exists(_settingsPath) && existed)
			{
				File.SetAttributes(_settingsPath, originalAttributes);
			}
			if (File.Exists(tempPath))
			{
				DeleteTemporaryFile(tempPath);
			}
			if (File.Exists(backupPath))
			{
				DeleteTemporaryFile(backupPath);
			}
		}
	}

	private static void DeleteTemporaryFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("GBSEditorViewModel::DeleteTemporaryFile", ex);
		}
	}

	private string? GetValue(string name)
	{
		ReloadIfChanged();
		return _props?.Elements().FirstOrDefault((XElement e) => e.Attribute("name")?.Value == name)?.Value;
	}

	private bool SetValue(string name, string value, string type)
	{
		ReloadIfChanged();
		if (_props is null)
		{
			Voidstrap.Utility.SettingChangeNotifier.Report(
				"GBSEditorViewModel::SetValue",
				"The Roblox settings file could not be updated.",
				new InvalidOperationException("The Roblox settings properties are unavailable"));
			return false;
		}

		XElement? element = _props.Elements().FirstOrDefault((XElement e) => e.Attribute("name")?.Value == name);
		bool added = element is null;
		string? previousValue = element?.Value;
		try
		{
			if (element is null)
			{
				element = new XElement(type, value);
				element.SetAttributeValue("name", name);
				_props.Add(element);
			}
			else
			{
				element.Value = value;
			}
			SaveSettings();
			return true;
		}
		catch (Exception ex)
		{
			if (added)
			{
				element?.Remove();
			}
			else if (element is not null)
			{
				element.Value = previousValue ?? string.Empty;
			}
			Voidstrap.Utility.SettingChangeNotifier.Report(
				"GBSEditorViewModel::SetValue",
				"The Roblox settings file could not be updated.",
				ex);
			return false;
		}
	}

	private bool GetBool(string name, bool defaultValue = false)
	{
		string value = GetValue(name);
		if (string.IsNullOrEmpty(value))
		{
			return defaultValue;
		}
		return value.Equals("true", StringComparison.OrdinalIgnoreCase);
	}

	private bool SetBool(string name, bool value)
	{
		return SetValue(name, value.ToString().ToLowerInvariant(), "bool");
	}

	private int GetInt(string name, int defaultValue)
	{
		if (!int.TryParse(GetValue(name), out var result))
		{
			return defaultValue;
		}
		return result;
	}

	private bool SetInt(string name, int value, string type = "int")
	{
		return SetValue(name, value.ToString(), type);
	}

	private float GetFloat(string name, float defaultValue)
	{
		if (!float.TryParse(GetValue(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return defaultValue;
		}
		return result;
	}

	private bool SetFloat(string name, float value)
	{
		return SetValue(name, value.ToString("G", CultureInfo.InvariantCulture), "float");
	}

	public void ResetToDefaults()
	{
		try
		{
			if (File.Exists(_settingsPath))
			{
				FileAttributes attributes = File.GetAttributes(_settingsPath);
				if (attributes.HasFlag(FileAttributes.ReadOnly))
				{
					File.SetAttributes(_settingsPath, attributes & ~FileAttributes.ReadOnly);
				}
				File.Delete(_settingsPath);
			}
			_doc = null;
			_props = null;
			_lastLoadedWriteTime = DateTime.MinValue;
			OnPropertyChanged(string.Empty);
		}
		catch (Exception)
		{
		}
	}
}
