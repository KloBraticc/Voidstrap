using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Voidstrap;

public class GBSEditor
{
	public XDocument? Document { get; set; }

	public Dictionary<string, string> PresetPaths { get; } = new Dictionary<string, string>
	{
		{ "Rendering.FramerateCap", "{UserSettings}/int[@name='FramerateCap']" },
		{ "Rendering.SavedQualityLevel", "{UserSettings}/token[@name='SavedQualityLevel']" },
		{ "User.MouseSensitivity", "{UserSettings}/float[@name='MouseSensitivity']" },
		{ "User.VREnabled", "{UserSettings}/bool[@name='VREnabled']" },
		{ "UI.Transparency", "{UserSettings}/float[@name='PreferredTransparency']" },
		{ "UI.ReducedMotion", "{UserSettings}/bool[@name='ReducedMotion']" },
		{ "UI.FontSize", "{UserSettings}/token[@name='PreferredTextSize']" }
	};

	public Dictionary<string, string> RootPaths { get; } = new Dictionary<string, string> { { "UserSettings", "//Item[@class='UserGameSettings']/Properties" } };

	public bool Loaded { get; private set; }

	public string FileLocation => OperatingSystem.IsLinux() ? SoberFileLocation : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "GlobalBasicSettings_13.xml");

	private static string SoberFileLocation
	{
		get
		{
			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return Path.Combine(home, ".var", "app", "org.vinegarhq.Sober", "data", "sober", "appData", "GlobalBasicSettings_13.xml");
		}
	}

	public bool PreviousReadOnlyState { get; private set; }

	public void SetPreset(string prefix, object? value)
	{
		foreach (KeyValuePair<string, string> item in PresetPaths.Where<KeyValuePair<string, string>>((KeyValuePair<string, string> x) => x.Key.StartsWith(prefix)))
		{
			SetValue(item.Value, value);
		}
	}

	public string? GetPreset(string prefix)
	{
		if (!PresetPaths.ContainsKey(prefix))
		{
			return null;
		}
		return GetValue(PresetPaths[prefix]);
	}

	public void SetValue(string path, object? value)
	{
		path = ResolvePath(path);
		XElement xElement = Document?.XPathSelectElement(path);
		if (xElement != null)
		{
			xElement.Value = value?.ToString() ?? string.Empty;
		}
	}

	public string? GetValue(string path)
	{
		path = ResolvePath(path);
		return Document?.XPathSelectElement(path)?.Value;
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
		if (!File.Exists(FileLocation))
		{
			return;
		}
		try
		{
			Document = XDocument.Load(FileLocation);
			Loaded = true;
			PreviousReadOnlyState = GetReadOnly();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("GBSEditor::Load", "Failed to load!");
			App.Logger?.WriteException("GBSEditor::Load", ex);
		}
	}

	public virtual void Save()
	{
		App.Logger?.WriteLine("GBSEditor::Save", "Saving to " + FileLocation + "...");
		try
		{
			SetReadOnly(readOnly: false, preserveState: true);
			Document?.Save(FileLocation);
			SetReadOnly(PreviousReadOnlyState);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("GBSEditor::Save", "Failed to save");
			App.Logger?.WriteException("GBSEditor::Save", ex);
			return;
		}
		App.Logger?.WriteLine("GBSEditor::Save", "Save complete!");
	}

	private string ResolvePath(string rawPath)
	{
		return Regex.Replace(rawPath, "\\{(.+?)\\}", delegate(Match match)
		{
			string value = match.Groups[1].Value;
			string value2;
			return (!RootPaths.TryGetValue(value, out value2)) ? match.Value : value2;
		});
	}
}
