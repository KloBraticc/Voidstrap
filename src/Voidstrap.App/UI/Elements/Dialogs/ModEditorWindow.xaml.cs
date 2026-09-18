using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Integrations.CommunityMods;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Settings;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class ModEditorWindow : WpfUiWindow
{
	private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

	private readonly string _recordId;

	private readonly ModPackInfo? _pack;

	private readonly CancellationTokenSource _lifetime = new();

	private readonly Dictionary<string, LoadedConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

	private List<ModSlotRow> _slots = [];

	private bool _rulesMode;

	public bool Changed { get; private set; }

	public ModEditorWindow(string recordId, string name, ModPackInfo? pack)
	{
		_recordId = recordId;
		_pack = pack;
		InitializeComponent();
		ModTitle.Text = name;
		Loaded += OnLoaded;
		Closed += OnClosed;
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnLoaded;
		try
		{
			_rulesMode = await Task.Run(() => ExternalModConfigs.IsExternal(_recordId), _lifetime.Token);
			if (_rulesMode)
			{
				await LoadConfigsAsync();
			}
			else
			{
				await LoadSlotsAsync();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			ShowEmpty("The mod could not be read: " + ex.Message);
		}
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;
		Closed -= OnClosed;
		_lifetime.Cancel();
		_lifetime.Dispose();
	}

	private async Task LoadSlotsAsync()
	{
		SetBusy(true, "Reading the mod files");
		ModHint.Text = "Pick which file this mod uses for each Roblox file it replaces. Changes apply the next time Roblox launches.";
		CancellationToken token = _lifetime.Token;
		(List<ModSlotRow> rows, bool hasSources) = await Task.Run(() =>
		{
			List<ModSlotRow> built = [.. ModVariantStore.BuildSlots(_recordId).Select(slot => new ModSlotRow(slot))];
			Parallel.ForEach(built, new ParallelOptions { CancellationToken = token }, row => row.Selected.PreloadPreview());
			return (built, ModVariantStore.HasSources(_recordId));
		}, token);
		_slots = rows;
		SlotList.ItemsSource = _slots;

		bool canDownload = _pack != null && _pack.Id > 0 && string.Equals(_pack.Source, GameBananaCatalog.SourceName, StringComparison.OrdinalIgnoreCase);
		DownloadOptionsButton.Visibility = canDownload ? Visibility.Visible : Visibility.Collapsed;
		DownloadOptionsButton.Content = hasSources ? "Refresh options from GameBanana" : "Get every option from GameBanana";

		SetBusy(false, "");
		if (_slots.Count == 0)
		{
			ShowEmpty("This mod has no images, sounds or fonts to choose from.");
			return;
		}
		EmptyText.Visibility = Visibility.Collapsed;
		SlotList.Visibility = Visibility.Visible;
		SaveButton.IsEnabled = true;
		int choices = _slots.Count(slot => slot.Options.Count > 2);
		StatusText.Text = _slots.Count + (_slots.Count == 1 ? " file" : " files") + ", " + choices + " with more than one option";
	}

	private async void DownloadOptions_Click(object sender, RoutedEventArgs e)
	{
		SetBusy(true, "Downloading the mod options");
		Progress<string> progress = new Progress<string>(OnDownloadProgress);
		try
		{
			int count = await Task.Run(() => ModVariantStore.DownloadSourcesAsync(_recordId, _pack, progress, _lifetime.Token), _lifetime.Token);
			await LoadSlotsAsync();
			StatusText.Text = count > 0
				? "Found " + count + " files on GameBanana. " + StatusText.Text
				: "GameBanana did not have any extra options for this mod.";
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			SetBusy(false, "The options could not be downloaded: " + ex.Message);
		}
	}

	private void OnDownloadProgress(string message)
	{
		StatusText.Text = message;
	}

	private async Task LoadConfigsAsync()
	{
		SetBusy(true, "Reading the replacement config");
		ModHint.Text = "Turn rules on or off and change what each one replaces. AssetWarp picks up saved changes within a few seconds, Fleasion on its next start.";
		List<ReplacementConfigFile> files = await Task.Run(() => ExternalModConfigs.GetConfigFiles(_recordId), _lifetime.Token);
		SetBusy(false, "");
		if (files.Count == 0)
		{
			ShowEmpty("The replacement config for this mod could not be found. Reinstall it from Mod Packs.");
			return;
		}
		ConfigPicker.Visibility = files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
		ConfigPicker.ItemsSource = files;
		ConfigPicker.SelectedIndex = 0;
	}

	private async void ConfigPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (ConfigPicker.SelectedItem is ReplacementConfigFile file)
		{
			try
			{
				await ShowConfigAsync(file);
			}
			catch (OperationCanceledException)
			{
			}
		}
	}

	private async Task ShowConfigAsync(ReplacementConfigFile file)
	{
		try
		{
			if (!_configs.TryGetValue(file.Path, out LoadedConfig? loaded))
			{
				SetBusy(true, "Reading the replacement config");
				RuleList.Visibility = Visibility.Collapsed;
				loaded = await Task.Run(() => ReadConfig(file), _lifetime.Token);
				_configs[file.Path] = loaded;
				SetBusy(false, "");
				if (!ReferenceEquals(ConfigPicker.SelectedItem, file) && ConfigPicker.Items.Count > 1)
				{
					return;
				}
			}
			RuleList.ItemsSource = loaded.Rows;
			RuleList.Visibility = Visibility.Visible;
			EmptyText.Visibility = loaded.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
			EmptyText.Text = "This config has no rules.";
			SaveButton.IsEnabled = true;
			int enabled = loaded.Rows.Count(row => row.Enabled);
			StatusText.Text = loaded.Rows.Count + (loaded.Rows.Count == 1 ? " rule, " : " rules, ") + enabled + " on" + (file.Parked ? ". This mod is turned off in My Mods." : "");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
		{
			ShowEmpty("The config could not be read: " + ex.Message);
		}
	}

	private static LoadedConfig ReadConfig(ReplacementConfigFile file)
	{
		JsonObject root = JsonNode.Parse(File.ReadAllText(file.Path)) as JsonObject
			?? throw new InvalidDataException("The config is not a JSON object.");
		JsonArray rules = root["replacement_rules"] as JsonArray
			?? throw new InvalidDataException("The config has no replacement rules.");
		IReadOnlyList<string> suggestions = FindLocalAssets(file);
		return new LoadedConfig(file, root, [.. Flatten(rules).Select(rule => new ReplacementRuleRow(rule, suggestions))]);
	}

	private async void Save_Click(object sender, RoutedEventArgs e)
	{
		SaveButton.IsEnabled = false;
		try
		{
			if (_rulesMode)
			{
				foreach (LoadedConfig config in _configs.Values)
				{
					foreach (ReplacementRuleRow row in config.Rows)
					{
						string? problem = row.Validate();
						if (problem != null)
						{
							StatusText.Text = (row.Name.Length > 0 ? row.Name + ": " : "") + problem;
							SaveButton.IsEnabled = true;
							return;
						}
					}
				}
				List<LoadedConfig> configs = [.. _configs.Values];
				await Task.Run(async () =>
				{
					foreach (LoadedConfig config in configs)
					{
						foreach (ReplacementRuleRow row in config.Rows)
						{
							row.WriteBack();
						}
						string temporary = config.File.Path + ".tmp";
						await File.WriteAllTextAsync(temporary, config.Root.ToJsonString(WriteOptions), new UTF8Encoding(false), _lifetime.Token);
						File.Move(temporary, config.File.Path, overwrite: true);
					}
					Voidstrap.Integrations.AssetProxy.TextureStripper.InvalidateRuntimeState();
				}, _lifetime.Token);
			}
			else
			{
				await Task.Run(() => ModVariantStore.ApplySlots(_recordId, _slots.Select(row => row.Slot)), _lifetime.Token);
			}
			Changed = true;
			Close();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			StatusText.Text = "The changes could not be saved: " + ex.Message;
			SaveButton.IsEnabled = true;
		}
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void SetBusy(bool busy, string status)
	{
		LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
		DownloadOptionsButton.IsEnabled = !busy;
		SaveButton.IsEnabled = !busy && (_slots.Count > 0 || _configs.Count > 0);
		if (busy)
		{
			SlotList.Visibility = Visibility.Collapsed;
			EmptyText.Visibility = Visibility.Collapsed;
		}
		StatusText.Text = status;
	}

	private void ShowEmpty(string message)
	{
		LoadingRing.Visibility = Visibility.Collapsed;
		EmptyText.Text = message;
		EmptyText.Visibility = Visibility.Visible;
		SaveButton.IsEnabled = false;
	}

	private static IEnumerable<JsonObject> Flatten(JsonArray rules)
	{
		foreach (JsonNode? node in rules)
		{
			if (node is not JsonObject rule)
			{
				continue;
			}
			yield return rule;
			if (rule["children"] is JsonArray children)
			{
				foreach (JsonObject child in Flatten(children))
				{
					yield return child;
				}
			}
		}
	}

	private static IReadOnlyList<string> FindLocalAssets(ReplacementConfigFile file)
	{
		try
		{
			string root = file.ConfigsFolder;
			string assetFolder = Path.Combine(root, file.Fleasion ? "Voidstrap Mods" : "Assets");
			if (!Directory.Exists(assetFolder))
			{
				return [];
			}
			return [.. Directory.EnumerateFiles(assetFolder, "*", SearchOption.AllDirectories)
				.Take(2000)
				.Select(path => (file.Fleasion ? "/" : "") + Path.GetRelativePath(root, path).Replace('\\', '/'))
				.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private sealed record LoadedConfig(ReplacementConfigFile File, JsonObject Root, List<ReplacementRuleRow> Rows);
}

internal sealed class ModSlotRow : NotifyPropertyChangedViewModel
{
	private ModSlotOptionRow _selected;

	public ModSlotRow(ModSlot slot)
	{
		Slot = slot;
		Options = [.. slot.Options.Select(option => new ModSlotOptionRow(option))];
		_selected = Options.FirstOrDefault(option => ReferenceEquals(option.Option, slot.Selected)) ?? Options[0];
	}

	public ModSlot Slot { get; }

	public string Title => Path.GetFileName(Slot.TargetRelative);

	public string Detail => Slot.TargetRelative;

	public IReadOnlyList<ModSlotOptionRow> Options { get; }

	public ModSlotOptionRow Selected
	{
		get => _selected;
		set
		{
			if (value == null || !SetProperty(ref _selected, value))
			{
				return;
			}
			Slot.Selected = value.Option;
			OnPropertyChanged(nameof(Preview));
			_ = RefreshPreviewAsync(value);
		}
	}

	private async Task RefreshPreviewAsync(ModSlotOptionRow option)
	{
		await option.EnsurePreviewAsync();
		if (ReferenceEquals(_selected, option))
		{
			OnPropertyChanged(nameof(Preview));
		}
	}

	public ImageSource? Preview => _selected.Preview;
}

internal sealed class ModSlotOptionRow : NotifyPropertyChangedViewModel
{
	private Task<ImageSource?>? _preview;

	public ModSlotOptionRow(ModSlotOption option)
	{
		Option = option;
	}

	public ModSlotOption Option { get; }

	public string Label => Option.Label;

	public ImageSource? Preview
	{
		get
		{
			Task<ImageSource?> preview = EnsurePreviewAsync();
			return preview.IsCompletedSuccessfully ? preview.Result : null;
		}
	}

	public void PreloadPreview()
	{
		_preview ??= Task.FromResult(LoadPreview(Option.SourcePath));
	}

	public Task<ImageSource?> EnsurePreviewAsync()
	{
		if (_preview == null)
		{
			string? path = Option.SourcePath;
			_preview = path == null || !ModVariantStore.PreviewExtensions.Contains(Path.GetExtension(path))
				? Task.FromResult<ImageSource?>(null)
				: LoadPreviewAsync(path);
		}
		return _preview;
	}

	private async Task<ImageSource?> LoadPreviewAsync(string path)
	{
		ImageSource? image = await Task.Run(() => LoadPreview(path));
		OnPropertyChanged(nameof(Preview));
		return image;
	}

	private static ImageSource? LoadPreview(string? path)
	{
		if (path == null || !ModVariantStore.PreviewExtensions.Contains(Path.GetExtension(path)))
		{
			return null;
		}
		try
		{
			BitmapImage image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
			image.DecodePixelWidth = 96;
			image.UriSource = new Uri(path);
			image.EndInit();
			image.Freeze();
			return image;
		}
		catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or InvalidOperationException)
		{
			return null;
		}
	}
}

internal sealed class ReplacementRuleRow : NotifyPropertyChangedViewModel
{
	private static readonly string[] TargetKeys = ["with_id", "replace_with", "local_path", "cdn_url"];

	private readonly JsonObject _node;

	private bool _enabled;

	private string _name;

	private string _ids;

	private string _mode;

	private string _target;

	public ReplacementRuleRow(JsonObject node, IReadOnlyList<string> suggestions)
	{
		_node = node;
		Suggestions = suggestions;
		_enabled = node["enabled"] is not JsonValue enabled || !enabled.TryGetValue(out bool value) || value;
		_name = Text(node["name"]);
		_ids = node["replace_ids"] is JsonArray ids ? string.Join(", ", ids.Select(Text).Where(id => id.Length > 0)) : "";
		string mode = Text(node["mode"]).ToLowerInvariant();
		_mode = ModeOptions.Contains(mode) ? mode : "id";
		_target = TargetKeys.Select(key => Text(node[key])).FirstOrDefault(text => text.Length > 0) ?? "";
	}

	private static readonly string[] ModeOptions = ["id", "local", "cdn", "remove"];

	public IReadOnlyList<string> Modes => ModeOptions;

	public IReadOnlyList<string> Suggestions { get; }

	public bool Enabled
	{
		get => _enabled;
		set => SetProperty(ref _enabled, value);
	}

	public string Name
	{
		get => _name;
		set => SetProperty(ref _name, value ?? "");
	}

	public string Ids
	{
		get => _ids;
		set => SetProperty(ref _ids, value ?? "");
	}

	public string Mode
	{
		get => _mode;
		set
		{
			if (SetProperty(ref _mode, value ?? "id"))
			{
				OnPropertyChanged(nameof(TargetEnabled));
				OnPropertyChanged(nameof(TargetHint));
			}
		}
	}

	public string Target
	{
		get => _target;
		set => SetProperty(ref _target, value ?? "");
	}

	public bool TargetEnabled => _mode != "remove";

	public string TargetHint => _mode switch
	{
		"id" => "The asset id to use instead, leave empty to remove the asset",
		"local" => "The file to use instead",
		"cdn" => "A web address to load the asset from",
		_ => "Removed assets do not need a target"
	};

	public string? Validate()
	{
		if (SplitIds().Count == 0)
		{
			return "add at least one asset id to replace.";
		}
		string target = _target.Trim();
		if (_mode == "local" && target.Length == 0)
		{
			return "pick the file to use.";
		}
		if (_mode == "cdn" && (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")))
		{
			return "the web address must start with https.";
		}
		return null;
	}

	public void WriteBack()
	{
		_node["enabled"] = _enabled;
		_node["name"] = _name.Trim();
		JsonArray ids = [];
		foreach (string id in SplitIds())
		{
			ids.Add(long.TryParse(id, out long number) ? JsonValue.Create(number) : JsonValue.Create(id));
		}
		_node["replace_ids"] = ids;
		_node["mode"] = _mode;
		foreach (string key in TargetKeys)
		{
			_node.Remove(key);
		}
		string target = _target.Trim();
		if (target.Length == 0)
		{
			return;
		}
		switch (_mode)
		{
			case "id":
				_node["with_id"] = long.TryParse(target, out long withId) ? JsonValue.Create(withId) : JsonValue.Create(target);
				break;
			case "local":
				_node["local_path"] = target;
				break;
			case "cdn":
				_node["cdn_url"] = target;
				break;
		}
	}

	private List<string> SplitIds()
	{
		return [.. _ids.Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
	}

	private static string Text(JsonNode? node)
	{
		if (node is null)
		{
			return "";
		}
		if (node is JsonValue value && value.TryGetValue(out string? text))
		{
			return text ?? "";
		}
		return node.ToJsonString().Trim('"').Trim();
	}
}
