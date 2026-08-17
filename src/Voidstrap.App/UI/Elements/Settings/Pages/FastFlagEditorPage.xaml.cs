using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Voidstrap.Models;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Dialogs;
using Wpf.Ui.Controls;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class FastFlagEditorPage : UiPage
{
	public static class FastFlagTagHelper
	{
		private static bool Has(string name, string token) => name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

		private static bool HasAny(string name, params string[] tokens)
		{
			foreach (string token in tokens)
			{
				if (Has(name, token))
					return true;
			}
			return false;
		}

		public static List<string> GetTags(string name)
		{
			List<string> tags = new List<string>();
			if (string.IsNullOrEmpty(name))
			{
				tags.Add("Unknown");
				return tags;
			}
			if (HasAny(name, "perf", "fps", "frame", "frm", "render", "thread", "graphics"))
				tags.Add("Performance");
			if (HasAny(name, "fix", "debug", "crash", "stability"))
				tags.Add("Fix");
			if (HasAny(name, "experimental", "test", "task", "beta"))
				tags.Add("Experimental");
			if (HasAny(name, "graphics", "render", "quality", "gpu", "shader", "postfx", "texture", "blur", "voxel", "detail", "lighting"))
				tags.Add("Graphics");
			if (HasAny(name, "distance", "level", "lod"))
				tags.Add("LOD");
			if (HasAny(name, "ui", "ux", "menu", "title", "interface"))
				tags.Add("UI");
			if (tags.Count == 0)
				tags.Add("Unknown");
			return tags;
		}
	}

	public enum FlagHistoryAction
	{
		Added,
		Edited,
		Renamed,
		Deleted,
		Imported,
		Cleared,
		Restored
	}

	public class FlagHistoryEntry
	{
		public FlagHistoryAction Action { get; set; }

		public string FlagName { get; set; } = string.Empty;

		public string? OldValue { get; set; }

		public string? NewValue { get; set; }

		public DateTime Timestamp { get; set; }

		public Dictionary<string, string>? Snapshot { get; set; }

		public override string ToString()
		{
			string time = Timestamp.ToString("HH:mm:ss");
			return Action switch
			{
				FlagHistoryAction.Added => $"{time}  Added '{FlagName}' with value '{NewValue}'",
				FlagHistoryAction.Edited => $"{time}  Changed '{FlagName}' from '{OldValue}' to '{NewValue}'",
				FlagHistoryAction.Renamed => $"{time}  Renamed '{FlagName}' to '{NewValue}'",
				FlagHistoryAction.Deleted => $"{time}  Deleted '{FlagName}', was '{OldValue}'",
				FlagHistoryAction.Imported => $"{time}  Imported {FlagName}",
				FlagHistoryAction.Cleared => $"{time}  Deleted all flags ({FlagName})",
				FlagHistoryAction.Restored => $"{time}  Reverted '{FlagName}'",
				_ => $"{time}  '{FlagName}'"
			};
		}
	}

	private const int MaxHistoryEntries = 100;

	private const int MaxSnapshotHistoryEntries = 2;

	private const int SearchDebounceMs = 180;

	private static readonly ObservableCollection<FlagHistoryEntry> _flagHistory = new ObservableCollection<FlagHistoryEntry>();

	private static readonly HashSet<string> _presetFlagValues = new HashSet<string>(FastFlagManager.PresetFlags.Values, StringComparer.Ordinal);

	private static readonly JsonSerializerOptions _indentedJson = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	private static readonly JsonSerializerOptions _lenientJson = new JsonSerializerOptions
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	private static readonly Regex _groupPrefixRegex = new Regex("^[A-Z]+[a-z]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly ImageSource _presetCheck = LoadIcon("pack://application:,,,/Resources/Checkmark.ico");

	private static readonly ImageSource _presetCross = LoadIcon("pack://application:,,,/Resources/CrossMark.ico");

	private static readonly string[] _flagSourceUrls = new string[]
	{
		"https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCClientBootstrapper.json",
		"https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCStudioApp.json",
		"https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCDesktopClient.json",
		"https://raw.githubusercontent.com/MaximumADHD/Roblox-Client-Tracker/refs/heads/roblox/FVariables.txt",
		"https://clientsettings.roblox.com/v2/settings/application/PCDesktopClient"
	};

	private static readonly string[] _flagPrefixes = new string[] { "FFlag", "DFFlag", "SFFlag", "FInt", "DFInt", "FString", "DFString", "FLog", "DFLog", "FDouble" };

	private static volatile HashSet<string>? _knownFlags;

	private static Task? _knownFlagsTask;

	private readonly ObservableCollection<FastFlag> _allFlags = new ObservableCollection<FastFlag>();

	private readonly DispatcherTimer _searchDebounce;

	private ListCollectionView? _view;

	private bool _showPresets = true;

	private string _searchFilter = string.Empty;

	private bool _suppressSearchChanged;

	private bool _suggestionVisible;

	private bool _searchTimerAttached;

	private static ImageSource LoadIcon(string uri)
	{
		BitmapImage image = new BitmapImage();
		image.BeginInit();
		image.UriSource = new Uri(uri, UriKind.Absolute);
		image.CacheOption = BitmapCacheOption.OnLoad;
		image.EndInit();
		image.Freeze();
		return image;
	}

	public FastFlagEditorPage()
	{
		InitializeComponent();
		TogglePresetsButton.IsChecked = true;
		_searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(SearchDebounceMs)
		};
		HistoryListBox.ItemsSource = _flagHistory;
		UpdateHistoryCount();
	}

	private async void Page_Loaded(object sender, RoutedEventArgs e)
	{
		if (!_searchTimerAttached)
		{
			_searchDebounce.Tick += SearchDebounce_Tick;
			_searchTimerAttached = true;
		}
		ReloadList();
		try
		{
			await EnsureKnownFlagsAsync();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("FastFlagEditor::KnownFlags", "Known flag list unavailable: " + ex.Message);
			_knownFlagsTask = null;
			return;
		}
		UpdateExistsColumn();
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e)
	{
		_searchDebounce.Stop();
		if (_searchTimerAttached)
		{
			_searchDebounce.Tick -= SearchDebounce_Tick;
			_searchTimerAttached = false;
		}
		CancelPendingEdit();
	}

	private void CancelPendingEdit()
	{
		try
		{
			DataGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
			DataGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
		}
		catch
		{
			try
			{
				DataGrid.CancelEdit(DataGridEditingUnit.Row);
			}
			catch
			{
			}
		}
	}

	private static Task EnsureKnownFlagsAsync()
	{
		if (_knownFlags != null)
		{
			return Task.CompletedTask;
		}
		return _knownFlagsTask ??= LoadKnownFlagsCoreAsync();
	}

	private static async Task LoadKnownFlagsCoreAsync()
	{
		List<string>[] results = await Task.WhenAll(_flagSourceUrls.Select(FetchFlagNamesAsync)).ConfigureAwait(continueOnCapturedContext: false);
		HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
		foreach (List<string> names in results)
		{
			set.UnionWith(names);
		}
		if (set.Count > 0)
		{
			_knownFlags = set;
		}
		else
		{
			_knownFlagsTask = null;
		}
	}

	private static async Task<List<string>> FetchFlagNamesAsync(string url)
	{
		List<string> names = new List<string>();
		try
		{
			string content = await Voidstrap.Utility.Http.GetString(url).ConfigureAwait(continueOnCapturedContext: false);
			if (url.EndsWith(".json") || url.Contains("clientsettings.roblox.com"))
			{
				using JsonDocument doc = JsonDocument.Parse(content);
				JsonElement root = doc.RootElement;
				if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("applicationSettings", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
				{
					root = nested;
				}
				if (root.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty prop in root.EnumerateObject())
					{
						names.Add(prop.Name);
					}
				}
			}
			else
			{
				using StringReader reader = new StringReader(content);
				string? line;
				while ((line = await reader.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false)) != null)
				{
					string name = ParseFlagLine(line);
					if (name.Length != 0)
					{
						names.Add(name);
					}
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("FastFlagEditor::KnownFlags", "Failed to fetch " + url + ": " + ex.Message);
		}
		return names;
	}

	private static string ParseFlagLine(string line)
	{
		line = line.Trim();
		if (line.Length == 0)
		{
			return string.Empty;
		}
		string tag = string.Empty;
		while (line.StartsWith('['))
		{
			int close = line.IndexOf(']');
			if (close < 0)
			{
				return string.Empty;
			}
			tag = line.Substring(1, close - 1).Trim();
			line = line.Substring(close + 1).Trim();
		}
		int eq = line.IndexOf('=');
		if (eq >= 0)
		{
			line = line.Substring(0, eq).Trim();
		}
		int space = line.IndexOf(' ');
		if (space >= 0)
		{
			line = line.Substring(0, space);
		}
		if (line.Length == 0)
		{
			return string.Empty;
		}
		if (HasFlagPrefix(line))
		{
			return line;
		}
		if (tag.Length != 0 && HasFlagPrefix(tag + line))
		{
			return tag + line;
		}
		return string.Empty;
	}

	private static bool HasFlagPrefix(string name)
	{
		foreach (string prefix in _flagPrefixes)
		{
			if (name.StartsWith(prefix, StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private void UpdateExistsColumn()
	{
		HashSet<string>? known = _knownFlags;
		if (known == null)
		{
			return;
		}
		foreach (FastFlag flag in _allFlags)
		{
			flag.Index = known.Contains(flag.Name);
		}
	}

	private async void KickKnownFlagsRefresh()
	{
		try
		{
			await EnsureKnownFlagsAsync();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("FastFlagEditor::KnownFlags", "Known flag list unavailable: " + ex.Message);
			_knownFlagsTask = null;
			return;
		}
		UpdateExistsColumn();
	}

	private static FastFlag CreateFlag(string name, string value)
	{
		bool isPreset = _presetFlagValues.Contains(name);
		return new FastFlag
		{
			Name = name,
			Value = value,
			Preset = isPreset ? _presetCheck : _presetCross
		};
	}

	private void ReloadList()
	{
		CancelPendingEdit();

		List<FastFlag> rebuilt = new List<FastFlag>(App.FastFlags.Prop.Count);
		foreach (KeyValuePair<string, object> kvp in App.FastFlags.Prop)
		{
			rebuilt.Add(CreateFlag(kvp.Key, kvp.Value?.ToString() ?? string.Empty));
		}
		rebuilt.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

		if (_view == null)
		{
			foreach (FastFlag flag in rebuilt)
			{
				_allFlags.Add(flag);
			}
			_view = (ListCollectionView)CollectionViewSource.GetDefaultView(_allFlags);
			_view.Filter = FilterFlag;
			DataGrid.ItemsSource = _view;
		}
		else
		{
			_allFlags.Clear();
			foreach (FastFlag flag in rebuilt)
			{
				_allFlags.Add(flag);
			}
			_view.Refresh();
		}

		UpdateExistsColumn();
		UpdateCounters();
		UpdateEmptyState();
	}

	private bool FilterFlag(object item)
	{
		if (item is not FastFlag flag)
		{
			return false;
		}
		if (!_showPresets && _presetFlagValues.Contains(flag.Name))
		{
			return false;
		}
		if (_searchFilter.Length != 0 && !flag.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return true;
	}

	private void RefreshView()
	{
		if (_view == null)
		{
			return;
		}
		CancelPendingEdit();
		_view.Refresh();
		UpdateCounters();
		UpdateEmptyState();
	}

	private int VisibleCount => _view?.Count ?? _allFlags.Count;

	private void InsertSorted(FastFlag flag)
	{
		int index = 0;
		while (index < _allFlags.Count && string.Compare(_allFlags[index].Name, flag.Name, StringComparison.OrdinalIgnoreCase) < 0)
		{
			index++;
		}
		_allFlags.Insert(index, flag);
	}

	private void UpdateEmptyState()
	{
		if (VisibleCount > 0)
		{
			EmptyStatePanel.Visibility = Visibility.Collapsed;
			return;
		}
		string text;
		Wpf.Ui.Common.SymbolRegular icon;
		if (App.FastFlags.Prop.Count == 0)
		{
			text = "Go to FFlag Settings to add Preset FFlags.";
			icon = Wpf.Ui.Common.SymbolRegular.Flag24;
		}
		else if (_searchFilter.Length != 0)
		{
			text = "No flags match your search.";
			icon = Wpf.Ui.Common.SymbolRegular.Search24;
		}
		else if (!_showPresets)
		{
			text = "All of your flags are presets. Enable Show Preset Flags to view them.";
			icon = Wpf.Ui.Common.SymbolRegular.Flag24;
		}
		else
		{
			text = "Go to FFlag Settings to add Preset FFlags.";
			icon = Wpf.Ui.Common.SymbolRegular.Flag24;
		}
		EmptyStateIcon.Symbol = icon;
		EmptyStateTextBlock.Text = text;
		EmptyStatePanel.Visibility = Visibility.Visible;
	}

	private void UpdateCounters()
	{
		int total = App.FastFlags.Prop.Count;
		TotalFlagsTextBlock.Text = "Flags added: " + total;
		double val = Math.Min((double)total * 0.2, 100.0);
		string text = ((val % 1.0 == 0.0) ? val.ToString("0") : val.ToString("0.##"));
		CrashRateTextBlock.Text = "Bloat: " + text + "%";
	}

	private static Dictionary<string, string> SnapshotProp()
	{
		return App.FastFlags.Prop.ToDictionary((KeyValuePair<string, object> x) => x.Key, (KeyValuePair<string, object> x) => x.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);
	}

	private Dictionary<string, string> CaptureProfileSnapshot()
	{
		DataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
		DataGrid.CommitEdit(DataGridEditingUnit.Row, true);
		Dictionary<string, string> snapshot = SnapshotProp();
		foreach (FastFlag flag in _allFlags)
		{
			if (!string.IsNullOrWhiteSpace(flag.Name))
				snapshot[flag.Name] = flag.Value ?? string.Empty;
		}
		return snapshot;
	}

	private void RecordHistory(FlagHistoryAction action, string flagName, string? oldValue, string? newValue, Dictionary<string, string>? snapshot = null)
	{
		if (snapshot != null)
		{
			int snapshotCount = 0;
			for (int i = 0; i < _flagHistory.Count; i++)
			{
				if (_flagHistory[i].Snapshot == null)
				{
					continue;
				}
				snapshotCount++;
				if (snapshotCount >= MaxSnapshotHistoryEntries)
				{
					_flagHistory.RemoveAt(i);
					i--;
				}
			}
		}
		_flagHistory.Insert(0, new FlagHistoryEntry
		{
			Action = action,
			FlagName = flagName,
			OldValue = oldValue,
			NewValue = newValue,
			Timestamp = DateTime.Now,
			Snapshot = snapshot
		});
		while (_flagHistory.Count > MaxHistoryEntries)
		{
			_flagHistory.RemoveAt(_flagHistory.Count - 1);
		}
		UpdateHistoryCount();
	}

	private void UpdateHistoryCount()
	{
		HistoryCountTextBlock.Text = ((_flagHistory.Count == 0) ? "No changes recorded" : $"{_flagHistory.Count} changes recorded");
	}

	private void UndoHistoryButton_Click(object sender, RoutedEventArgs e)
	{
		UndoEntry(HistoryListBox.SelectedItem as FlagHistoryEntry);
	}

	private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		UndoEntry(HistoryListBox.SelectedItem as FlagHistoryEntry);
	}

	private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
	{
		_flagHistory.Clear();
		UpdateHistoryCount();
	}

	private void UndoEntry(FlagHistoryEntry? entry)
	{
		if (entry == null)
		{
			return;
		}
		try
		{
			switch (entry.Action)
			{
			case FlagHistoryAction.Added:
				App.FastFlags.SetValue(entry.FlagName, null);
				break;
			case FlagHistoryAction.Edited:
				App.FastFlags.SetValue(entry.FlagName, entry.OldValue ?? string.Empty);
				break;
			case FlagHistoryAction.Renamed:
				if (!string.IsNullOrEmpty(entry.NewValue))
				{
					App.FastFlags.SetValue(entry.NewValue, null);
				}
				App.FastFlags.SetValue(entry.FlagName, entry.OldValue ?? string.Empty);
				break;
			case FlagHistoryAction.Deleted:
				App.FastFlags.SetValue(entry.FlagName, entry.OldValue ?? string.Empty);
				break;
			case FlagHistoryAction.Imported:
			case FlagHistoryAction.Cleared:
				if (entry.Snapshot == null)
				{
					ShowInfoMessage("This entry cannot be reverted.");
					return;
				}
				RestoreSnapshot(entry.Snapshot);
				break;
			default:
				ShowInfoMessage("This entry cannot be reverted.");
				return;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("FastFlagEditor::Undo", ex);
			Frontend.ShowMessageBox("That change could not be reverted: " + ex.Message, MessageBoxImage.Hand);
			return;
		}
		_flagHistory.Remove(entry);
		RecordHistory(FlagHistoryAction.Restored, entry.FlagName, entry.NewValue, entry.OldValue);
		ReloadList();
	}

	private static void RestoreSnapshot(Dictionary<string, string> snapshot)
	{
		foreach (string key in App.FastFlags.Prop.Keys.ToList())
		{
			if (!snapshot.ContainsKey(key))
			{
				App.FastFlags.SetValue(key, null);
			}
		}
		foreach (KeyValuePair<string, string> kvp in snapshot)
		{
			App.FastFlags.SetValue(kvp.Key, kvp.Value);
		}
	}

	private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_suppressSearchChanged)
		{
			return;
		}
		_searchDebounce.Stop();
		_searchDebounce.Start();
	}

	private void SearchDebounce_Tick(object? sender, EventArgs e)
	{
		_searchDebounce.Stop();
		string newSearch = SearchTextBox.Text.Trim();
		if (newSearch == _searchFilter)
		{
			return;
		}
		_searchFilter = newSearch;
		RefreshView();
		ShowSearchSuggestion(newSearch);
	}

	private void ClearSearchBox()
	{
		_searchDebounce.Stop();
		_suppressSearchChanged = true;
		SearchTextBox.Text = string.Empty;
		_suppressSearchChanged = false;
		_searchFilter = string.Empty;
		AnimateSuggestionVisibility(0.0);
	}

	private void ShowAddDialog()
	{
		while (true)
		{
			AddFastFlagDialog dialog = new AddFastFlagDialog();
			dialog.ShowDialog();
			if (dialog.Result != MessageBoxResult.OK)
			{
				return;
			}
			switch (dialog.Tabs.SelectedIndex)
			{
			case 0:
				AddSingle(dialog.FlagNameTextBox.Text.Trim(), dialog.FlagValueTextBox.Text);
				return;
			case 1:
				if (ImportJSON(dialog.JsonTextBox.Text))
				{
					return;
				}
				break;
			case 2:
			{
				string? json = AddFastFlagDialog.TryDecodeBase64(dialog.Base64TextBox.Text);
				if (json == null)
				{
					Frontend.ShowMessageBox("Invalid Base64 string!", MessageBoxImage.Hand);
					break;
				}
				if (ImportJSON(json))
				{
					return;
				}
				break;
			}
			default:
				return;
			}
		}
	}

	private void AddSingle(string name, string value)
	{
		name = name.Trim();
		if (name.Length == 0)
		{
			return;
		}
		if (App.FastFlags.GetValue(name) == null)
		{
			App.FastFlags.SetValue(name, value);
			RecordHistory(FlagHistoryAction.Added, name, null, value);
			InsertSorted(CreateFlag(name, value));
			if (!name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
			{
				ClearSearchBox();
			}
			RefreshView();
		}
		else
		{
			Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_AlreadyExists, MessageBoxImage.Asterisk);
			if (!_showPresets && _presetFlagValues.Contains(name))
			{
				TogglePresetsButton.IsChecked = true;
				_showPresets = true;
				PresetColumn.Visibility = Visibility.Visible;
			}
			if (!name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
			{
				ClearSearchBox();
			}
			RefreshView();
		}
		SelectFlag(name);
		UpdateExistsColumn();
	}

	private void SelectFlag(string name)
	{
		FastFlag? row = null;
		foreach (FastFlag flag in _allFlags)
		{
			if (string.Equals(flag.Name, name, StringComparison.Ordinal))
			{
				row = flag;
				break;
			}
		}
		if (row == null || _view == null || !_view.Contains(row))
		{
			return;
		}
		DataGrid.SelectedItem = row;
		DataGrid.ScrollIntoView(row);
	}

	private bool ImportJSON(string json)
	{
		Dictionary<string, object>? list;
		json = json.Trim();
		if (!json.StartsWith('{'))
		{
			json = "{" + json;
		}
		if (!json.EndsWith('}'))
		{
			int num = json.LastIndexOf('}');
			json = ((num != -1) ? json.Substring(0, num + 1) : (json + "}"));
		}
		try
		{
			list = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _lenientJson);
			if (list == null)
			{
				throw new Exception("JSON deserialization returned null");
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox(string.Format(Strings.Menu_FastFlagEditor_InvalidJSON, ex.Message), MessageBoxImage.Hand);
			return false;
		}
		List<string> conflicts = list.Keys.Where(App.FastFlags.Prop.ContainsKey).ToList();
		bool overwrite = false;
		if (conflicts.Count > 0)
		{
			string text = string.Format(Strings.Menu_FastFlagEditor_ConflictingImport, conflicts.Count, string.Join(", ", conflicts.Take(25)));
			if (conflicts.Count > 25)
			{
				text += "...";
			}
			overwrite = Frontend.ShowMessageBox(text, MessageBoxImage.Question, MessageBoxButton.YesNo) == MessageBoxResult.Yes;
		}
		Dictionary<string, string> snapshot = SnapshotProp();
		int applied = 0;
		foreach (KeyValuePair<string, object> item in list)
		{
			if (item.Value == null)
			{
				continue;
			}
			if (App.FastFlags.Prop.ContainsKey(item.Key) && !overwrite)
			{
				continue;
			}
			string? value = item.Value.ToString();
			if (value != null)
			{
				App.FastFlags.SetValue(item.Key, value);
				applied++;
			}
		}
		if (applied > 0)
		{
			RecordHistory(FlagHistoryAction.Imported, $"{applied} flags", null, null, snapshot);
		}
		ClearSearchBox();
		ReloadList();
		KickKnownFlagsRefresh();
		return true;
	}

	private void ShowProfilesDialog()
	{
		FlagProfilesDialog dialog = new FlagProfilesDialog(CaptureProfileSnapshot())
		{
			Owner = Window.GetWindow(this)
		};
		if (dialog.ShowDialog() != true || dialog.Result != MessageBoxResult.OK || dialog.AppliedFlags == null)
		{
			return;
		}
		try
		{
			Dictionary<string, string> snapshot = SnapshotProp();
			if (dialog.ReplaceExisting)
			{
				foreach (string key in App.FastFlags.Prop.Keys.ToList())
					App.FastFlags.SetValue(key, null);
			}
			foreach (KeyValuePair<string, string> flag in dialog.AppliedFlags)
				App.FastFlags.SetValue(flag.Key, flag.Value);
			RecordHistory(FlagHistoryAction.Imported, dialog.AppliedFlags.Count + " flags from profile '" + dialog.AppliedProfileName + "'", null, null, snapshot);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("FastFlagEditor::Profiles", ex);
			Frontend.ShowMessageBox("That profile could not be applied: " + ex.Message, MessageBoxImage.Hand);
			return;
		}
		ReloadList();
		KickKnownFlagsRefresh();
	}

	private void ShowFFlagSearchDialog()
	{
		new FFlagSearchDialog().ShowDialog();
		ReloadList();
		KickKnownFlagsRefresh();
	}

	private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
	{
		if (e.EditAction != DataGridEditAction.Commit || e.Row.DataContext is not FastFlag fastFlag || e.EditingElement is not System.Windows.Controls.TextBox textBox)
		{
			return;
		}
		string text = textBox.Text;
		if (e.Column == NameColumn)
		{
			string oldName = fastFlag.Name;
			string newName = text.Trim();
			if (newName == oldName)
			{
				return;
			}
			if (newName.Length == 0)
			{
				e.Cancel = true;
				textBox.Text = oldName;
				return;
			}
			if (App.FastFlags.GetValue(newName) != null)
			{
				Frontend.ShowMessageBox(Strings.Menu_FastFlagEditor_AlreadyExists, MessageBoxImage.Asterisk);
				e.Cancel = true;
				textBox.Text = oldName;
				return;
			}
			string currentValue = App.FastFlags.GetValue(oldName) ?? fastFlag.Value;
			App.FastFlags.SetValue(oldName, null);
			App.FastFlags.SetValue(newName, currentValue);
			RecordHistory(FlagHistoryAction.Renamed, oldName, currentValue, newName);
			Dispatcher.BeginInvoke(new Action(() => ResortRenamed(fastFlag, newName)), DispatcherPriority.Background);
		}
		else if (e.Column == ValueColumn)
		{
			string oldValue = App.FastFlags.GetValue(fastFlag.Name) ?? fastFlag.Value;
			if (oldValue == text)
			{
				return;
			}
			App.FastFlags.SetValue(fastFlag.Name, text);
			RecordHistory(FlagHistoryAction.Edited, fastFlag.Name, oldValue, text);
		}
	}

	private void ResortRenamed(FastFlag flag, string newName)
	{
		int index = _allFlags.IndexOf(flag);
		if (index < 0)
		{
			ReloadList();
			return;
		}
		_allFlags.RemoveAt(index);
		flag.Name = newName;
		flag.Preset = _presetFlagValues.Contains(newName) ? _presetCheck : _presetCross;
		HashSet<string>? known = _knownFlags;
		if (known != null)
		{
			flag.Index = known.Contains(newName);
		}
		InsertSorted(flag);
		if (!newName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
		{
			ClearSearchBox();
		}
		RefreshView();
		SelectFlag(newName);
	}

	private void BackButton_Click(object sender, RoutedEventArgs e)
	{
		if (Window.GetWindow(this) is INavigationWindow navigationWindow)
		{
			navigationWindow.Navigate(typeof(FastFlagsPage));
		}
	}

	private void AddButton_Click(object sender, RoutedEventArgs e)
	{
		ShowAddDialog();
	}

	private void FlagProfiles_Click(object sender, RoutedEventArgs e)
	{
		ShowProfilesDialog();
	}

	private void FlagFind_Click(object sender, RoutedEventArgs e)
	{
		ShowFFlagSearchDialog();
	}

	private void DeleteButton_Click(object sender, RoutedEventArgs e)
	{
		List<FastFlag> selected = DataGrid.SelectedItems.OfType<FastFlag>().ToList();
		if (selected.Count == 0)
		{
			return;
		}
		CancelPendingEdit();
		foreach (FastFlag item in selected)
		{
			RecordHistory(FlagHistoryAction.Deleted, item.Name, App.FastFlags.GetValue(item.Name), null);
			App.FastFlags.SetValue(item.Name, null);
			_allFlags.Remove(item);
		}
		UpdateCounters();
		UpdateEmptyState();
	}

	private void DeleteAllButton_Click(object sender, RoutedEventArgs e)
	{
		if (App.FastFlags.Prop.Count == 0 && _allFlags.Count == 0)
		{
			ShowInfoMessage("There are no flags to delete.");
			return;
		}
		if (Frontend.ShowMessageBox("Are you sure you want to delete all flags?", MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
		{
			return;
		}
		try
		{
			Dictionary<string, string> snapshot = SnapshotProp();
			foreach (string key in App.FastFlags.Prop.Keys.ToList())
			{
				App.FastFlags.SetValue(key, null);
			}
			RecordHistory(FlagHistoryAction.Cleared, $"{snapshot.Count} flags", null, null, snapshot);
			ReloadList();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("An error occurred while deleting flags:\n" + ex.Message, MessageBoxImage.Hand);
			App.Logger.WriteException("FastFlagEditor::DeleteAll", ex);
		}
	}

	private void ToggleButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is ToggleButton toggleButton)
		{
			_showPresets = toggleButton.IsChecked == true;
			PresetColumn.Visibility = (_showPresets ? Visibility.Visible : Visibility.Collapsed);
			RefreshView();
		}
	}

	private static void TrySetClipboard(string text)
	{
		try
		{
			Clipboard.SetText(text);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Could not access the clipboard: " + ex.Message, MessageBoxImage.Hand);
		}
	}

	private void CopyFlagsButton_Click(object sender, RoutedEventArgs e)
	{
		Dictionary<string, object> prop = App.FastFlags.Prop;
		if (prop.Count == 0)
		{
			ShowInfoMessage("There are no flags to copy.");
			return;
		}

		Voidstrap.UI.Elements.Dialogs.CopyFlagsDialog dialog = new Voidstrap.UI.Elements.Dialogs.CopyFlagsDialog(prop.Count)
		{
			Owner = Window.GetWindow(this)
		};
		if (dialog.ShowDialog() != true)
			return;

		switch (dialog.SelectedFormat)
		{
			case Voidstrap.UI.Elements.Dialogs.CopyFlagsFormat.Base64:
			{
				Dictionary<string, string> payload = prop.ToDictionary((KeyValuePair<string, object> x) => x.Key, (KeyValuePair<string, object> x) => x.Value?.ToString() ?? string.Empty);
				string json = JsonSerializer.Serialize(payload, _indentedJson);
				TrySetClipboard(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
				ShowInfoMessage($"Copied {payload.Count} flags to the clipboard as Base64.");
				break;
			}
			case Voidstrap.UI.Elements.Dialogs.CopyFlagsFormat.GroupedJson:
				TrySetClipboard(BuildGroupedJson());
				ShowInfoMessage($"Copied {prop.Count} flags to the clipboard as grouped JSON.");
				break;
			default:
				TrySetClipboard(JsonSerializer.Serialize(prop, _indentedJson));
				ShowInfoMessage($"Copied {prop.Count} flags to the clipboard as JSON.");
				break;
		}
	}

	private static string BuildGroupedJson()
	{
		Dictionary<string, object> prop = App.FastFlags.Prop;
		IOrderedEnumerable<IGrouping<string, KeyValuePair<string, object>>> groups = from g in prop.GroupBy(delegate(KeyValuePair<string, object> kvp)
			{
				Match match = _groupPrefixRegex.Match(kvp.Key);
				return (!match.Success) ? "Other" : match.Value;
			})
			orderby g.Key
			select g;
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("{");
		int total = prop.Count;
		int written = 0;
		int groupIndex = 0;
		foreach (IGrouping<string, KeyValuePair<string, object>> group in groups)
		{
			if (groupIndex > 0)
			{
				sb.AppendLine();
			}
			foreach (KeyValuePair<string, object> kvp in group.OrderByDescending((KeyValuePair<string, object> x) => x.Key.Length + (x.Value?.ToString()?.Length).GetValueOrDefault()))
			{
				written++;
				string line = "    " + JsonSerializer.Serialize(kvp.Key) + ": " + JsonSerializer.Serialize(kvp.Value?.ToString() ?? string.Empty);
				if (written != total)
				{
					line += ",";
				}
				sb.AppendLine(line);
			}
			groupIndex++;
		}
		sb.AppendLine("}");
		return sb.ToString();
	}

	private void ExportJSONButton_Click(object sender, RoutedEventArgs e)
	{
		if (App.FastFlags.Prop.Count == 0)
		{
			ShowInfoMessage("There are no flags to export.");
			return;
		}
		SaveJSONToFile(BuildGroupedJson());
	}

	private void SaveJSONToFile(string json)
	{
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Filter = "JSON files (*.json)|*.json|Text files (*.txt)|*.txt",
			Title = "Save JSON or TXT File",
			FileName = "VoidstrapExport.json"
		};
		if (saveFileDialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			string path = saveFileDialog.FileName;
			if (string.IsNullOrEmpty(Path.GetExtension(path)))
			{
				path += ".json";
			}
			File.WriteAllText(path, json);
			Frontend.ShowMessageBox("JSON file saved successfully!", MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("FastFlagEditor::Export", ex);
			Frontend.ShowMessageBox("The file could not be saved: " + ex.Message, MessageBoxImage.Hand);
		}
	}

	private void ShowInfoMessage(string message)
	{
		Frontend.ShowMessageBox(message, MessageBoxImage.Asterisk);
	}

	private void ShowSearchSuggestion(string searchFilter)
	{
		if (string.IsNullOrWhiteSpace(searchFilter))
		{
			AnimateSuggestionVisibility(0.0);
			return;
		}
		string? best = null;
		int bestStart = int.MaxValue;
		int bestIndex = int.MaxValue;
		int bestLength = int.MaxValue;
		foreach (string flag in App.FastFlags.Prop.Keys)
		{
			int index = flag.IndexOf(searchFilter, StringComparison.OrdinalIgnoreCase);
			if (index < 0)
			{
				continue;
			}
			int start = (index == 0) ? 0 : 1;
			if (start > bestStart)
			{
				continue;
			}
			if (start == bestStart)
			{
				if (index > bestIndex)
				{
					continue;
				}
				if (index == bestIndex && flag.Length >= bestLength)
				{
					continue;
				}
			}
			best = flag;
			bestStart = start;
			bestIndex = index;
			bestLength = flag.Length;
		}
		if (!string.IsNullOrEmpty(best))
		{
			SuggestionKeywordRun.Text = best;
			AnimateSuggestionVisibility(1.0);
		}
		else
		{
			AnimateSuggestionVisibility(0.0);
		}
	}

	private void SuggestionTextBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		string text = SuggestionKeywordRun.Text;
		if (!string.IsNullOrEmpty(text))
		{
			SearchTextBox.Text = text;
			SearchTextBox.CaretIndex = text.Length;
		}
	}

	private void AnimateSuggestionVisibility(double targetOpacity)
	{
		bool show = targetOpacity > 0.0;
		if (show == _suggestionVisible)
		{
			return;
		}
		_suggestionVisible = show;

		CubicEase easingFunction = new CubicEase
		{
			EasingMode = EasingMode.EaseInOut
		};
		DoubleAnimation fade = new DoubleAnimation
		{
			To = targetOpacity,
			Duration = TimeSpan.FromMilliseconds(250L),
			EasingFunction = easingFunction
		};
		DoubleAnimation slide = new DoubleAnimation
		{
			To = show ? 0 : 10,
			Duration = TimeSpan.FromMilliseconds(250L),
			EasingFunction = easingFunction
		};
		fade.Completed += delegate
		{
			if (!_suggestionVisible)
			{
				SuggestionTextBlock.Visibility = Visibility.Collapsed;
			}
		};
		if (show)
		{
			SuggestionTextBlock.Visibility = Visibility.Visible;
		}
		SuggestionTextBlock.BeginAnimation(UIElement.OpacityProperty, fade);
		SuggestionTranslateTransform.BeginAnimation(TranslateTransform.XProperty, slide);
	}
}
