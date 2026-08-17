using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.SharpZipLib.Zip;

namespace Voidstrap.UI.ViewModels.Settings;

public class PluginsViewModel : INotifyPropertyChanged
{
	private const int MaxArchiveBytes = 8 * 1024 * 1024;
	private const int MaxEntryBytes = 4 * 1024 * 1024;
	private const int MaxPluginCount = 128;
	private readonly object _saveGate = new object();
	private CancellationTokenSource? _autoSaveCancellation;
	private CancellationTokenSource? _previewCancellation;
	private class PlayAreaSession
	{
		public List<PluginModel> Plugins { get; set; }

		public string SelectedPlugin { get; set; }
	}

	private bool _suppressCodeSync;

	private PluginModel _selectedPlugin;

	private PluginModel _selectedPublicPlugin;

	private string _pluginXamlCode;

	private string _pluginCsCode;

	private FrameworkElement _pluginPreview;

	private string _newPluginName;

	private readonly string AutoSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Voidstrap", "autosave_plugin.zip");

	private readonly string PluginSessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Voidstrap", "plugin_session.json");

	public ObservableCollection<PluginModel> LoadedPlugins { get; } = new ObservableCollection<PluginModel>();

	public ObservableCollection<PluginModel> PublicPlugins { get; } = new ObservableCollection<PluginModel>();

	public PluginModel SelectedPlugin
	{
		get
		{
			return _selectedPlugin;
		}
		set
		{
			if (SetProperty(ref _selectedPlugin, value, "SelectedPlugin"))
			{
				RunPluginCommand.NotifyCanExecuteChanged();
			}
			SavePlayArea();
		}
	}

	public PluginModel SelectedPublicPlugin
	{
		get
		{
			return _selectedPublicPlugin;
		}
		set
		{
			if (SetProperty(ref _selectedPublicPlugin, value, "SelectedPublicPlugin"))
			{
				LoadPublicPluginCommand.NotifyCanExecuteChanged();
			}
		}
	}

	public string PluginXamlCode
	{
		get
		{
			return _pluginXamlCode;
		}
		set
		{
			if (SetProperty(ref _pluginXamlCode, value, "PluginXamlCode"))
			{
				QueueLivePreview();
				QueueAutoSavePlugin();
			}
		}
	}

	public string PluginCsCode
	{
		get
		{
			return _pluginCsCode;
		}
		set
		{
			if (SetProperty(ref _pluginCsCode, value, "PluginCsCode"))
			{
				AutoFixPluginCode();
				QueueAutoSavePlugin();
			}
		}
	}

	public FrameworkElement PluginPreview
	{
		get
		{
			return _pluginPreview;
		}
		set
		{
			SetProperty(ref _pluginPreview, value, "PluginPreview");
		}
	}

	public string NewPluginName
	{
		get
		{
			return _newPluginName;
		}
		set
		{
			if (SetProperty(ref _newPluginName, value, "NewPluginName"))
			{
				AddPluginCommand.NotifyCanExecuteChanged();
			}
		}
	}

	public RelayCommand CompileAndLoadPluginCommand { get; }

	public RelayCommand SavePluginCommand { get; }

	public RelayCommand NewPluginCommand { get; }

	public RelayCommand LoadPublicPluginCommand { get; }

	public RelayCommand RefreshPublicPluginsCommand { get; }

	public RelayCommand RunPluginCommand { get; }

	public RelayCommand AddPluginCommand { get; }

	public event PropertyChangedEventHandler PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
		{
			return false;
		}
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	public PluginsViewModel()
	{
		CompileAndLoadPluginCommand = new RelayCommand(CompileAndLoadPlugin);
		SavePluginCommand = new RelayCommand(SavePlugin);
		NewPluginCommand = new RelayCommand(NewPlugin);
		LoadPublicPluginCommand = new RelayCommand(LoadPublicPlugin, () => SelectedPublicPlugin != null);
		RefreshPublicPluginsCommand = new RelayCommand(RefreshPublicPlugins);
		RunPluginCommand = new RelayCommand(RunSelectedPlugin, CanRunPlugin);
		AddPluginCommand = new RelayCommand(AddPluginByName, CanAddPlugin);
		AutoLoadPlugin();
		LoadPlayArea();
		if (string.IsNullOrWhiteSpace(PluginXamlCode) || PluginXamlCode.Length > MaxEntryBytes)
		{
			NewPlugin();
		}
	}

