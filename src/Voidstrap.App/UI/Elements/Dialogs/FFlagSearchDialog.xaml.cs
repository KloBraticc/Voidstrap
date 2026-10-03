using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;
using Voidstrap.UI.Elements.Base;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class FFlagSearchDialog : WpfUiWindow{
	private static string Text(string key) => Voidstrap.Resources.Strings.ResourceManager.GetString("FlagSearch." + key, Voidstrap.Resources.Strings.Culture) ?? key;
	private static string Format(string key, params object[] values) => string.Format(System.Globalization.CultureInfo.CurrentCulture, Text(key), values);

	private static readonly JsonSerializerOptions ExportJsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; 	

	private const int MaximumValidationFileBytes = 4 * 1024 * 1024;

	private const int MaximumFlagsPerSource = 100_000;

	private const int MaximumTotalFlags = 250_000;

	private const int MaximumValidationFlags = 50_000;

	private const int MaximumVisibleResults = 1_000;

	private const int MaximumValidationCharacters = 4_000_000;



	private readonly ObservableCollection<FlagValidationResult> _validationResults = new ObservableCollection<FlagValidationResult>();

	private CancellationTokenSource? _searchCancellation;
	private readonly CancellationToken _lifetimeToken;
	private bool _closed;
	private bool _loading;
	private int _validationGeneration;

	private readonly ObservableCollection<DataSourceInfo> _dataSources = new ObservableCollection<DataSourceInfo>();

	private Dictionary<string, object> _allFlags = new Dictionary<string, object>();

	private Dictionary<string, FlagMetadata> _flagMetadata = new Dictionary<string, FlagMetadata>();

	private static readonly HttpClient _httpClient = Voidstrap.Utility.VpnHttpClient.Create();

	private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();

	private List<FlagSearchResult> _lastSearchResults = new List<FlagSearchResult>();

	private List<FlagValidationResult> _lastValidationResults = new List<FlagValidationResult>();

	private int _searchGeneration;

	public FFlagSearchDialog()
	{
		_lifetimeToken = _lifetimeCancellation.Token;
		InitializeComponent();
		InitializeDataSources();
		SetupDataGrids();
		Loaded += OnDialogLoaded;
	}

	private async void OnDialogLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnDialogLoaded;
		await LoadDataAsync(_lifetimeToken);
	}

	private void InitializeDataSources()
	{
		DataSourceInfo[] array = new DataSourceInfo[5]
		{
			new DataSourceInfo
			{
				Name = "PCClientBootstrapper",
				Url = "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCClientBootstrapper.json",
				Status = Text("Pending")
			},
			new DataSourceInfo
			{
				Name = "PCStudioApp",
				Url = "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCStudioApp.json",
				Status = Text("Pending")
			},
			new DataSourceInfo
			{
				Name = "PCDesktopClient",
				Url = "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCDesktopClient.json",
				Status = Text("Pending")
			},
			new DataSourceInfo
			{
				Name = "FVariables.txt",
				Url = "https://raw.githubusercontent.com/MaximumADHD/Roblox-Client-Tracker/refs/heads/roblox/FVariables.txt",
				Status = Text("Pending")
			},
			new DataSourceInfo
			{
				Name = "Roblox ClientSettings",
				Url = "https://clientsettings.roblox.com/v2/settings/application/PCDesktopClient",
				Status = Text("Pending")
			}
		};
		foreach (DataSourceInfo item in array)
		{
			_dataSources.Add(item);
		}
	}

	private void SetupDataGrids()
	{
		SearchResultsDataGrid.ItemsSource = Array.Empty<FlagSearchResult>();
		ValidationResultsDataGrid.ItemsSource = _validationResults;
		SourcesDataGrid.ItemsSource = _dataSources;
	}

	private async Task LoadDataAsync(CancellationToken token)
	{
		if (_loading || _closed) return;
		_loading = true;
		RefreshSourcesButton.IsEnabled = false;
		ValidateButton.IsEnabled = false;
		await UpdateStatusAsync(Text("Loading"));
		ShowProgress(true);
		try
		{
			var allFlags = new Dictionary<string, object>(StringComparer.Ordinal);
			var metadata = new Dictionary<string, FlagMetadata>(StringComparer.Ordinal);
			var ordered = _dataSources.OrderBy(source => source.Name == "Roblox ClientSettings" ? 0 : source.Name == "PCDesktopClient" ? 1 : source.Name == "FVariables.txt" ? 4 : 2).ToArray();
			var fetched = await Task.WhenAll(ordered.Select(source => FetchSourceDataAsync(source, token)));
			int successes = 0;
			for (int index = 0; index < ordered.Length; index++)
			{
				token.ThrowIfCancellationRequested();
				var flags = fetched[index];
				if (flags == null) continue;
				successes++;
				foreach (var flag in flags)
				{
					if (allFlags.Count >= MaximumTotalFlags) break;
					if (allFlags.TryAdd(flag.Key, flag.Value)) metadata[flag.Key] = new FlagMetadata { Source = ordered[index].Name };
				}
			}
			if (_closed) return;
			_allFlags = allFlags;
			_flagMetadata = metadata;
			StatusText.Text = Format("LoadedSummary", allFlags.Count, successes, _dataSources.Count);
			QueueSearch();
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { }
		catch (Exception ex)
		{
			if (!_closed) StatusText.Text = Text("LoadError");
			App.Logger.WriteException("FFlagSearch", ex);
		}
		finally
		{
			_loading = false;
			if (!_closed)
			{
				RefreshSourcesButton.IsEnabled = true;
				ValidateButton.IsEnabled = _allFlags.Count > 0;
				ShowProgress(false);
			}
		}
	}

	private async Task<Dictionary<string, object>?> FetchSourceDataAsync(DataSourceInfo source, CancellationToken token)
	{
		source.Status = Text("Loading");
		source.FlagCount = 0;
		try
		{
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
			deadline.CancelAfter(TimeSpan.FromSeconds(15));
			var flags = await FetchFlagsFromSourceAsync(source.Url, source.Name, deadline.Token);
			if (_closed) return null;
			source.Status = Text("Loaded");
			source.FlagCount = flags.Count;
			source.LastUpdated = DateTime.Now.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
			return flags;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
		catch (Exception ex)
		{
			if (!_closed)
			{
				source.Status = Text("Unavailable") + ": " + ex.Message;
				source.LastUpdated = string.Empty;
			}
			App.Logger.WriteException("FFlagSearch", ex);
			return null;
		}
	}

	private static async Task<Dictionary<string, object>> FetchFlagsFromSourceAsync(string url, string sourceName, CancellationToken token)
	{
		string response = await Voidstrap.Utility.Http.GetStringBoundedAsync(_httpClient, url, token: token).ConfigureAwait(false);
		return await Task.Run(() => ParseSource(response, url.EndsWith(".txt", StringComparison.OrdinalIgnoreCase), token), token).ConfigureAwait(false);
	}

	private static Dictionary<string, object> ParseSource(string response, bool namesOnly, CancellationToken token)
	{
		var flags = new Dictionary<string, object>(StringComparer.Ordinal);
		if (namesOnly)
		{
			foreach (string line in response.Split('\n'))
			{
				token.ThrowIfCancellationRequested();
				string name = line.Trim();
				if (name.StartsWith("[", StringComparison.Ordinal) && name.IndexOf(']') is int end && end >= 0) name = name[(end + 1)..].Trim();
				if (IsFlagName(name)) flags.TryAdd(name, string.Empty);
				if (flags.Count >= MaximumFlagsPerSource) break;
			}
		}
		else
		{
			using var document = JsonDocument.Parse(response);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) throw new JsonException(Text("ObjectRequired"));
			if (root.TryGetProperty("applicationSettings", out JsonElement settings)) root = settings;
			if (root.ValueKind != JsonValueKind.Object) throw new JsonException(Text("ObjectRequired"));
			foreach (JsonProperty flag in root.EnumerateObject())
			{
				token.ThrowIfCancellationRequested();
				if (IsFlagName(flag.Name) && flag.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null))
					flags[flag.Name] = flag.Value.ValueKind == JsonValueKind.String ? flag.Value.GetString() ?? string.Empty : flag.Value.GetRawText();
				if (flags.Count >= MaximumFlagsPerSource) break;
			}
		}
		if (flags.Count == 0) throw new InvalidDataException(Text("EmptySource"));
		return flags;
	}

	private static bool IsFlagName(string name) => name.Length is > 4 and <= 512
		&& name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
		&& (name.StartsWith("FFlag", StringComparison.Ordinal) || name.StartsWith("DFFlag", StringComparison.Ordinal)
		|| name.StartsWith("SFFlag", StringComparison.Ordinal) || name.StartsWith("FInt", StringComparison.Ordinal)
		|| name.StartsWith("DFInt", StringComparison.Ordinal) || name.StartsWith("SFInt", StringComparison.Ordinal)
		|| name.StartsWith("FString", StringComparison.Ordinal) || name.StartsWith("DFString", StringComparison.Ordinal)
		|| name.StartsWith("SFString", StringComparison.Ordinal) || name.StartsWith("FLog", StringComparison.Ordinal)
		|| name.StartsWith("DFLog", StringComparison.Ordinal) || name.StartsWith("SFLog", StringComparison.Ordinal));

	private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => QueueSearch();

	private void SearchFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => QueueSearch();

	private async void QueueSearch()
	{
		if (_closed || SearchResultsDataGrid == null || ValueFilter == null) return;
		_searchCancellation?.Cancel();
		_searchCancellation?.Dispose();
		_searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
		CancellationToken token = _searchCancellation.Token;
		int generation = ++_searchGeneration;
		string term = SearchTextBox.Text.Trim();
		int filter = ValueFilter.SelectedIndex;
		var flags = _allFlags;
		var metadata = _flagMetadata;
		try
		{
			await Task.Delay(200, token);
			var results = await Task.Run(() =>
			{
				var matches = new List<FlagSearchResult>();
				foreach (var flag in flags)
				{
					token.ThrowIfCancellationRequested();
					if (!flag.Key.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
					if (filter == 1 && !IsTrueValue(flag.Value) || filter == 2 && !IsFalseValue(flag.Value)) continue;
					matches.Add(new FlagSearchResult { Name = flag.Key, Value = flag.Value.ToString() ?? string.Empty,
						Source = metadata.TryGetValue(flag.Key, out var info) ? info.Source : string.Empty });
				}
				return matches.OrderBy(r => !r.Name.Equals(term, StringComparison.OrdinalIgnoreCase))
					.ThenBy(r => !r.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
			}, token);
			await Dispatcher.InvokeAsync(() =>
			{
				if (_closed || token.IsCancellationRequested || generation != _searchGeneration) return;
				_lastSearchResults = results;
				SearchResultsDataGrid.ItemsSource = results.Take(MaximumVisibleResults).ToArray();
				SearchResultsCount.Text = Format("ResultCount", Math.Min(results.Count, MaximumVisibleResults), results.Count);
				SearchEmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
				ExportSearchResultsButton.IsEnabled = results.Any(r => r.Source != "FVariables.txt");
			});
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { }
		catch (Exception ex)
		{
			await Dispatcher.InvokeAsync(() => { if (!_closed) StatusText.Text = Text("SearchError"); });
			App.Logger.WriteException("FFlagSearch", ex);
		}
	}

	private async void ValidateButton_Click(object sender, RoutedEventArgs e)
	{
		string? text = ValidationInputTextBox.Text?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			System.Windows.MessageBox.Show(Text("Message1"), Text("Message2"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (text.Length > MaximumValidationCharacters)
		{
			System.Windows.MessageBox.Show(Text("Message3"), Text("Message4"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else
		{
			await ValidateFlagsAsync(text);
		}
	}

	private async Task ValidateFlagsAsync(string input)
	{
		int generation = ++_validationGeneration;
		ValidateButton.IsEnabled = false;
		await UpdateStatusAsync(Text("Validating"));
		ShowProgress(show: true);
		try
		{
			CancellationToken token = _lifetimeToken;
			(Dictionary<string, object> dictionary, HashSet<string> duplicates) = await Task.Run(() => ParseValidationInput(input, token), token);
			if (_closed || generation != _validationGeneration) return;
			if (duplicates.Count > 0)
			{
				System.Windows.MessageBox.Show(Text("Message5") + string.Join(", ", duplicates.Take(25)), Text("Message6"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
			Dictionary<string, object> knownFlags = _allFlags;
			List<FlagValidationResult> list = await Task.Run(() =>
			{
				List<FlagValidationResult> results = new List<FlagValidationResult>(dictionary.Count);
				foreach (KeyValuePair<string, object> item in dictionary)
				{
					token.ThrowIfCancellationRequested();
					FlagValidationResult result = new FlagValidationResult
					{
						Name = item.Key,
						InputValue = item.Value?.ToString() ?? string.Empty
					};
					if (knownFlags.TryGetValue(item.Key, out object? value))
					{
						result.CanExport = IsValidInput(item.Key, result.InputValue);
						result.Status = Text(result.CanExport ? "Found" : "ValueError");
						result.ValidValue = value?.ToString() ?? string.Empty;
						result.Notes = Text(result.CanExport ? "FoundNote" : "ValueNote");
					}
					else
					{
						result.Status = Text("NotFound");
						result.ValidValue = string.Empty;
						result.Notes = Text("NotFoundNote");
					}
					results.Add(result);
				}
				return results;
			}, token);
			if (_closed || generation != _validationGeneration) return;
			_lastValidationResults = list;
			ValidationResultsDataGrid.ItemsSource = list.Take(MaximumVisibleResults).ToArray();
			ValidationResultsCount.Text = Format("ResultCount", Math.Min(list.Count, MaximumVisibleResults), list.Count);
			ExportValidResultsButton.IsEnabled = list.Any(r => r.CanExport);
			await UpdateStatusAsync(Format("ValidationSummary", list.Count, list.Count(r => r.CanExport)));
		}
		catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
		{
		}
		catch (Exception ex2)
		{
			await UpdateStatusAsync(Text("Message7"));
			if (!_closed) System.Windows.MessageBox.Show(Text("Message8") + ex2.Message, Text("Message9"), MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			if (!_closed) { ShowProgress(false); ValidateButton.IsEnabled = !_loading && _allFlags.Count > 0; }
		}
	}

	private static (Dictionary<string, object> Flags, HashSet<string> Duplicates) ParseValidationInput(string input, CancellationToken token)
	{
		var flags = new Dictionary<string, object>(StringComparer.Ordinal);
		var duplicates = new HashSet<string>(StringComparer.Ordinal);
		void Add(string name, object value)
		{
			token.ThrowIfCancellationRequested();
			name = name.Trim();
			if (!IsFlagName(name)) throw new InvalidDataException(Text("NameError"));
			if (flags.Count >= MaximumValidationFlags) throw new InvalidDataException(Text("TooMany"));
			if (!flags.TryAdd(name, value)) { duplicates.Add(name); flags[name] = value; }
		}
		if (input.TrimStart().StartsWith("{", StringComparison.Ordinal) || input.TrimStart().StartsWith("[", StringComparison.Ordinal))
		{
			using var document = JsonDocument.Parse(input);
			if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(Text("ObjectRequired"));
			foreach (var flag in document.RootElement.EnumerateObject())
			{
				if (flag.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null) throw new JsonException(Text("ScalarRequired"));
				Add(flag.Name, flag.Value.ValueKind == JsonValueKind.String ? flag.Value.GetString() ?? string.Empty : flag.Value.GetRawText());
			}
		}
		else
		{
			foreach (string line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				if (string.IsNullOrWhiteSpace(line)) continue;
				string[] pair = line.Split('=', 2);
				if (pair.Length != 2) throw new InvalidDataException(Text("LineError"));
				Add(pair[0], pair[1].Trim());
			}
		}
		if (flags.Count == 0) throw new InvalidDataException(Text("EmptyInput"));
		return (flags, duplicates);
	}

	private static bool IsValidInput(string name, string value)
	{
		if (name.StartsWith("D", StringComparison.Ordinal) || name.StartsWith("S", StringComparison.Ordinal)) name = name[1..];
		if (name.StartsWith("FFlag", StringComparison.Ordinal)) return bool.TryParse(value, out _);
		if (name.StartsWith("FInt", StringComparison.Ordinal) || name.StartsWith("FLog", StringComparison.Ordinal))
			return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);
		return true;
	}

	private async void RefreshSourcesButton_Click(object sender, RoutedEventArgs e) => await LoadDataAsync(_lifetimeToken);

	private async void LoadFileButton_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "JSON files (*.json)|*.json|Text files (*.txt)|*.txt|All files (*.*)|*.*",
			Title = Text("Message10")
		};
		if (openFileDialog.ShowDialog() == true)
		{
			try
			{
				string text = await ReadValidationFileAsync(openFileDialog.FileName, _lifetimeToken);
				ValidationInputTextBox.Text = text;
			}
			catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show(Text("Message11") + ex.Message, Text("Message12"), MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private static async Task<string> ReadValidationFileAsync(string path, CancellationToken token)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaximumValidationFileBytes)
			throw new InvalidDataException(Text("Message13"));
		byte[] data = new byte[checked((int)stream.Length)];
		int offset = 0;
		while (offset < data.Length)
		{
			int read = await stream.ReadAsync(data.AsMemory(offset), token);
			if (read == 0)
				throw new EndOfStreamException();
			offset += read;
		}
		if (await stream.ReadAsync(new byte[1], token) != 0)
			throw new InvalidDataException(Text("Message14"));
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, System.Text.Encoding.UTF8, true);
		string text = await reader.ReadToEndAsync(token);
		if (text.Length > MaximumValidationCharacters)
			throw new InvalidDataException(Text("Message15"));
		return text;
	}

	private void ClearValidationButton_Click(object sender, RoutedEventArgs e)
	{
		++_validationGeneration;
		ValidationInputTextBox.Clear();
		ValidationResultsDataGrid.ItemsSource = Array.Empty<FlagValidationResult>();
		_validationResults.Clear();
		_lastValidationResults.Clear();
		UpdateValidationResultsCount();
		ExportValidResultsButton.IsEnabled = false;
	}

	private async void ExportSearchResultsButton_Click(object sender, RoutedEventArgs e)
	{
		await ExportFlagsAsync(_lastSearchResults.Where(r => r.Source != "FVariables.txt").ToDictionary(r => r.Name, r => ParseFlagValue(r.Name, r.Value)), "search_results");
	}

	private async void ExportValidResultsButton_Click(object sender, RoutedEventArgs e)
	{
		Dictionary<string, object> flags = _lastValidationResults.Where(r => r.CanExport).ToDictionary(r => r.Name, r => ParseFlagValue(r.Name, r.InputValue));
		await ExportFlagsAsync(flags, "valid_flags");
	}



	private static async Task ExportFlagsAsync(Dictionary<string, object> flags, string defaultName)
	{
		SaveFileDialog dialog = new SaveFileDialog
		{
			Filter = "JSON files (*.json)|*.json",
			FileName = $"{defaultName}_{DateTime.Now:yyyyMMdd_HHmmss}.json"
		};
		if (dialog.ShowDialog() == true)
		{
			try
			{
				string contents = await Task.Run(() => JsonSerializer.Serialize(flags, ExportJsonOptions));
				await File.WriteAllTextAsync(dialog.FileName, contents);
				System.Windows.MessageBox.Show(Format("ExportSummary", flags.Count, dialog.FileName), Text("Message17"), MessageBoxButton.OK, MessageBoxImage.Asterisk);
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show(Text("Message18") + ex.Message, Text("Message19"), MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private async Task UpdateStatusAsync(string status)
	{
		if (_closed) return;
		await ((DispatcherObject)this).Dispatcher.InvokeAsync<string>((Func<string>)(() => StatusText.Text = status));
	}

	private void ShowProgress(bool show)
	{
		if (_closed) return;
		((DispatcherObject)this).Dispatcher.Invoke<Visibility>((Func<Visibility>)(() => LoadingProgress.Visibility = ((!show) ? Visibility.Collapsed : Visibility.Visible)));
	}

	private void UpdateSearchResultsCount()
	{
		SearchResultsCount.Text = Format("ResultCount", 0, 0);
	}

	private void UpdateValidationResultsCount()
	{
		ValidationResultsCount.Text = Format("ResultCount", 0, 0);
	}



	private static bool IsTrueValue(object? value) => bool.TryParse(value?.ToString(), out bool result) && result;
	private static bool IsFalseValue(object? value) => bool.TryParse(value?.ToString(), out bool result) && !result;
	private static object ParseFlagValue(string name, string value) => (name.StartsWith("FString", StringComparison.Ordinal) || name.StartsWith("DFString", StringComparison.Ordinal) || name.StartsWith("SFString", StringComparison.Ordinal)) ? value : ParseValue(value);

	private static object ParseValue(string value)
	{
		if (bool.TryParse(value, out var result))
		{
			return result;
		}
		if (int.TryParse(value, out var result2))
		{
			return result2;
		}
		if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result3))
		{
			return result3;
		}
		return value;
	}

	protected override void OnClosed(EventArgs e)
	{
		_closed = true;
		Loaded -= OnDialogLoaded;
		++_searchGeneration;
		++_validationGeneration;
		_searchCancellation?.Cancel();
		_searchCancellation?.Dispose();
		_searchCancellation = null;
		_lifetimeCancellation.Cancel();
		_lifetimeCancellation.Dispose();
		base.OnClosed(e);
	}



	private void PasteMenuItem_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (Voidstrap.Utility.ClipboardService.ContainsText())
			{
				string text = Voidstrap.Utility.ClipboardService.GetText();
				ValidationInputTextBox.Focus();
				RoutedUICommand paste = ApplicationCommands.Paste;
				if (paste.CanExecute(null, ValidationInputTextBox))
				{
					paste.Execute(null, ValidationInputTextBox);
				}
				else
				{
					ValidationInputTextBox.Text = text;
				}
			}
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show(Text("Message20") + ex.Message, Text("Message21"), MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void ClearMenuItem_Click(object sender, RoutedEventArgs e)
	{
		ValidationInputTextBox.Clear();
	}

	private void SelectAllMenuItem_Click(object sender, RoutedEventArgs e)
	{
		ValidationInputTextBox.SelectAll();
	}

	private void SampleFormatButton_Click(object sender, RoutedEventArgs e)
	{
		string text = "{\n  \"FFlagDebugDisplayFPS\": \"True\",\n  \"DFIntTaskSchedulerTargetFps\": \"120\",\n  \"FFlagDisablePostFx\": \"False\",\n  \"DFIntRenderClampRoughnessMax\": \"-640000000\"\n}";
		ValidationInputTextBox.Text = text;
	}
}
