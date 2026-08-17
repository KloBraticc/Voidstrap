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
	private const int MaximumValidationFileBytes = 4 * 1024 * 1024;

	private const int MaximumFlagsPerSource = 100_000;

	private const int MaximumTotalFlags = 250_000;

	private const int MaximumValidationFlags = 50_000;

	private const int MaximumVisibleResults = 1_000;

	private const int MaximumValidationCharacters = 4_000_000;

	private readonly ObservableCollection<FlagSearchResult> _searchResults = new ObservableCollection<FlagSearchResult>();

	private readonly ObservableCollection<FlagValidationResult> _validationResults = new ObservableCollection<FlagValidationResult>();

	private readonly ObservableCollection<FlagSearchResult> _recentFlags = new ObservableCollection<FlagSearchResult>();

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
		InitializeComponent();
		InitializeDataSources();
		SetupDataGrids();
		_ = LoadDataAsync(_lifetimeCancellation.Token);
	}

	private void InitializeDataSources()
	{
		DataSourceInfo[] array = new DataSourceInfo[5]
		{
			new DataSourceInfo
			{
				Name = "PCClientBootstrapper",
				Url = "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCClientBootstrapper.json",
				Status = "Pending"
			},
			new DataSourceInfo
			{
				Name = "PCStudioApp",
				Url = "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCStudioApp.json",
				Status = "Pending"
			},
			new DataSourceInfo
			{
				Name = "PCDesktopClient",
				Url = "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCDesktopClient.json",
				Status = "Pending"
			},
			new DataSourceInfo
			{
				Name = "FVariables.txt",
				Url = "https://raw.githubusercontent.com/MaximumADHD/Roblox-Client-Tracker/refs/heads/roblox/FVariables.txt",
				Status = "Pending"
			},
			new DataSourceInfo
			{
				Name = "Roblox ClientSettings",
				Url = "https://clientsettings.roblox.com/v2/settings/application/PCDesktopClient",
				Status = "Pending"
			}
		};
		foreach (DataSourceInfo item in array)
		{
			_dataSources.Add(item);
		}
	}

	private void SetupDataGrids()
	{
		SearchResultsDataGrid.ItemsSource = _searchResults;
		ValidationResultsDataGrid.ItemsSource = _validationResults;
		RecentFlagsDataGrid.ItemsSource = _recentFlags;
	}

	private async Task LoadDataAsync(CancellationToken token)
	{
		await UpdateStatusAsync("Loading flags...");
		ShowProgress(show: true);
		try
		{
			Dictionary<string, object> allFlags = new Dictionary<string, object>();
			Dictionary<string, FlagMetadata> flagMetadata = new Dictionary<string, FlagMetadata>();
			foreach (DataSourceInfo source in _dataSources)
			{
				token.ThrowIfCancellationRequested();
				try
				{
					source.Status = "Loading...";
					Dictionary<string, object> dictionary = await FetchFlagsFromSourceAsync(source.Url, source.Name, token);
					foreach (KeyValuePair<string, object> item in dictionary)
					{
						if (allFlags.Count >= MaximumTotalFlags)
							break;
						if (item.Key.Length is > 0 and <= 512 && !allFlags.ContainsKey(item.Key))
						{
							allFlags[item.Key] = item.Value;
							flagMetadata[item.Key] = new FlagMetadata
							{
								Source = source.Name,
								DateAdded = DateTime.Now
							};
						}
					}
					source.Status = "✓ Success";
					source.FlagCount = dictionary.Count;
					source.LastUpdated = DateTime.Now.ToString("HH:mm:ss");
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					source.Status = "❌ Error";
					App.Logger.WriteException("FFlagSearch", ex);
				}
			}
			_allFlags = allFlags;
			_flagMetadata = flagMetadata;
			await UpdateStatusAsync($"Done: {allFlags.Count} flags loaded!");
			UpdateTotalFlagsCount();
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex2)
		{
			await UpdateStatusAsync("Error loading flag data");
			App.Logger.WriteException("FFlagSearch", ex2);
		}
		finally
		{
			ShowProgress(show: false);
		}
	}

	private async Task<Dictionary<string, object>> FetchFlagsFromSourceAsync(string url, string sourceName, CancellationToken token)
	{
		Dictionary<string, object> flags = new Dictionary<string, object>();
		string response = string.Empty;
		try
		{
			response = await Voidstrap.Utility.Http.GetStringBoundedAsync(_httpClient, url, token);
			if (url.EndsWith(".json") || url.Contains("clientsettings.roblox.com"))
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(response);
				if (jsonDocument.RootElement.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty item in jsonDocument.RootElement.EnumerateObject())
					{
						if (flags.Count >= MaximumFlagsPerSource)
							break;
						Dictionary<string, object> dictionary = flags;
						string name = item.Name;
						dictionary[name] = item.Value.ValueKind switch
						{
							JsonValueKind.String => item.Value.GetString() ?? "", 
							JsonValueKind.Number => item.Value.TryGetInt32(out var value) ? ((double)value) : item.Value.GetDouble(), 
							JsonValueKind.True => true, 
							JsonValueKind.False => false, 
							_ => item.Value.GetRawText(), 
						};
					}
				}
			}
			else if (url.EndsWith(".txt"))
			{
				string[] array = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
				for (int i = 0; i < array.Length; i++)
				{
					if (flags.Count >= MaximumFlagsPerSource)
						break;
					string[] array2 = array[i].Split('=', 2);
					if (array2.Length == 2)
					{
						string key = array2[0].Trim();
						string text = array2[1].Trim();
						int result2;
						double result3;
						if (bool.TryParse(text, out var result))
						{
							flags[key] = result;
						}
						else if (int.TryParse(text, out result2))
						{
							flags[key] = result2;
						}
						else if (double.TryParse(text, out result3))
						{
							flags[key] = result3;
						}
						else
						{
							flags[key] = text;
						}
					}
				}
			}
			else
			{
				using JsonDocument document = JsonDocument.Parse(response);
				foreach (JsonProperty item2 in document.RootElement.EnumerateObject())
				{
					if (flags.Count >= MaximumFlagsPerSource)
						break;
					Dictionary<string, object> dictionary = flags;
					string name = item2.Name;
					dictionary[name] = item2.Value.ValueKind switch
					{
						JsonValueKind.String => item2.Value.GetString() ?? "", 
						JsonValueKind.Number => item2.Value.TryGetInt32(out var value2) ? ((double)value2) : item2.Value.GetDouble(), 
						JsonValueKind.True => true, 
						JsonValueKind.False => false, 
						_ => item2.Value.GetRawText(), 
					};
				}
			}
		}
		catch (JsonException)
		{
			if (!string.IsNullOrEmpty(response))
			{
				string[] array = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
				for (int i = 0; i < array.Length; i++)
				{
					if (flags.Count >= MaximumFlagsPerSource)
						break;
					string[] array3 = array[i].Split('=', 2);
					if (array3.Length == 2)
					{
						flags[array3[0].Trim()] = array3[1].Trim();
					}
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (HttpRequestException ex2)
		{
			App.Logger.WriteLine("FFlagSearch", "Failed to fetch from " + sourceName + ": " + ex2.Message);
			throw;
		}
		return flags;
	}

	private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		int generation = Interlocked.Increment(ref _searchGeneration);
		string searchTerm = SearchTextBox.Text?.Trim();
		if (string.IsNullOrEmpty(searchTerm))
		{
			_searchResults.Clear();
			_lastSearchResults.Clear();
			ExportSearchResultsButton.IsEnabled = false;
			UpdateSearchResultsCount();
			return;
		}
		try
		{
			await Task.Delay(300, _lifetimeCancellation.Token);
			if (SearchTextBox.Text?.Trim() == searchTerm)
				await PerformSearchAsync(searchTerm, generation, _lifetimeCancellation.Token);
		}
		catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
		{
		}
	}

	private async Task PerformSearchAsync(string searchTerm, int generation, CancellationToken token)
	{
		bool trueFlagsOnly = TrueFlagsOnlyCheckBox.IsChecked == true;
		bool falseFlagsOnly = FalseFlagsOnlyCheckBox.IsChecked == true;
		Dictionary<string, object> flags = _allFlags;
		Dictionary<string, FlagMetadata> metadata = _flagMetadata;
		List<FlagSearchResult> results;
		try
		{
			results = await Task.Run(delegate
			{
				List<FlagSearchResult> matches = new List<FlagSearchResult>();
				int scanned = 0;
				foreach (KeyValuePair<string, object> allFlag in flags)
				{
					if ((scanned++ & 1023) == 0)
						token.ThrowIfCancellationRequested();
					if (allFlag.Key.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
					{
						FlagMetadata value;
						FlagMetadata flagMetadata = (metadata.TryGetValue(allFlag.Key, out value) ? value : new FlagMetadata());
						if ((!trueFlagsOnly || IsTrueValue(allFlag.Value)) && (!falseFlagsOnly || IsFalseValue(allFlag.Value)))
						{
							matches.Add(new FlagSearchResult
							{
								Name = allFlag.Key,
								Value = (allFlag.Value?.ToString() ?? "null"),
								Source = (flagMetadata.Source ?? "Unknown")
							});
						}
					}
				}
				return matches;
			}, token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			return;
		}
		if (token.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration))
			return;
		_lastSearchResults = results;
		_searchResults.Clear();
		foreach (FlagSearchResult item in results.Take(1000))
		{
			_searchResults.Add(item);
		}
		UpdateSearchResultsCount();
		ExportSearchResultsButton.IsEnabled = results.Count > 0;
		if (results.Count > 1000)
		{
			StatusText.Text = $"Showing first 1000 of {results.Count} results. Use export to get all results.";
		}
	}

	private async void ValidateButton_Click(object sender, RoutedEventArgs e)
	{
		string text = ValidationInputTextBox.Text?.Trim();
		if (string.IsNullOrEmpty(text))
		{
			System.Windows.MessageBox.Show("Please enter flags to validate.", "No Input", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (text.Length > MaximumValidationCharacters)
		{
			System.Windows.MessageBox.Show("That flag input is too large to validate.", "Input Too Large", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else
		{
			await ValidateFlagsAsync(text);
		}
	}

	private async Task ValidateFlagsAsync(string input)
	{
		await UpdateStatusAsync("Validating flags...");
		ShowProgress(show: true);
		try
		{
			CancellationToken token = _lifetimeCancellation.Token;
			(Dictionary<string, object> dictionary, HashSet<string> duplicates) = await Task.Run(() => ParseValidationInput(input, token), token);
			if (duplicates.Count > 0)
			{
				System.Windows.MessageBox.Show("Duplicate flags found in input: " + string.Join(", ", duplicates.Take(25)), "Duplicates Detected", MessageBoxButton.OK, MessageBoxImage.Exclamation);
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
						InputValue = item.Value?.ToString() ?? "null"
					};
					if (knownFlags.TryGetValue(item.Key, out object value))
					{
						result.Status = "✓ Valid";
						result.ValidValue = value?.ToString() ?? "null";
						result.Notes = "Flag exists in database";
					}
					else
					{
						result.Status = "❌ Invalid";
						result.ValidValue = "N/A";
						result.Notes = "Flag not found in any data source";
					}
					results.Add(result);
				}
				return results;
			}, token);
			_lastValidationResults = list;
			_validationResults.Clear();
			foreach (FlagValidationResult item in list.Take(MaximumVisibleResults))
			{
				_validationResults.Add(item);
			}
			ValidationResultsCount.Text = $"{list.Count} results";
			ExportValidResultsButton.IsEnabled = list.Any((FlagValidationResult r) => r.Status == "✓ Valid");
			await UpdateStatusAsync($"Validated {list.Count} flags. {list.Count((FlagValidationResult r) => r.Status == "✓ Valid")} valid, {list.Count((FlagValidationResult r) => r.Status == "❌ Invalid")} invalid.");
		}
		catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
		{
		}
		catch (Exception ex2)
		{
			await UpdateStatusAsync("Error validating flags");
			System.Windows.MessageBox.Show("Error validating flags: " + ex2.Message, "Validation Error", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			ShowProgress(show: false);
		}
	}

	private static (Dictionary<string, object> Flags, HashSet<string> Duplicates) ParseValidationInput(string input, CancellationToken token)
	{
		Dictionary<string, object> flags = new Dictionary<string, object>();
		HashSet<string> duplicates = new HashSet<string>();
		try
		{
			using JsonDocument document = JsonDocument.Parse(input);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
				throw new JsonException("Flag input must be an object");
			foreach (JsonProperty item in document.RootElement.EnumerateObject())
			{
				token.ThrowIfCancellationRequested();
				if (flags.Count >= MaximumValidationFlags)
					throw new InvalidDataException("The flag input contains too many values");
				string name = item.Name.Trim();
				if (name.Length is 0 or > 512)
					continue;
				object value = item.Value.ValueKind switch
				{
					JsonValueKind.String => item.Value.GetString() ?? "",
					JsonValueKind.Number => item.Value.TryGetInt32(out int number) ? number : item.Value.GetDouble(),
					JsonValueKind.True => true,
					JsonValueKind.False => false,
					_ => item.Value.GetRawText()
				};
				if (!flags.TryAdd(name, value))
				{
					duplicates.Add(name);
					flags[name] = value;
				}
			}
		}
		catch (JsonException)
		{
			foreach (string line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				token.ThrowIfCancellationRequested();
				if (flags.Count >= MaximumValidationFlags)
					throw new InvalidDataException("The flag input contains too many values");
				string[] pair = line.Split('=', 2);
				if (pair.Length != 2)
					continue;
				string name = pair[0].Trim();
				if (name.Length is 0 or > 512)
					continue;
				if (!flags.TryAdd(name, pair[1].Trim()))
				{
					duplicates.Add(name);
					flags[name] = pair[1].Trim();
				}
			}
		}
		return (flags, duplicates);
	}

	private async void FetchRecentButton_Click(object sender, RoutedEventArgs e)
	{
		await UpdateStatusAsync("Fetching recent flags...");
		ShowProgress(show: true);
		try
		{
			List<FlagSearchResult> list = (from flag in _allFlags.Take(100)
				select new FlagSearchResult
				{
					Name = flag.Key,
					Value = (flag.Value?.ToString() ?? "null"),
					Source = (_flagMetadata.TryGetValue(flag.Key, out FlagMetadata value) ? value.Source : "Unknown"),
					DateAdded = DateTime.Now.AddHours(-Random.Shared.Next(0, 24)).ToString("yyyy-MM-dd HH:mm")
				}).ToList();
			_recentFlags.Clear();
			foreach (FlagSearchResult item in list)
			{
				_recentFlags.Add(item);
			}
			UpdateRecentFlagsCount();
			DownloadAllRecentButton.IsEnabled = list.Any();
			DownloadTrueRecentButton.IsEnabled = list.Any();
			DownloadFalseRecentButton.IsEnabled = list.Any();
			await UpdateStatusAsync($"Found {list.Count} recent flags");
		}
		catch (Exception ex)
		{
			await UpdateStatusAsync("Error fetching recent flags");
			App.Logger.WriteException("FFlagSearch", ex);
		}
		finally
		{
			ShowProgress(show: false);
		}
	}

	private async void LoadFileButton_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "JSON files (*.json)|*.json|Text files (*.txt)|*.txt|All files (*.*)|*.*",
			Title = "Select flag file to validate"
		};
		if (openFileDialog.ShowDialog() == true)
		{
			try
			{
				string text = await ReadValidationFileAsync(openFileDialog.FileName, _lifetimeCancellation.Token);
				ValidationInputTextBox.Text = text;
			}
			catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show("Error loading file: " + ex.Message, "File Error", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private static async Task<string> ReadValidationFileAsync(string path, CancellationToken token)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaximumValidationFileBytes)
			throw new InvalidDataException("The flag file size is invalid");
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
			throw new InvalidDataException("The flag file changed while it was being read");
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, System.Text.Encoding.UTF8, true);
		string text = await reader.ReadToEndAsync(token);
		if (text.Length > MaximumValidationCharacters)
			throw new InvalidDataException("The flag input is too large");
		return text;
	}

	private void ClearValidationButton_Click(object sender, RoutedEventArgs e)
	{
		ValidationInputTextBox.Clear();
		_validationResults.Clear();
		_lastValidationResults.Clear();
		UpdateValidationResultsCount();
		ExportValidResultsButton.IsEnabled = false;
	}

	private async void ExportSearchResultsButton_Click(object sender, RoutedEventArgs e)
	{
		await ExportFlagsAsync(_lastSearchResults.ToDictionary((FlagSearchResult r) => r.Name, (FlagSearchResult r) => ParseValue(r.Value)), "search_results");
	}

	private async void ExportValidResultsButton_Click(object sender, RoutedEventArgs e)
	{
		Dictionary<string, object> flags = _lastValidationResults.Where((FlagValidationResult r) => r.Status == "✓ Valid").ToDictionary((FlagValidationResult r) => r.Name, (FlagValidationResult r) => ParseValue(r.ValidValue));
		await ExportFlagsAsync(flags, "valid_flags");
	}

	private async void DownloadAllRecentButton_Click(object sender, RoutedEventArgs e)
	{
		await ExportFlagsAsync(_recentFlags.ToDictionary((FlagSearchResult r) => r.Name, (FlagSearchResult r) => ParseValue(r.Value)), "recent_flags_all");
	}

	private async void DownloadTrueRecentButton_Click(object sender, RoutedEventArgs e)
	{
		Dictionary<string, object> flags = _recentFlags.Where((FlagSearchResult r) => IsTrueValue(ParseValue(r.Value))).ToDictionary((FlagSearchResult r) => r.Name, (FlagSearchResult r) => ParseValue(r.Value));
		await ExportFlagsAsync(flags, "recent_flags_true");
	}

	private async void DownloadFalseRecentButton_Click(object sender, RoutedEventArgs e)
	{
		Dictionary<string, object> flags = _recentFlags.Where((FlagSearchResult r) => IsFalseValue(ParseValue(r.Value))).ToDictionary((FlagSearchResult r) => r.Name, (FlagSearchResult r) => ParseValue(r.Value));
		await ExportFlagsAsync(flags, "recent_flags_false");
	}

	private async Task ExportFlagsAsync(Dictionary<string, object> flags, string defaultName)
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
				JsonSerializerOptions options = new JsonSerializerOptions
				{
					WriteIndented = true,
					Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
				};
				string contents = JsonSerializer.Serialize(flags, options);
				await File.WriteAllTextAsync(dialog.FileName, contents);
				System.Windows.MessageBox.Show($"Exported {flags.Count} flags to {dialog.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show("Error exporting flags: " + ex.Message, "Export Error", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private async Task UpdateStatusAsync(string status)
	{
		await ((DispatcherObject)this).Dispatcher.InvokeAsync<string>((Func<string>)(() => StatusText.Text = status));
	}

	private void ShowProgress(bool show)
	{
		((DispatcherObject)this).Dispatcher.Invoke<Visibility>((Func<Visibility>)(() => LoadingProgress.Visibility = ((!show) ? Visibility.Collapsed : Visibility.Visible)));
	}

	private void UpdateSearchResultsCount()
	{
		SearchResultsCount.Text = $"{_searchResults.Count} results";
	}

	private void UpdateValidationResultsCount()
	{
		ValidationResultsCount.Text = $"{_validationResults.Count} results";
	}

	private void UpdateRecentFlagsCount()
	{
		RecentFlagsCount.Text = $"{_recentFlags.Count} recent flags";
	}

	private void UpdateTotalFlagsCount()
	{
		if (_allFlags.Count > 0)
		{
			StatusText.Text = $"Done: {_allFlags.Count} flags loaded!";
		}
	}

	private static bool IsTrueValue(object value)
	{
		if (!(value is bool result))
		{
			if (!(value is string text))
			{
				if (value is int num)
				{
					return num != 0;
				}
				return false;
			}
			return text.Equals("true", StringComparison.OrdinalIgnoreCase);
		}
		return result;
	}

	private static bool IsFalseValue(object value)
	{
		if (!(value is bool flag))
		{
			if (!(value is string text))
			{
				if (value is int num)
				{
					return num == 0;
				}
				return false;
			}
			return text.Equals("false", StringComparison.OrdinalIgnoreCase);
		}
		return !flag;
	}

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
		if (double.TryParse(value, out var result3))
		{
			return result3;
		}
		return value;
	}

	protected override void OnClosed(EventArgs e)
	{
		Interlocked.Increment(ref _searchGeneration);
		_lifetimeCancellation.Cancel();
		_lifetimeCancellation.Dispose();
		base.OnClosed(e);
	}

	private void TrueFlagsOnlyCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
	{
		string text = SearchTextBox.Text?.Trim();
		if (!string.IsNullOrEmpty(text))
		{
			int generation = Interlocked.Increment(ref _searchGeneration);
			_ = PerformSearchAsync(text, generation, _lifetimeCancellation.Token);
		}
	}

	private void FalseFlagsOnlyCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
	{
		string text = SearchTextBox.Text?.Trim();
		if (!string.IsNullOrEmpty(text))
		{
			int generation = Interlocked.Increment(ref _searchGeneration);
			_ = PerformSearchAsync(text, generation, _lifetimeCancellation.Token);
		}
	}

	private void PasteMenuItem_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (Clipboard.ContainsText())
			{
				string text = Clipboard.GetText();
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
			System.Windows.MessageBox.Show("Error pasting from clipboard: " + ex.Message, "Paste Error", MessageBoxButton.OK, MessageBoxImage.Hand);
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