	private void UpdateLivePreview()
	{
		if (string.IsNullOrWhiteSpace(PluginXamlCode))
		{
			PluginPreview = null;
			return;
		}
		try
		{
			XmlReaderSettings settings = new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = MaxEntryBytes,
				MaxCharactersFromEntities = 0
			};
			using StringReader source = new StringReader(PluginXamlCode);
			using XmlReader reader = XmlReader.Create(source, settings);
			object obj = XamlReader.Load(reader);
			if (obj is Window window)
			{
				object content = window.Content;
				window.Content = null;
				window.Close();
				PluginPreview = new UserControl
				{
					Content = content
				};
			}
			else if (obj is FrameworkElement content)
			{
				PluginPreview = new UserControl
				{
					Content = content
				};
			}
			else
			{
				PluginPreview = null;
			}
		}
		catch
		{
			PluginPreview = null;
		}
	}

	private void QueueLivePreview()
	{
		CancellationTokenSource next = new CancellationTokenSource();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _previewCancellation, next);
		previous?.Cancel();
		_ = UpdateLivePreviewAfterDelayAsync(next);
	}

	private async Task UpdateLivePreviewAfterDelayAsync(CancellationTokenSource owner)
	{
		try
		{
			await Task.Delay(500, owner.Token);
			UpdateLivePreview();
		}
		catch (OperationCanceledException) when (owner.IsCancellationRequested)
		{
		}
		finally
		{
			Interlocked.CompareExchange(ref _previewCancellation, null, owner);
			owner.Dispose();
		}
	}

	private void AutoFixPluginCode()
	{
		if (!string.IsNullOrWhiteSpace(PluginCsCode))
		{
			if (!PluginCsCode.Contains("using System;"))
			{
				PluginCsCode = "using System;\n" + PluginCsCode;
			}
			if (!PluginCsCode.Contains("using System.Windows;"))
			{
				PluginCsCode = "using System.Windows;\n" + PluginCsCode;
			}
		}
	}

	private void SavePlayArea()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(PluginSessionPath));
			string contents = JsonSerializer.Serialize(new
			{
				Plugins = LoadedPlugins.Select((PluginModel p) => new { p.Name, p.Author, p.Description, p.PluginXaml }).ToList(),
				SelectedPlugin = SelectedPlugin?.Name
			}, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			if (contents.Length > MaxArchiveBytes)
				throw new InvalidDataException("The plugin session is too large");
			string temporary = PluginSessionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				File.WriteAllText(temporary, contents);
				File.Move(temporary, PluginSessionPath, true);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to save play area: " + ex.Message);
		}
	}

	private void LoadPlayArea()
	{
		try
		{
			if (!File.Exists(PluginSessionPath))
			{
				return;
			}
			FileInfo info = new FileInfo(PluginSessionPath);
			if (info.Length <= 0 || info.Length > MaxArchiveBytes)
				throw new InvalidDataException("The plugin session size is invalid");
			string json = File.ReadAllText(PluginSessionPath);
			PlayAreaSession session = JsonSerializer.Deserialize<PlayAreaSession>(json);
			if (session?.Plugins == null)
			{
				return;
			}
			LoadedPlugins.Clear();
			foreach (PluginModel plugin in session.Plugins.Take(MaxPluginCount))
			{
				LoadedPlugins.Add(new PluginModel
				{
					Name = plugin.Name,
					Author = plugin.Author,
					Description = plugin.Description,
					PluginXaml = plugin.PluginXaml
				});
			}
			if (!string.IsNullOrEmpty(session.SelectedPlugin))
			{
				SelectedPlugin = LoadedPlugins.FirstOrDefault((PluginModel p) => p.Name == session.SelectedPlugin);
				if (SelectedPlugin != null)
				{
					PluginXamlCode = SelectedPlugin.PluginXaml;
				}
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to load play area: " + ex.Message);
		}
	}

	private void ConvertXamlToCSharp()
	{
		if (string.IsNullOrWhiteSpace(PluginXamlCode))
		{
			return;
		}
		try
		{
			string value = "GeneratedPlugin";
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("using System;");
			stringBuilder.AppendLine("using System.Collections.Generic;");
			stringBuilder.AppendLine("using System.Windows;");
			stringBuilder.AppendLine("using System.Windows.Controls;");
			stringBuilder.AppendLine("using System.Windows.Markup;");
			stringBuilder.AppendLine("using System.Windows.Media;");
			stringBuilder.AppendLine();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(22, 1, stringBuilder2);
			handler.AppendLiteral("public class ");
			handler.AppendFormatted(value);
			handler.AppendLiteral(" : Window");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder.AppendLine("{");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(13, 1, stringBuilder2);
			handler.AppendLiteral("    public ");
			handler.AppendFormatted(value);
			handler.AppendLiteral("()");
			stringBuilder4.AppendLine(ref handler);
			stringBuilder.AppendLine("    {");
			stringBuilder.AppendLine("        InitializeComponent();");
			stringBuilder.AppendLine("    }");
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("    private void InitializeComponent()");
			stringBuilder.AppendLine("    {");
			stringBuilder.AppendLine("        var xaml = @\"" + PluginXamlCode.Replace("\"", "\"\"") + "\";");
			stringBuilder.AppendLine("        var parsed = (Window)XamlReader.Parse(xaml);");
			stringBuilder.AppendLine("        this.Content = parsed.Content;");
			stringBuilder.AppendLine("        this.Title = parsed.Title;");
			stringBuilder.AppendLine("        this.Width = parsed.Width;");
			stringBuilder.AppendLine("        this.Height = parsed.Height;");
			stringBuilder.AppendLine("        this.Loaded += Window_Loaded;");
			stringBuilder.AppendLine("        this.Closed += Window_Closed;");
			stringBuilder.AppendLine("    }");
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("    private void Window_Loaded(object sender, RoutedEventArgs e)");
			stringBuilder.AppendLine("    {");
			stringBuilder.AppendLine("        AttachButtonHandlers(this);");
			stringBuilder.AppendLine("    }");
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("    private void Window_Closed(object sender, EventArgs e)");
			stringBuilder.AppendLine("    {");
			stringBuilder.AppendLine("        this.Loaded -= Window_Loaded;");
			stringBuilder.AppendLine("        this.Closed -= Window_Closed;");
			stringBuilder.AppendLine("        foreach (var btn in FindVisualChildren<Button>(this))");
			stringBuilder.AppendLine("            btn.Click -= Button_Click;");
			stringBuilder.AppendLine("    }");
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("    private void AttachButtonHandlers(DependencyObject root)");
			stringBuilder.AppendLine("    {");
			stringBuilder.AppendLine("        foreach (var btn in FindVisualChildren<Button>(root))");
			stringBuilder.AppendLine("        {");
			stringBuilder.AppendLine("            btn.Click -= Button_Click;");
			stringBuilder.AppendLine("            btn.Click += Button_Click;");
			stringBuilder.AppendLine("        }");
			stringBuilder.AppendLine("    }");
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("    private void Button_Click(object sender, RoutedEventArgs e)");
			stringBuilder.AppendLine("    {");
			stringBuilder.AppendLine("        if (sender is Button b)");
			stringBuilder.AppendLine("            MessageBox.Show($\"{b.Content} clicked!\", \"Plugin\", MessageBoxButton.OK);");
			stringBuilder.AppendLine("    }");
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject");
			stringBuilder.AppendLine("    {");
			stringBuilder.AppendLine("        if (parent == null) yield break;");
			stringBuilder.AppendLine("        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)");
			stringBuilder.AppendLine("        {");
			stringBuilder.AppendLine("            var child = VisualTreeHelper.GetChild(parent, i);");
			stringBuilder.AppendLine("            if (child is T t) yield return t;");
			stringBuilder.AppendLine("            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;");
			stringBuilder.AppendLine("        }");
			stringBuilder.AppendLine("    }");
			stringBuilder.AppendLine("}");
			PluginCsCode = stringBuilder.ToString();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Error generating C#: " + ex.Message);
		}
	}

	private void CompileAndLoadPlugin()
	{
		try
		{
			Frontend.ShowMessageBox("Plugin compilation is currently disabled to reduce application size.");
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Error loading plugin: " + ex.Message);
		}
	}

	private void SavePlugin()
	{
		try
		{
			CancelQueuedAutoSave();
			WriteAutoSavePlugin();
			Frontend.ShowMessageBox("Plugin saved successfully!");
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Save failed: " + ex.Message);
		}
	}

	private void AutoSavePlugin()
	{
		try
		{
			WriteAutoSavePlugin();
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Automatic save failed: " + ex.Message);
		}
	}

	private void WriteAutoSavePlugin()
	{
		lock (_saveGate)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(AutoSavePath));
			byte[] bytes = Encoding.UTF8.GetBytes(PluginXamlCode ?? "");
			byte[] bytes2 = Encoding.UTF8.GetBytes(PluginCsCode ?? "");
			if (bytes.Length > MaxEntryBytes || bytes2.Length > MaxEntryBytes)
				throw new InvalidDataException("The plugin source is too large");
			string temporary = AutoSavePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				using FileStream baseOutputStream = File.Create(temporary);
				using ZipOutputStream zipOutputStream = new ZipOutputStream(baseOutputStream);
				zipOutputStream.PutNextEntry(new ZipEntry("Plugin.xaml"));
				zipOutputStream.Write(bytes, 0, bytes.Length);
				zipOutputStream.PutNextEntry(new ZipEntry("Plugin.cs"));
				zipOutputStream.Write(bytes2, 0, bytes2.Length);
				zipOutputStream.Finish();
				File.Move(temporary, AutoSavePath, true);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}
	}

	private void AutoLoadPlugin()
	{
		try
		{
			if (!File.Exists(AutoSavePath))
			{
				return;
			}
			FileInfo info = new FileInfo(AutoSavePath);
			if (info.Length <= 0 || info.Length > MaxArchiveBytes)
				throw new InvalidDataException("The plugin archive size is invalid");
			using FileStream baseInputStream = File.OpenRead(AutoSavePath);
			using ZipInputStream zipInputStream = new ZipInputStream(baseInputStream);
			string text = null;
			string text2 = null;
			ZipEntry nextEntry;
			int entryCount = 0;
			while ((nextEntry = zipInputStream.GetNextEntry()) != null)
			{
				entryCount++;
				if (entryCount > 16 || nextEntry.Size > MaxEntryBytes)
					throw new InvalidDataException("The plugin archive contains invalid entries");
				string text3 = ReadPluginEntry(zipInputStream);
				if (string.Equals(nextEntry.Name, "Plugin.xaml", StringComparison.Ordinal))
				{
					text = text3;
				}
				else if (string.Equals(nextEntry.Name, "Plugin.cs", StringComparison.Ordinal))
				{
					text2 = text3;
				}
			}
			if (!string.IsNullOrEmpty(text))
			{
				PluginXamlCode = text;
			}
			if (!string.IsNullOrEmpty(text2))
			{
				PluginCsCode = text2;
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Auto-load failed: " + ex.Message);
		}
	}

	private static string ReadPluginEntry(Stream input)
	{
		using MemoryStream output = new MemoryStream();
		byte[] buffer = new byte[81920];
		while (true)
		{
			int read = input.Read(buffer, 0, buffer.Length);
			if (read == 0)
				return Encoding.UTF8.GetString(output.ToArray());
			if (output.Length + read > MaxEntryBytes)
				throw new InvalidDataException("The plugin archive entry is too large");
			output.Write(buffer, 0, read);
		}
	}

	private void QueueAutoSavePlugin()
	{
		CancellationTokenSource next = new CancellationTokenSource();
		CancellationTokenSource? previous = Interlocked.Exchange(ref _autoSaveCancellation, next);
		previous?.Cancel();
		_ = AutoSaveAfterDelayAsync(next);
	}

	private async Task AutoSaveAfterDelayAsync(CancellationTokenSource owner)
	{
		try
		{
			await Task.Delay(600, owner.Token).ConfigureAwait(false);
			AutoSavePlugin();
		}
		catch (OperationCanceledException) when (owner.IsCancellationRequested)
		{
		}
		finally
		{
			Interlocked.CompareExchange(ref _autoSaveCancellation, null, owner);
			owner.Dispose();
		}
	}

	private void CancelQueuedAutoSave()
	{
		CancellationTokenSource? cancellation = Interlocked.Exchange(ref _autoSaveCancellation, null);
		cancellation?.Cancel();
	}

	private void CancelQueuedPreview()
	{
		CancellationTokenSource? cancellation = Interlocked.Exchange(ref _previewCancellation, null);
		cancellation?.Cancel();
	}

	private void NewPlugin()
	{
		_suppressCodeSync = false;
		NewPluginName = $"NewPlugin_{DateTime.Now:MMddHHmm}";
		PluginXamlCode = "\n<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n        Title=\"New Plugin\" Width=\"300\" Height=\"200\">\n    <StackPanel VerticalAlignment=\"Center\" HorizontalAlignment=\"Center\">\n        <TextBlock Text=\"Hello, World!\" FontSize=\"16\" HorizontalAlignment=\"Center\"/>\n        <Button Content=\"Click Me!\" Margin=\"5\"/>\n    </StackPanel>\n</Window>";
		ConvertXamlToCSharp();
		CancelQueuedPreview();
		UpdateLivePreview();
		AutoSavePlugin();
		SavePlayArea();
		Frontend.ShowMessageBox("New plugin template created!");
	}

	private void LoadPublicPlugin()
	{
		if (SelectedPublicPlugin != null)
		{
			if (LoadedPlugins.Count >= MaxPluginCount)
			{
				Frontend.ShowMessageBox("The plugin limit has been reached.");
				return;
			}
			LoadedPlugins.Add(new PluginModel
			{
				Name = SelectedPublicPlugin.Name,
				Author = SelectedPublicPlugin.Author,
				Description = SelectedPublicPlugin.Description
			});
			AddPluginCommand.NotifyCanExecuteChanged();
			SavePlayArea();
		}
	}

	private void RefreshPublicPlugins()
	{
		Frontend.ShowMessageBox("Public plugin list refreshed!");
	}

	private void RunSelectedPlugin()
	{
		SelectedPlugin?.Run();
	}

	private bool CanRunPlugin()
	{
		return SelectedPlugin != null;
	}

	private void AddPluginByName()
	{
		if (string.IsNullOrWhiteSpace(NewPluginName))
		{
			Frontend.ShowMessageBox("Please enter a plugin name.");
			return;
		}
		if (LoadedPlugins.Count >= MaxPluginCount)
		{
			Frontend.ShowMessageBox("The plugin limit has been reached.");
			return;
		}
		PluginModel pluginModel = new PluginModel
		{
			Name = NewPluginName
		};
		LoadedPlugins.Add(pluginModel);
		AddPluginCommand.NotifyCanExecuteChanged();
		SelectedPlugin = pluginModel;
		NewPluginName = string.Empty;
		SavePlayArea();
	}

	private bool CanAddPlugin()
	{
		return LoadedPlugins.Count < MaxPluginCount && !string.IsNullOrWhiteSpace(NewPluginName);
	}
}
