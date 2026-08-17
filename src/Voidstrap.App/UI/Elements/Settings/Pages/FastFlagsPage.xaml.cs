using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Navigation;
using System.Windows.Threading;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class FastFlagsPage : UiPage{
	public class NvidiaFFlag : INotifyPropertyChanged, IDataErrorInfo
	{
		private string _name = string.Empty;

		private string _value = string.Empty;

		public string Name
		{
			get
			{
				return _name;
			}
			set
			{
				_name = value;
				OnPropertyChanged("Name");
			}
		}

		public string Value
		{
			get
			{
				return _value;
			}
			set
			{
				_value = value;
				OnPropertyChanged("Value");
			}
		}

		public string Error => null;

		public string this[string columnName]
		{
			get
			{
				if (columnName == "Name" && string.IsNullOrWhiteSpace(Name))
				{
					return "Flag name is required";
				}
				if (columnName == "Value" && string.IsNullOrWhiteSpace(Value))
				{
					return "Value is required";
				}
				return null;
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		protected void OnPropertyChanged(string prop)
		{
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
		}
	}

	public class FFlagItem
	{
		public string Name { get; set; }

		public string Value { get; set; }
	}

	private bool _initialLoad;

	private FastFlagsViewModel _viewModel;
	private bool _loadingFlags;

	private const string AllowlistJsonUrl = RobloxFastFlagAllowlist.AnnouncementUrl + ".json";

	private static Dictionary<string, string>? _allowlistCache;

	private static Task<Dictionary<string, string>>? _allowlistTask;

	private static async Task<Dictionary<string, string>> LoadAllowlistCoreAsync()
	{
		string payload = await Voidstrap.Utility.Http.GetStringBoundedAsync(_httpClient, AllowlistJsonUrl).ConfigureAwait(continueOnCapturedContext: false);
		using JsonDocument document = JsonDocument.Parse(payload);
		string cooked = document.RootElement
			.GetProperty("post_stream")
			.GetProperty("posts")[0]
			.GetProperty("cooked")
			.GetString() ?? string.Empty;
		Dictionary<string, string> parsed = ParseOfficialAllowlist(cooked);
		if (parsed.Count == 0)
		{
			throw new InvalidOperationException("Roblox's allowlist post did not contain any flags.");
		}
		_allowlistCache = parsed;
		return parsed;
	}

	private static Dictionary<string, string> ParseOfficialAllowlist(string cooked)
	{
		Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
		int start = cooked.IndexOf("What Fast Flags are currently on the allowlist?", StringComparison.OrdinalIgnoreCase);
		int end = cooked.IndexOf("How have the Fast Flags on the Allowlist been chosen?", StringComparison.OrdinalIgnoreCase);
		if (start < 0 || end <= start)
			return result;

		string section = cooked[start..end];
		foreach (Match categoryMatch in Regex.Matches(section, "<p><strong>(?<category>[^<]+)</strong>:</p>\\s*<ul>(?<items>.*?)</ul>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
		{
			string category = WebUtility.HtmlDecode(categoryMatch.Groups["category"].Value).Trim();
			foreach (Match flagMatch in Regex.Matches(categoryMatch.Groups["items"].Value, "<li>(?<flag>[A-Za-z][A-Za-z0-9_]*)</li>", RegexOptions.IgnoreCase))
			{
				string flag = flagMatch.Groups["flag"].Value;
				result[flag] = category;
			}
		}
		return result;
	}

	private static readonly Regex _intRegex = new Regex("^[0-9]+$");

	private static readonly HttpClient _httpClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(25L));

	public ObservableCollection<FFlagItem> FFlags { get; } = new ObservableCollection<FFlagItem>();

	public ObservableCollection<NvidiaFFlag> CustomFFlags { get; } = new ObservableCollection<NvidiaFFlag>();

	private void ValidateInt(object sender, TextCompositionEventArgs e)
	{
		e.Handled = !_intRegex.IsMatch(e.Text);
	}

	public FastFlagsPage()
	{
		SetupViewModel();
		InitializeComponent();
		base.Loaded += LoadFlagsOnLoaded;
		base.Unloaded += FastFlagsPage_Unloaded;
	}

	private void FastFlagsPage_Unloaded(object sender, RoutedEventArgs e)
	{
		_loadingFlags = false;
	}

	private async void LoadFlagsOnLoaded(object sender, RoutedEventArgs e)
	{
		if (_loadingFlags || FFlags.Count > 0)
			return;

		_loadingFlags = true;
		try
		{
			await LoadFFlagsAsync();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("FastFlagsPage::LoadFlagsOnLoaded", ex);
		}
		finally
		{
			_loadingFlags = false;
		}
	}

	private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
	{
		Process.Start(new ProcessStartInfo
		{
			FileName = e.Uri.AbsoluteUri,
			UseShellExecute = true
		});
		e.Handled = true;
	}

	private async Task LoadFFlagsAsync()
	{
		if (FFlags.Count > 0)
		{
			return;
		}

		Dictionary<string, string> dict;
		try
		{
			dict = _allowlistCache ?? await (_allowlistTask ??= LoadAllowlistCoreAsync());
		}
		catch (Exception ex)
		{
			_allowlistTask = null;
			App.Logger.WriteException("FastFlagsPage::LoadFFlagsAsync", ex);
			dict = RobloxFastFlagAllowlist.Flags.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
		}

		FFlags.Clear();
		foreach (KeyValuePair<string, string> item in dict.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key))
		{
			FFlags.Add(new FFlagItem
			{
				Name = item.Key,
				Value = item.Value,
			});
		}
		DataGrid.ItemsSource = FFlags;
	}

	private void NvidiaFrame_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
	{
		if (e.Handled)
		{
			return;
		}
		DependencyObject node = e.OriginalSource as DependencyObject;
		while (node != null && node != sender)
		{
			if (node is ScrollViewer scrollViewer && scrollViewer.ScrollableHeight > 0.0)
			{
				return;
			}
			node = ((node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D) ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node));
		}
		e.Handled = true;
		System.Windows.Input.MouseWheelEventArgs args = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
		{
			RoutedEvent = UIElement.MouseWheelEvent,
			Source = sender
		};
		if (sender is UIElement element)
		{
			element.RaiseEvent(args);
		}
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		if (!_initialLoad)
		{
			_initialLoad = true;
		}
		else
		{
			SetupViewModel();
		}
	}

	private void SetupViewModel()
	{
		if (_viewModel != null)
		{
			_viewModel.OpenFlagEditorEvent -= OpenFlagEditor;
			_viewModel.RequestPageReloadEvent -= RequestPageReload;
		}

		_viewModel = new FastFlagsViewModel();
		_viewModel.OpenFlagEditorEvent += OpenFlagEditor;
		_viewModel.RequestPageReloadEvent += RequestPageReload;
		base.DataContext = _viewModel;
	}

	private void RequestPageReload(object? sender, EventArgs e)
	{
		SetupViewModel();
	}

	private void ValidateIntInput(object sender, TextCompositionEventArgs e)
	{
		System.Windows.Controls.TextBox textBox = sender as System.Windows.Controls.TextBox;
		string input = textBox.Text.Insert(textBox.SelectionStart, e.Text);
		e.Handled = !Regex.IsMatch(input, "^[\\+\\-]?[0-9]*$");
	}

	private void OpenFlagEditor(object? sender, EventArgs e)
	{
		if (Window.GetWindow((DependencyObject)(object)this) is INavigationWindow navigationWindow)
		{
			navigationWindow.Navigate(typeof(FastFlagEditorPage));
		}
	}

	private void ValidateInt32(object sender, TextCompositionEventArgs e)
	{
		e.Handled = e.Text != "-" && !int.TryParse(e.Text, out var _);
	}

	private void ValidateUInt32(object sender, TextCompositionEventArgs e)
	{
		e.Handled = !uint.TryParse(e.Text, out var _);
	}

	private void CheckSystemButton_Click(object sender, RoutedEventArgs e)
	{
		CheckSystemButton.IsEnabled = false;
		try
		{
			ApplyRecommendedFlags();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("FastFlagsPage::ApplyRecommended", ex);
			Frontend.ShowMessageBox("The recommended flags could not be applied: " + ex.Message, MessageBoxImage.Hand);
		}
		finally
		{
			CheckSystemButton.IsEnabled = true;
		}
	}

	private void ApplyRecommendedFlags()
	{
		string profile = (RecommendedProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Balanced";
		Dictionary<string, Dictionary<string, string>> profiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
		{
			["Performance"] = new Dictionary<string, string>
			{
				["DFFlagTextureQualityOverrideEnabled"] = "True",
				["DFIntTextureQualityOverride"] = "0",
				["FIntDebugForceMSAASamples"] = "1",
			},
			["Balanced"] = new Dictionary<string, string>
			{
				["DFFlagTextureQualityOverrideEnabled"] = "True",
				["DFIntTextureQualityOverride"] = "2",
				["FIntDebugForceMSAASamples"] = "2",
			},
			["Quality"] = new Dictionary<string, string>
			{
				["DFFlagTextureQualityOverrideEnabled"] = "True",
				["DFIntTextureQualityOverride"] = "3",
				["FIntDebugForceMSAASamples"] = "4",
			},
		};

		if (!profiles.TryGetValue(profile, out Dictionary<string, string>? flags))
		{
			Frontend.ShowMessageBox("Choose a recommended profile first.", MessageBoxImage.Warning);
			return;
		}

		foreach (KeyValuePair<string, string> flag in flags)
		{
			if (!RobloxFastFlagAllowlist.IsAllowed(flag.Key))
				throw new InvalidOperationException(flag.Key + " is not on Roblox's FastFlag allowlist.");
			App.FastFlags.SetValue(flag.Key, flag.Value);
		}
		App.FastFlags.Save();
		Frontend.ShowMessageBox(profile + " profile applied. " + flags.Count + " Roblox allowlisted flags were updated.\nYour other flags were left unchanged.", MessageBoxImage.Asterisk);
	}

}
