using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Voidstrap;

public sealed record GBSProperty(string Tag, double Minimum, double Maximum);

public class GBSEditor
{
	private static readonly string[] AllowedRootChildren = ["External", "Item", "Meta", "SharedStrings"];

	public static readonly IReadOnlyDictionary<string, GBSProperty> KnownProperties = new Dictionary<string, GBSProperty>(StringComparer.Ordinal)
	{
		["FramerateCap"] = new("int", 0.0, 10000.0),
		["SavedQualityLevel"] = new("token", 0.0, 10.0),
		["PreferredTextSize"] = new("token", 0.0, 3.0),
		["VRComfortSetting"] = new("token", 0.0, 2.0),
		["PreferredTransparency"] = new("float", 0.0, 1.0),
		["MasterVolume"] = new("float", 0.0, 1.0),
		["PartyVoiceVolume"] = new("float", 0.0, 1.0),
		["MouseSensitivity"] = new("float", 0.01, 10.0),
		["GamepadCameraSensitivity"] = new("float", 0.1, 2.0),
		["HapticStrength"] = new("float", 0.0, 1.0),
		["ReducedMotion"] = new("bool", 0.0, 1.0),
		["UsedHideHudShortcut"] = new("bool", 0.0, 1.0),
		["VignetteEnabled"] = new("bool", 0.0, 1.0),
		["Fullscreen"] = new("bool", 0.0, 1.0),
		["CameraYInverted"] = new("bool", 0.0, 1.0),
		["VREnabled"] = new("bool", 0.0, 1.0),
		["PerformanceStatsVisible"] = new("bool", 0.0, 1.0),
		["ChatTranslationEnabled"] = new("bool", 0.0, 1.0),
		["MicroProfilerWebServerEnabled"] = new("bool", 0.0, 1.0),
		["OnScreenProfilerEnabled"] = new("bool", 0.0, 1.0),
		["PlayerNamesEnabled"] = new("bool", 0.0, 1.0),
		["BadgeVisible"] = new("bool", 0.0, 1.0),
		["ChatVisible"] = new("bool", 0.0, 1.0)
	};

	private readonly object _sync = new();

	private DateTime _loadedWriteTimeUtc = DateTime.MinValue;

	private bool _repairedOnLoad;

	public XDocument? Document { get; set; }

	public Dictionary<string, string> PresetPaths { get; } = new Dictionary<string, string>
	{
		{ "Rendering.FramerateCap", "FramerateCap" },
		{ "Rendering.SavedQualityLevel", "SavedQualityLevel" },
		{ "User.MouseSensitivity", "MouseSensitivity" },
		{ "User.VREnabled", "VREnabled" },
		{ "UI.Transparency", "PreferredTransparency" },
		{ "UI.ReducedMotion", "ReducedMotion" },
		{ "UI.FontSize", "PreferredTextSize" }
	};

	public bool Loaded { get; private set; }

	public bool RepairedOnLoad => _repairedOnLoad;

