using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Utility;

namespace Voidstrap.Utility;

internal static class AppControl
{
	private const string LogIdent = "AppControl";

	private const long MaximumImageBytes = 64L * 1024 * 1024;

	private const long MaximumVideoBytes = 256L * 1024 * 1024;

	private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".gif", ".tif", ".tiff"];

	private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi", ".wmv"];

	private static readonly object Gate = new();

	private static Dictionary<string, string> _textOverrides = new(StringComparer.Ordinal);

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
		DependencyObject,
		Dictionary<DependencyProperty, object?>
	> Originals = new();

	private static int _installed;

	private static string ControlFolder => Path.Combine(Paths.Data, "AppControl");

	private static string TextOverridesPath => Path.Combine(ControlFolder, "TextOverrides.json");

	public static event Action? OverridesChanged;

	public static void Initialize()
	{
		if (Interlocked.Exchange(ref _installed, 1) == 1)
			return;
		lock (Gate)
			_textOverrides = LoadTextOverrides();
		Application? app = System.Windows.Application.Current;
		if (app == null)
			return;
		app.Dispatcher.BeginInvoke(
			new Action(() =>
			{
				System.Windows.EventManager.RegisterClassHandler(
					typeof(Window),
					FrameworkElement.LoadedEvent,
					new RoutedEventHandler(OnWindowLoaded),
					true
				);
				ApplyToOpenWindows();
			}),
			System.Windows.Threading.DispatcherPriority.ApplicationIdle
		);
	}

	private static void OnWindowLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is Window window)
			ApplyTree(window);
	}

	public static IReadOnlyDictionary<string, string> ListText()
	{
		lock (Gate)
			return new Dictionary<string, string>(_textOverrides, StringComparer.Ordinal);
	}

	public static string SetText(string key, string value)
	{
		string normalKey = NormalizeTextKey(key);
		if (normalKey.Length == 0)
			throw new InvalidDataException("The override target must start with text: or name:");
		if (
			normalKey.StartsWith("name:", StringComparison.Ordinal)
			&& (normalKey.Length <= 5 || !System.Text.RegularExpressions.Regex.IsMatch(normalKey[5..], "^[A-Za-z_][A-Za-z0-9_]*$"))
		)
			throw new InvalidDataException("A name target must be the XAML name of an element");
		lock (Gate)
		{
			if (value.Length == 0)
				_textOverrides.Remove(normalKey);
			else
				_textOverrides[normalKey] = value.Length > 2000 ? value[..2000] : value;
			SaveTextOverrides();
		}
		ApplyToOpenWindows();
		OverridesChanged?.Invoke();
		return normalKey;
	}

	public static bool ClearText(string key)
	{
		string normalKey = NormalizeTextKey(key);
		bool removed;
		lock (Gate)
			removed = _textOverrides.Remove(normalKey);
		if (removed)
		{
			SaveTextOverrides();
			ApplyToOpenWindows();
			OverridesChanged?.Invoke();
		}
		return removed;
	}

	public static int ClearAllText()
	{
		int removed;
		lock (Gate)
		{
			removed = _textOverrides.Count;
			_textOverrides.Clear();
			SaveTextOverrides();
		}
		if (removed > 0)
		{
			ApplyToOpenWindows();
			OverridesChanged?.Invoke();
		}
		return removed;
	}

	private static string NormalizeTextKey(string key)
	{
		string trimmed = (key ?? "").Trim();
		if (trimmed.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
			return "text:" + trimmed[5..];
		if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
			return "name:" + trimmed[5..];
		return "";
	}

	private static Dictionary<string, string> LoadTextOverrides()
	{
		try
		{
			if (!File.Exists(TextOverridesPath))
				return new Dictionary<string, string>(StringComparer.Ordinal);
			if (JsonNode.Parse(File.ReadAllText(TextOverridesPath)) is JsonObject root && root["overrides"] is JsonObject entries)
			{
				Dictionary<string, string> loaded = new(StringComparer.Ordinal);
				foreach (KeyValuePair<string, JsonNode?> entry in entries)
				{
					string key = NormalizeTextKey(entry.Key);
					string? value = entry.Value?.ToString();
					if (key.Length > 0 && !string.IsNullOrEmpty(value))
						loaded[key] = value;
				}
				return loaded;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The text overrides could not be read: " + ex.Message);
		}
		return new Dictionary<string, string>(StringComparer.Ordinal);
	}

	private static void SaveTextOverrides()
	{
		try
		{
			Directory.CreateDirectory(ControlFolder);
			JsonObject entries = new();
			foreach (KeyValuePair<string, string> pair in _textOverrides)
				entries[pair.Key] = pair.Value;
			JsonObject root = new() { ["version"] = 1, ["overrides"] = entries };
			string temporary = TextOverridesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
				File.Move(temporary, TextOverridesPath, true);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The text overrides could not be saved: " + ex.Message);
		}
	}

	public static void ApplyToOpenWindows()
	{
		Application? app = System.Windows.Application.Current;
		if (app == null)
			return;
		if (app.CheckAccess())
			ApplyAllWindows();
		else
			app.Dispatcher.BeginInvoke(new Action(ApplyAllWindows), System.Windows.Threading.DispatcherPriority.Background);
	}

	private static void ApplyAllWindows()
	{
		Application? app = System.Windows.Application.Current;
		if (app == null)
			return;
		foreach (Window window in app.Windows.OfType<Window>().ToArray())
		{
			try
			{
				ApplyTree(window);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "A window could not be updated: " + ex.Message);
			}
		}
	}

	private static void ApplyTree(DependencyObject root)
	{
		Dictionary<string, string>? snapshot = null;
		void Walk(DependencyObject node)
		{
			int count = VisualTreeHelper.GetChildrenCount(node);
			for (int index = 0; index < count; index++)
				Walk(VisualTreeHelper.GetChild(node, index));
			if (Originals.TryGetValue(node, out Dictionary<DependencyProperty, object?>? stored))
			{
				foreach (KeyValuePair<DependencyProperty, object?> entry in stored)
				{
					if (node.ReadLocalValue(entry.Key) != DependencyProperty.UnsetValue)
						node.SetValue(entry.Key, entry.Value);
				}
			}
			if (node is FrameworkElement { Name.Length: > 0 })
			{
				snapshot ??= SnapshotOverrides();
				if (
					snapshot.TryGetValue("name:" + ((FrameworkElement)node).Name, out string? namedValue) && ApplyOverride(node, namedValue)
				)
					return;
			}
			if (node is TextBlock textBlock)
			{
				string? original = GetOriginal(textBlock, TextBlock.TextProperty) as string ?? textBlock.Text;
				if (original.Length > 0)
				{
					snapshot ??= SnapshotOverrides();
					if (snapshot.TryGetValue("text:" + original, out string? value))
						SetOverride(textBlock, TextBlock.TextProperty, value);
				}
			}
			else if (node is ContentControl contentControl && contentControl.Content is string contentText && contentText.Length > 0)
			{
				string? original = GetOriginal(contentControl, ContentControl.ContentProperty) as string ?? contentText;
				snapshot ??= SnapshotOverrides();
				if (snapshot.TryGetValue("text:" + original, out string? value))
					SetOverride(contentControl, ContentControl.ContentProperty, value);
			}
			else if (node is HeaderedContentControl headered && headered.Header is string headerText && headerText.Length > 0)
			{
				string? original = GetOriginal(headered, HeaderedContentControl.HeaderProperty) as string ?? headerText;
				snapshot ??= SnapshotOverrides();
				if (snapshot.TryGetValue("text:" + original, out string? value))
					SetOverride(headered, HeaderedContentControl.HeaderProperty, value);
			}
		}
		Walk(root);
	}

	private static Dictionary<string, string> SnapshotOverrides()
	{
		lock (Gate)
			return new Dictionary<string, string>(_textOverrides, StringComparer.Ordinal);
	}

	private static bool ApplyOverride(DependencyObject element, string value)
	{
		if (element is TextBlock textBlock)
		{
			SetOverride(textBlock, TextBlock.TextProperty, value);
			return true;
		}
		if (element is ContentControl contentControl && (contentControl.Content is string || contentControl.Content == null))
		{
			SetOverride(contentControl, ContentControl.ContentProperty, value);
			return true;
		}
		if (element is HeaderedContentControl headered && (headered.Header is string || headered.Header == null))
		{
			SetOverride(headered, HeaderedContentControl.HeaderProperty, value);
			return true;
		}
		return false;
	}

	private static object? GetOriginal(DependencyObject element, DependencyProperty property)
	{
		if (
			Originals.TryGetValue(element, out Dictionary<DependencyProperty, object?>? entries)
			&& entries.TryGetValue(property, out object? original)
		)
			return original;
		return null;
	}

	private static void SetOverride(DependencyObject element, DependencyProperty property, object? value)
	{
		if (!Originals.TryGetValue(element, out Dictionary<DependencyProperty, object?>? entries))
		{
			entries = new Dictionary<DependencyProperty, object?>();
			Originals.Add(element, entries);
		}
		if (!entries.ContainsKey(property))
			entries[property] = element.GetValue(property);
		if (!Equals(element.GetValue(property), value))
			element.SetValue(property, value);
	}

	public static JsonObject GetBackground()
	{
		JsonObject result = new()
		{
			["backgroundFilePath"] = "",
			["gradientOpacity"] = 1.0,
			["blackOverlayOpacity"] = 0.0,
			["displayEverywhere"] = false,
		};
		try
		{
			if (File.Exists(Paths.BackgroundSettings) && JsonNode.Parse(File.ReadAllText(Paths.BackgroundSettings)) is JsonObject root)
			{
				result["backgroundFilePath"] = root["BackgroundFilePath"]?.ToString() ?? "";
				result["gradientOpacity"] = root["GradientOpacity"]?.GetValue<double>() ?? 1.0;
				result["blackOverlayOpacity"] = root["BlackOverlayOpacity"]?.GetValue<double>() ?? 0.0;
				result["displayEverywhere"] = root["DisplayEverywhere"]?.GetValue<bool>() ?? false;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The background state could not be read: " + ex.Message);
		}
		return result;
	}

	public static string SetBackground(string sourcePath, double? gradientOpacity, double? blackOverlayOpacity, bool? displayEverywhere)
	{
		if (string.IsNullOrWhiteSpace(sourcePath))
			throw new InvalidDataException("Choose an image or video file for the background");
		FileInfo source = new(sourcePath);
		if (!source.Exists)
			throw new FileNotFoundException(Path.GetFileName(sourcePath));
		string extension = source.Extension.ToLowerInvariant();
		bool isImage = ImageExtensions.Contains(extension);
		bool isVideo = VideoExtensions.Contains(extension);
		if (!isImage && !isVideo)
			throw new InvalidDataException("The background must be an image like PNG or JPG or a video like MP4 or WEBM");
		long limit = isVideo ? MaximumVideoBytes : MaximumImageBytes;
		if (source.Length > limit)
			throw new InvalidDataException("The background file is larger than " + (limit / 1024 / 1024) + " MB");
		Directory.CreateDirectory(ControlFolder);
		foreach (string existing in Directory.EnumerateFiles(ControlFolder, "Background.*"))
			Filesystem.DeleteWritableFile(existing);
		string stored = Path.Combine(ControlFolder, "Background" + extension);
		File.Copy(sourcePath, stored, true);

		JsonObject current = GetBackground();
		double grad = Math.Clamp(gradientOpacity ?? current["gradientOpacity"]?.GetValue<double>() ?? 1.0, 0.0, 1.0);
		double black = Math.Clamp(blackOverlayOpacity ?? current["blackOverlayOpacity"]?.GetValue<double>() ?? 0.0, 0.0, 1.0);
		bool everywhere = displayEverywhere ?? current["displayEverywhere"]?.GetValue<bool>() ?? false;
		WriteBackgroundState(stored, grad, black, everywhere);
		return stored;
	}

	public static void ClearBackground()
	{
		try
		{
			string folder = ControlFolder;
			if (Directory.Exists(folder))
			{
				foreach (string existing in Directory.EnumerateFiles(folder, "Background.*"))
					Filesystem.DeleteWritableFile(existing);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The stored background could not be removed: " + ex.Message);
		}
		WriteBackgroundState(null, 1.0, 0.0, false);
	}

	private static void WriteBackgroundState(
		string? backgroundFilePath,
		double gradientOpacity,
		double blackOverlayOpacity,
		bool displayEverywhere
	)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Paths.BackgroundSettings)!);
			JsonObject state = new()
			{
				["BackgroundFilePath"] = backgroundFilePath,
				["GradientOpacity"] = gradientOpacity,
				["BlackOverlayOpacity"] = blackOverlayOpacity,
				["DisplayEverywhere"] = displayEverywhere,
			};
			string temporary = Paths.BackgroundSettings + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
				File.Move(temporary, Paths.BackgroundSettings, true);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The background state could not be saved: " + ex.Message);
			return;
		}
		Application? app = System.Windows.Application.Current;
		if (app == null)
			return;
		app.Dispatcher.BeginInvoke(
			new Action(() =>
			{
				try
				{
					Voidstrap.UI.GlobalBackground.Update(backgroundFilePath, gradientOpacity, blackOverlayOpacity, displayEverywhere);
					Voidstrap.UI.GlobalBackground.Reload();
				}
				catch (Exception ex)
				{
					App.Logger?.WriteLine(LogIdent, "The background could not be applied: " + ex.Message);
				}
			}),
			System.Windows.Threading.DispatcherPriority.Background
		);
	}
}