	public virtual string FileLocation => OperatingSystem.IsLinux() ? SoberFileLocation : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "GlobalBasicSettings_13.xml");

	private static string SoberFileLocation
	{
		get
		{
			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return Path.Combine(home, ".var", "app", "org.vinegarhq.Sober", "data", "sober", "appData", "GlobalBasicSettings_13.xml");
		}
	}

	public bool PreviousReadOnlyState { get; private set; }

	public bool SetPreset(string prefix, object? value)
	{
		bool applied = true;
		foreach (KeyValuePair<string, string> item in PresetPaths.Where((KeyValuePair<string, string> x) => x.Key.StartsWith(prefix, StringComparison.Ordinal)))
		{
			if (!SetProperty(item.Value, value))
			{
				applied = false;
			}
		}
		return applied;
	}

	public string? GetPreset(string prefix)
	{
		if (!PresetPaths.TryGetValue(prefix, out string? name))
		{
			return null;
		}
		return GetProperty(name);
	}

	public string? GetProperty(string name)
	{
		lock (_sync)
		{
			EnsureFresh();
			return FindProperties(Document)?
				.Elements()
				.LastOrDefault((XElement element) => element.Attribute("name")?.Value == name)?
				.Value;
		}
	}

	public bool GetBool(string name, bool defaultValue = false)
	{
		string? value = GetProperty(name);
		if (string.IsNullOrWhiteSpace(value))
		{
			return defaultValue;
		}
		return value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
	}

	public int GetInt(string name, int defaultValue)
	{
		if (!int.TryParse(GetProperty(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
		{
			return defaultValue;
		}
		return result;
	}

	public float GetFloat(string name, float defaultValue)
	{
		if (!float.TryParse(GetProperty(name), NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
		{
			return defaultValue;
		}
		return result;
	}

	public bool SetProperty(string name, object? value)
	{
		if (!KnownProperties.TryGetValue(name, out GBSProperty? property))
		{
			App.Logger?.WriteLine("GBSEditor::SetProperty", "Refusing to write the unknown setting " + name);
			return false;
		}

		lock (_sync)
		{
			EnsureFresh();
			XElement? properties = EnsureProperties(Document);
			if (properties is null)
			{
				App.Logger?.WriteLine("GBSEditor::SetProperty", "The settings document has no properties container, " + name + " was not changed");
				return false;
			}

			string? normalized = Normalize(property, value);
			if (normalized is null)
			{
				App.Logger?.WriteLine("GBSEditor::SetProperty", "The value for " + name + " could not be interpreted, it was not changed");
				return false;
			}

			List<XElement> existing = properties
				.Elements()
				.Where((XElement element) => element.Attribute("name")?.Value == name)
				.ToList();

			foreach (XElement duplicate in existing.Skip(1))
			{
				duplicate.Remove();
			}

			XElement? element2 = existing.FirstOrDefault();
			if (element2 is null || element2.Name.LocalName != property.Tag)
			{
				element2?.Remove();
				element2 = new XElement(property.Tag);
				element2.SetAttributeValue("name", name);
				properties.Add(element2);
			}

			element2.Value = normalized;
			return true;
		}
	}

	public bool SetBool(string name, bool value) => SetProperty(name, value);

	public bool SetInt(string name, int value) => SetProperty(name, value);

	public bool SetFloat(string name, float value) => SetProperty(name, value);

	private static string? Normalize(GBSProperty property, object? value)
	{
		if (value is null)
		{
			return null;
		}

		if (property.Tag == "bool")
		{
			if (value is bool flag)
			{
				return flag ? "true" : "false";
			}
			string text = value.ToString() ?? string.Empty;
			if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
			{
				return "true";
			}
			if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
			{
				return "false";
			}
			return null;
		}

		double number;
		if (value is IConvertible && value is not string)
		{
			try
			{
				number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				return null;
			}
		}
		else if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
		{
			return null;
		}

		if (double.IsNaN(number) || double.IsInfinity(number))
		{
			return null;
		}

		number = Math.Clamp(number, property.Minimum, property.Maximum);

		if (property.Tag == "float")
		{
			return number.ToString("G9", CultureInfo.InvariantCulture);
		}

		return ((long)Math.Round(number, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
	}

	private void EnsureFresh()
	{
		if (!Loaded)
		{
			Load();
			return;
		}

		try
		{
			string location = FileLocation;
			DateTime current = File.Exists(location) ? File.GetLastWriteTimeUtc(location) : DateTime.MinValue;
			if (current != _loadedWriteTimeUtc)
			{
				Load();
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("GBSEditor::EnsureFresh", ex);
		}
	}

	public void SetReadOnly(bool readOnly, bool preserveState = false)
	{
		if (!File.Exists(FileLocation))
		{
			return;
		}
		try
		{
			FileAttributes attributes = File.GetAttributes(FileLocation);
			attributes = ((!readOnly) ? (attributes & ~FileAttributes.ReadOnly) : (attributes | FileAttributes.ReadOnly));
			File.SetAttributes(FileLocation, attributes);
			if (!preserveState)
			{
				PreviousReadOnlyState = readOnly;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("GBSEditor::SetReadOnly", "Failed to set read-only on " + FileLocation);
			App.Logger?.WriteException("GBSEditor::SetReadOnly", ex);
		}
	}

	public bool GetReadOnly()
	{
		if (!File.Exists(FileLocation))
		{
			return false;
		}
		return File.GetAttributes(FileLocation).HasFlag(FileAttributes.ReadOnly);
	}

	public void Load()
	{
		App.Logger?.WriteLine("GBSEditor::Load", "Loading from " + FileLocation + "...");
		XDocument? loaded = null;
		_repairedOnLoad = false;
		try
		{
			if (File.Exists(FileLocation))
			{
				_loadedWriteTimeUtc = File.GetLastWriteTimeUtc(FileLocation);
				loaded = XDocument.Load(FileLocation);
			}
			else
			{
				_loadedWriteTimeUtc = DateTime.MinValue;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("GBSEditor::Load", "Failed to load, the settings file will be rebuilt");
			App.Logger?.WriteException("GBSEditor::Load", ex);
			loaded = null;
			_repairedOnLoad = true;
		}

		if (loaded is null)
		{
			Document = CreateDocument();
		}
		else if (IsWellFormed(loaded))
		{
			Document = loaded;
		}
		else
		{
			App.Logger?.WriteLine("GBSEditor::Load", "The settings file is not a valid Roblox document, rebuilding it and keeping the recognised values");
			Document = Rebuild(loaded);
			_repairedOnLoad = true;
		}

		EnsureProperties(Document);
		Loaded = true;
		PreviousReadOnlyState = GetReadOnly();
	}

	public bool RepairFile()
	{
		lock (_sync)
		{
			try
			{
				if (!File.Exists(FileLocation))
				{
					return false;
				}

				Load();
				if (!_repairedOnLoad)
				{
					return false;
				}

				if (!Save())
				{
					App.Logger?.WriteLine("GBSEditor::RepairFile", "The settings file needed repair but could not be written");
					return false;
				}

				App.Logger?.WriteLine("GBSEditor::RepairFile", "Repaired a malformed Roblox settings file");
				return true;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteException("GBSEditor::RepairFile", ex);
				return false;
			}
		}
	}

	private static bool IsWellFormed(XDocument document)
	{
		XElement? root = document.Root;
		if (root is null || root.Name.LocalName != "roblox")
		{
			return false;
		}

		if (root.Elements().Any((XElement child) => !AllowedRootChildren.Contains(child.Name.LocalName, StringComparer.Ordinal)))
		{
			return false;
		}

		return FindProperties(document) is not null;
	}

	private static XDocument Rebuild(XDocument source)
	{
		XDocument document = CreateDocument();
		XElement? properties = FindProperties(document);
		if (properties is null)
		{
			return document;
		}

		foreach (XElement candidate in source.Descendants())
		{
			string? name = candidate.Attribute("name")?.Value;
			if (string.IsNullOrEmpty(name) || !KnownProperties.TryGetValue(name, out GBSProperty? property))
			{
				continue;
			}

			string? normalized = Normalize(property, candidate.Value);
			if (normalized is null)
			{
				continue;
			}

			XElement? existing = properties.Elements().FirstOrDefault((XElement element) => element.Attribute("name")?.Value == name);
			if (existing is not null)
			{
				existing.Remove();
			}

			XElement rebuilt = new(property.Tag, normalized);
			rebuilt.SetAttributeValue("name", name);
			properties.Add(rebuilt);
		}

		return document;
	}

	public virtual bool Save()
	{
		App.Logger?.WriteLine("GBSEditor::Save", "Saving to " + FileLocation + "...");
		if (Document is null)
		{
			App.Logger?.WriteLine("GBSEditor::Save", "There is nothing to save, the settings document was never loaded");
			return false;
		}

		string location = FileLocation;
		string? directory = Path.GetDirectoryName(location);
		if (string.IsNullOrEmpty(directory))
		{
			App.Logger?.WriteLine("GBSEditor::Save", "The settings directory could not be resolved");
			return false;
		}

		string tempPath = Path.Combine(directory, Path.GetFileName(location) + "." + Guid.NewGuid().ToString("N") + ".tmp");
		bool restoreReadOnly = PreviousReadOnlyState;
		try
		{
			Directory.CreateDirectory(directory);
			SetReadOnly(readOnly: false, preserveState: true);
			Document.Save(tempPath);
			if (File.Exists(location))
			{
				try
				{
					File.Replace(tempPath, location, null, true);
				}
				catch (Exception replaceException)
				{
					App.Logger?.WriteLine("GBSEditor::Save", "Atomic replace was unavailable, writing in place: " + replaceException.Message);
					File.Copy(tempPath, location, true);
				}
			}
			else
			{
				File.Move(tempPath, location);
			}
			_loadedWriteTimeUtc = File.GetLastWriteTimeUtc(location);
			App.Logger?.WriteLine("GBSEditor::Save", "Save complete!");
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("GBSEditor::Save", "Failed to save");
			App.Logger?.WriteException("GBSEditor::Save", ex);
			return false;
		}
		finally
		{
			SetReadOnly(restoreReadOnly);
			DeleteTemporaryFile(tempPath);
		}
	}

	public void ResetProperties()
	{
		lock (_sync)
		{
			EnsureFresh();
			XElement? properties = FindProperties(Document);
			if (properties is null)
			{
				return;
			}

			foreach (XElement element in properties.Elements().Where((XElement element) => KnownProperties.ContainsKey(element.Attribute("name")?.Value ?? string.Empty)).ToList())
			{
				element.Remove();
			}
		}
	}

	public static XDocument CreateDocument()
	{
		XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
		XElement root = new("roblox",
			new XAttribute(XNamespace.Xmlns + "xmime", "http://www.w3.org/2005/05/xmlmime"),
			new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
			new XAttribute(xsi + "noNamespaceSchemaLocation", "http://www.roblox.com/roblox.xsd"),
			new XAttribute("version", "4"),
			new XElement("External", "null"),
			new XElement("External", "nil"),
			CreateUserGameSettingsItem());
		return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
	}

	public static XElement CreateUserGameSettingsItem()
	{
		return new XElement("Item",
			new XAttribute("class", "UserGameSettings"),
			new XAttribute("referent", "RBX" + Guid.NewGuid().ToString("N").ToUpperInvariant()),
			new XElement("Properties"));
	}

	public static XElement? FindProperties(XDocument? document)
	{
		return document?.Descendants("Item")
			.FirstOrDefault((XElement item) => item.Attribute("class")?.Value == "UserGameSettings")
			?.Element("Properties");
	}

	public static XElement? EnsureProperties(XDocument? document)
	{
		XElement? existing = FindProperties(document);
		if (existing is not null)
		{
			return existing;
		}

		XElement? root = document?.Root;
		if (root is null || root.Name.LocalName != "roblox")
		{
			return null;
		}

		XElement? item = document!.Descendants("Item")
			.FirstOrDefault((XElement candidate) => candidate.Attribute("class")?.Value == "UserGameSettings");
		if (item is null)
		{
			item = CreateUserGameSettingsItem();
			root.Add(item);
			return item.Element("Properties");
		}

		XElement properties = new("Properties");
		item.Add(properties);
		return properties;
	}

	private static void DeleteTemporaryFile(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("GBSEditor::DeleteTemporaryFile", ex);
		}
	}
}
