using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Search;
using Voidstrap.Integrations.Plugins;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class VoidstrapModsPage : UiPage
{
	private static readonly string[] AllowedExtensions = [".tsx", ".ts", ".js", ".mjs", ".jsx", ".json", ".md"];

	[GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
	private static partial Regex ModNameRegex { get; }

	private string? _editingMod;

	private string? _editingFile;

	private bool _suppressEditorEvents;

	private bool _suppressManifestEvents;

	private bool _isDirty;

	private bool _editorInitialized;

	public VoidstrapModsPage()
	{
		LinuxEditorCompat.Install();
		InitializeComponent();
		InitializeEditorControls();
	}

	private void InitializeEditorControls()
	{
		if (_editorInitialized)
			return;
		_editorInitialized = true;
		ModCodeEditor.Options.IndentationSize = 2;
		ModCodeEditor.Options.ConvertTabsToSpaces = true;
		ModCodeEditor.Options.HighlightCurrentLine = true;
		ModCodeEditor.Options.AllowScrollBelowDocument = true;
		ModCodeEditor.Options.EnableTextDragDrop = true;
		ModCodeEditor.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
		ModCodeEditor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(24, 96, 165, 250));
		ModCodeEditor.TextArea.TextView.CurrentLineBorder = new Pen(new SolidColorBrush(Color.FromArgb(64, 96, 165, 250)), 1);
		ModCodeEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(100, 65, 135, 225));
		ModCodeEditor.TextArea.Caret.CaretBrush = new SolidColorBrush(Color.FromRgb(116, 185, 255));
		SearchPanel.Install(ModCodeEditor.TextArea);
		ModCodeEditor.TextChanged += ModCodeEditor_TextChanged;
		ModCodeEditor.TextArea.Caret.PositionChanged += (_, _) =>
		{
			EditorPositionText.Text = "Ln " + ModCodeEditor.TextArea.Caret.Line + ", Col " + ModCodeEditor.TextArea.Caret.Column;
		};
		ModCodeEditor.PreviewKeyDown += ModCodeEditor_PreviewKeyDown;
		PopulateTemplates();
		PopulateSnippets();
		ApiGuideTextBox.Text = BuildApiReferenceText();
	}

	private void PopulateTemplates()
	{
		TemplateComboBox.Items.Clear();
		TemplateComboBox.Items.Add(new ComboBoxItem { Content = Strings.Ui_VoidstrapMods_TemplateDashboard, Tag = "dashboard" });
		TemplateComboBox.Items.Add(new ComboBoxItem { Content = Strings.Ui_VoidstrapMods_TemplateSidebarPage, Tag = "sidebar-page" });
		TemplateComboBox.Items.Add(new ComboBoxItem { Content = Strings.Ui_VoidstrapMods_TemplateThemeCustomizer, Tag = "theme-customizer" });
		TemplateComboBox.Items.Add(new ComboBoxItem { Content = Strings.Ui_VoidstrapMods_TemplateSidebarManager, Tag = "sidebar-manager" });
		TemplateComboBox.Items.Add(new ComboBoxItem { Content = Strings.Ui_VoidstrapMods_TemplateFastFlags, Tag = "fastflags" });
		TemplateComboBox.Items.Add(new ComboBoxItem { Content = Strings.Ui_VoidstrapMods_TemplateGameHooks, Tag = "game-hooks" });
		TemplateComboBox.Items.Add(new ComboBoxItem { Content = Strings.Ui_VoidstrapMods_TemplateBlank, Tag = "blank" });
		TemplateComboBox.SelectedIndex = 0;
	}

	private void PopulateSnippets()
	{
		SnippetCombo.Items.Clear();
		foreach ((string label, string code) in GetSnippets())
			SnippetCombo.Items.Add(new ComboBoxItem { Content = label, Tag = code });
		if (SnippetCombo.Items.Count > 0)
			SnippetCombo.SelectedIndex = 0;
	}

	private void Page_Loaded(object sender, RoutedEventArgs e)
	{
		PluginHost.PanesChanged -= OnPanesChanged;
		PluginHost.PanesChanged += OnPanesChanged;
		PluginHost.PaneInvalidated -= OnPaneInvalidated;
		PluginHost.PaneInvalidated += OnPaneInvalidated;
		PluginHost.StatusChanged -= OnStatusChanged;
		PluginHost.StatusChanged += OnStatusChanged;
		PluginHost.ModsChanged -= OnModsChanged;
		PluginHost.ModsChanged += OnModsChanged;
		RenderMods();
		RenderAll();
		RenderStatus();
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e)
	{
		PluginHost.PanesChanged -= OnPanesChanged;
		PluginHost.PaneInvalidated -= OnPaneInvalidated;
		PluginHost.StatusChanged -= OnStatusChanged;
		PluginHost.ModsChanged -= OnModsChanged;
	}

	private void OnModsChanged() => Dispatcher.BeginInvoke(new Action(RenderMods));

	private void RenderMods()
	{
		ModsHost.Children.Clear();
		IReadOnlyList<PluginHost.ModInfo> mods = PluginHost.ListMods();
		foreach (PluginHost.ModInfo mod in mods)
		{
			Grid header = new();
			header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			StackPanel text = new();
			WrapPanel titleRow = new() { Orientation = Orientation.Horizontal };
			titleRow.Children.Add(PluginUiRenderer.MakeText(mod.Name + "  " + mod.Version, new JsonObject { ["bold"] = true, ["size"] = 14 }));
			Border stateBadge = new()
			{
				CornerRadius = new CornerRadius(8),
				Padding = new Thickness(7, 1, 7, 1),
				Margin = new Thickness(6, 0, 6, 6),
				VerticalAlignment = VerticalAlignment.Center,
				Background = new SolidColorBrush(mod.Loaded ? Color.FromArgb(45, 46, 204, 113) : Color.FromArgb(35, 160, 168, 180)),
				Child = new System.Windows.Controls.TextBlock
				{
					Text = mod.Loaded ? Strings.Ui_VoidstrapMods_LoadedBadge : Strings.Ui_VoidstrapMods_DisabledBadge,
					FontSize = 11,
					FontWeight = FontWeights.SemiBold,
					Foreground = new SolidColorBrush(mod.Loaded ? Color.FromRgb(88, 214, 141) : Color.FromRgb(190, 196, 206))
				}
			};
			titleRow.Children.Add(stateBadge);
			text.Children.Add(titleRow);
			if (mod.Description.Length > 0)
				text.Children.Add(PluginUiRenderer.MakeText(mod.Description, new JsonObject { ["dim"] = true }));
			if (mod.Hooks.Count > 0)
				text.Children.Add(PluginUiRenderer.MakeText("Hooks: " + string.Join(", ", mod.Hooks), new JsonObject { ["dim"] = true, ["size"] = 11, ["mono"] = true }));
			if (mod.Consent == "on" && !mod.Loaded)
				text.Children.Add(PluginUiRenderer.MakeText(Strings.Ui_VoidstrapMods_NotLoaded, new JsonObject()));
			header.Children.Add(text);

			WrapPanel actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
			Wpf.Ui.Controls.Button editBtn = new()
			{
				Content = Strings.Ui_VoidstrapMods_EditButton,
				Icon = SymbolRegular.Code24,
				Appearance = string.Equals(_editingMod, mod.Name, StringComparison.Ordinal) ? ControlAppearance.Primary : ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 6, 0),
				Tag = mod.Name
			};
			editBtn.Click += EditMod_Click;
			actions.Children.Add(editBtn);

			Wpf.Ui.Controls.Button validateBtn = new()
			{
				Content = Strings.Ui_VoidstrapMods_ValidateButton,
				Icon = SymbolRegular.CheckmarkCircle24,
				Appearance = ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 6, 0),
				Tag = mod.Name
			};
			validateBtn.Click += ValidateCardMod_Click;
			actions.Children.Add(validateBtn);

			Wpf.Ui.Controls.Button testBtn = new()
			{
				Content = Strings.Ui_VoidstrapMods_TestButton,
				Icon = SymbolRegular.Play24,
				Appearance = ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 6, 0),
				Tag = mod.Name
			};
			testBtn.Click += TestCardMod_Click;
			actions.Children.Add(testBtn);

			Wpf.Ui.Controls.Button deleteBtn = new()
			{
				Icon = SymbolRegular.Delete24,
				ToolTip = Strings.Common_Delete,
				Appearance = ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 10, 0),
				Tag = mod.Name
			};
			deleteBtn.Click += DeleteMod_Click;
			actions.Children.Add(deleteBtn);

			ToggleSwitch toggle = new() { IsChecked = mod.Consent == "on", Tag = mod.Name, VerticalAlignment = VerticalAlignment.Center };
			toggle.Click += ModToggle_Click;
			actions.Children.Add(toggle);

			Grid.SetColumn(actions, 1);
			header.Children.Add(actions);

			StackPanel body = new();
			body.Children.Add(header);
			if (mod.Problems.Count > 0)
			{
				System.Windows.Controls.TextBlock problems = new()
				{
					Text = string.Join(Environment.NewLine, mod.Problems.TakeLast(10)),
					FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono, monospace"),
					FontSize = 11,
					TextWrapping = TextWrapping.Wrap
				};
				problems.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
				body.Children.Add(new Expander { Header = Strings.Ui_VoidstrapMods_RecentProblems, Content = problems, Margin = new Thickness(0, 8, 0, 0) });
			}
			Border card = new()
			{
				Child = body,
				Padding = new Thickness(14, 10, 14, 10),
				Margin = new Thickness(0, 0, 0, 6),
				CornerRadius = new CornerRadius(6),
				BorderThickness = new Thickness(1)
			};
			card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
			card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
			ModsHost.Children.Add(card);
		}
	}

	private void NewMod_Click(object sender, RoutedEventArgs e)
	{
		if (CreateModCard.Visibility == Visibility.Visible)
		{
			CreateModCard.Visibility = Visibility.Collapsed;
			return;
		}
		CreateModErrorText.Visibility = Visibility.Collapsed;
		CreateModCard.Visibility = Visibility.Visible;
		ApplyTemplateDefaults();
		NewModNameBox.Focus();
	}

	private void CreateModCancel_Click(object sender, RoutedEventArgs e)
	{
		CreateModCard.Visibility = Visibility.Collapsed;
	}

	private void TemplateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (CreateModCard?.Visibility == Visibility.Visible)
			ApplyTemplateDefaults();
	}

	private void ApplyTemplateDefaults()
	{
		string template = (TemplateComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dashboard";
		(string defaultName, string defaultDesc) = template switch
		{
			"sidebar-page" => ("custom-sidebar-page", "Adds a custom page directly to the Voidstrap navigation sidebar"),
			"theme-customizer" => ("theme-studio-pro", "Customizes Voidstrap theme brushes, window title, backdrop and banners live"),
			"sidebar-manager" => ("sidebar-controller", "Renames, reorders, hides and changes icons on Voidstrap sidebar items"),
			"fastflags" => ("fastflag-profiles", "Quick FastFlag and Voidstrap settings preset controller"),
			"game-hooks" => ("session-tracker", "Tracks Roblox game joins, session time and quick place launches"),
			"blank" => ("my-custom-mod", "Custom Voidstrap mod"),
			_ => ("control-center", "Interactive Voidstrap control center pane")
		};
		if (string.IsNullOrWhiteSpace(NewModNameBox.Text) || IsSuggestedName(NewModNameBox.Text))
			NewModNameBox.Text = NextAvailableModName(defaultName);
		if (string.IsNullOrWhiteSpace(NewModDescriptionBox.Text) || IsSuggestedDescription(NewModDescriptionBox.Text))
			NewModDescriptionBox.Text = defaultDesc;
	}

	private static bool IsSuggestedName(string text) => text.StartsWith("custom-sidebar-page", StringComparison.Ordinal)
		|| text.StartsWith("theme-studio-pro", StringComparison.Ordinal)
		|| text.StartsWith("sidebar-controller", StringComparison.Ordinal)
		|| text.StartsWith("fastflag-profiles", StringComparison.Ordinal)
		|| text.StartsWith("session-tracker", StringComparison.Ordinal)
		|| text.StartsWith("my-custom-mod", StringComparison.Ordinal)
		|| text.StartsWith("control-center", StringComparison.Ordinal);

	private static bool IsSuggestedDescription(string text) => text is "Adds a custom page directly to the Voidstrap navigation sidebar"
		or "Customizes Voidstrap theme brushes, window title, backdrop and banners live"
		or "Renames, reorders, hides and changes icons on Voidstrap sidebar items"
		or "Quick FastFlag and Voidstrap settings preset controller"
		or "Tracks Roblox game joins, session time and quick place launches"
		or "Custom Voidstrap mod"
		or "Interactive Voidstrap control center pane";

	private static string NextAvailableModName(string baseName)
	{
		string folder = Path.Combine(Paths.Plugins, baseName);
		if (!Directory.Exists(folder))
			return baseName;
		for (int i = 2; i < 100; i++)
		{
			string candidate = baseName + "-" + i;
			if (!Directory.Exists(Path.Combine(Paths.Plugins, candidate)))
				return candidate;
		}
		return baseName;
	}

	private static string NormalizeModSlug(string raw)
	{
		string slug = Regex.Replace((raw ?? "").Trim().ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
		return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
	}

	private void CreateModConfirm_Click(object sender, RoutedEventArgs e)
	{
		string slug = NormalizeModSlug(NewModNameBox.Text);
		if (!ModNameRegex.IsMatch(slug))
		{
			CreateModErrorText.Text = Strings.Ui_VoidstrapMods_InvalidNameMessage;
			CreateModErrorText.Visibility = Visibility.Visible;
			return;
		}
		string modFolder = Path.Combine(Paths.Plugins, slug);
		if (Directory.Exists(modFolder))
		{
			CreateModErrorText.Text = string.Format(Strings.Ui_VoidstrapMods_AlreadyExistsMessage, slug);
			CreateModErrorText.Visibility = Visibility.Visible;
			return;
		}
		string version = string.IsNullOrWhiteSpace(NewModVersionBox.Text) ? "0.1.0" : NewModVersionBox.Text.Trim();
		string description = (NewModDescriptionBox.Text ?? "").Trim();
		string[] hosts = ParseHosts(NewModNetworkBox.Text);
		string template = (TemplateComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "dashboard";
		try
		{
			Directory.CreateDirectory(Path.Combine(modFolder, PluginManifest.ManifestFolder));
			Directory.CreateDirectory(Path.Combine(modFolder, "hooks"));
			JsonObject manifest = new()
			{
				["name"] = slug,
				["version"] = version,
				["description"] = description,
				["network"] = new JsonArray(hosts.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
				["userConfig"] = new JsonObject()
			};
			File.WriteAllText(Path.Combine(modFolder, PluginManifest.ManifestFolder, "plugin.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			JsonObject hooks = new()
			{
				["modules"] = new JsonArray(JsonValue.Create("./register.tsx"))
			};
			File.WriteAllText(Path.Combine(modFolder, "hooks", "hooks.json"), hooks.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
			(string registerCode, string testCode) = BuildTemplateFiles(template, slug, description);
			File.WriteAllText(Path.Combine(modFolder, "hooks", "register.tsx"), registerCode);
			File.WriteAllText(Path.Combine(modFolder, "pane.test.tsx"), testCode);
			ModTypes.Write(modFolder);
			CreateModCard.Visibility = Visibility.Collapsed;
			PluginHost.SetEnabled(slug, true);
			OpenModInEditor(slug);
			RunValidateAndDisplay(modFolder);
		}
		catch (Exception ex)
		{
			CreateModErrorText.Text = ex.Message;
			CreateModErrorText.Visibility = Visibility.Visible;
		}
	}

	private static string[] ParseHosts(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return [];
		return text.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Select(h => h.ToLowerInvariant())
			.Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private void EditMod_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string modName })
			OpenModInEditor(modName);
	}

	private void ValidateCardMod_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string modName })
		{
			if (!string.Equals(_editingMod, modName, StringComparison.Ordinal))
				OpenModInEditor(modName);
			RunValidateAndDisplay(Path.Combine(Paths.Plugins, modName));
		}
	}

	private void TestCardMod_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string modName })
		{
			if (!string.Equals(_editingMod, modName, StringComparison.Ordinal))
				OpenModInEditor(modName);
			RunTestsAndDisplay(Path.Combine(Paths.Plugins, modName));
		}
	}

	private void DeleteMod_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not FrameworkElement { Tag: string modName })
			return;
		if (Frontend.ShowMessageBox(
			string.Format(Strings.Ui_VoidstrapMods_DeleteModPrompt, modName),
			MessageBoxImage.Warning,
			MessageBoxButton.YesNo,
			MessageBoxResult.No) != MessageBoxResult.Yes)
			return;
		try
		{
			if (string.Equals(_editingMod, modName, StringComparison.Ordinal))
				CloseEditor();
			string folder = Path.Combine(Paths.Plugins, modName);
			if (Directory.Exists(folder))
				Directory.Delete(folder, true);
			PluginHost.ForgetConsent(modName);
			Task.Run(PluginHost.Reload);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
		}
	}

	public void OpenModInEditor(string modName)
	{
		string folder = Path.Combine(Paths.Plugins, modName);
		if (!Directory.Exists(folder))
			return;
		_editingMod = modName;
		EditorTitleText.Text = string.Format(Strings.Ui_VoidstrapMods_EditorTitle, modName);
		EditorCard.Visibility = Visibility.Visible;
		NewFilePanel.Visibility = Visibility.Collapsed;
		LoadManifestFields(folder);
		RefreshFileList(folder, "hooks/register.tsx");
		RenderMods();
	}

	private void LoadManifestFields(string folder)
	{
		_suppressManifestEvents = true;
		try
		{
			string manifestPath = Path.Combine(folder, PluginManifest.ManifestFolder, "plugin.json");
			if (File.Exists(manifestPath) && JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject obj)
			{
				EditVersionBox.Text = obj["version"]?.ToString() ?? "0.1.0";
				EditDescriptionBox.Text = obj["description"]?.ToString() ?? "";
				JsonArray? net = obj["network"] as JsonArray;
				EditNetworkBox.Text = net != null ? string.Join(", ", net.Select(n => n?.ToString() ?? "").Where(s => s.Length > 0)) : "";
			}
		}
		catch
		{
		}
		finally
		{
			_suppressManifestEvents = false;
		}
	}

	private void SaveManifestFieldsIfNeeded(string folder)
	{
		if (string.Equals(_editingFile, PluginManifest.ManifestFolder + "/plugin.json", StringComparison.OrdinalIgnoreCase))
			return;
		try
		{
			string manifestPath = Path.Combine(folder, PluginManifest.ManifestFolder, "plugin.json");
			JsonObject obj = File.Exists(manifestPath) && JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject parsed
				? parsed
				: new JsonObject { ["name"] = _editingMod ?? "" };
			obj["name"] = _editingMod ?? Path.GetFileName(folder);
			obj["version"] = string.IsNullOrWhiteSpace(EditVersionBox.Text) ? "0.1.0" : EditVersionBox.Text.Trim();
			obj["description"] = (EditDescriptionBox.Text ?? "").Trim();
			obj["network"] = new JsonArray(ParseHosts(EditNetworkBox.Text).Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
			if (obj["userConfig"] is not JsonObject)
				obj["userConfig"] = new JsonObject();
			Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
			File.WriteAllText(manifestPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		}
		catch
		{
		}
	}

	private void ManifestField_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_suppressManifestEvents || _editingMod == null)
			return;
		SetDirty(true);
	}

	private void RefreshFileList(string folder, string? preferredRelative)
	{
		string[] files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
			.Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/'))
			.Where(path => !path.StartsWith(PluginManifest.ManifestFolder + "/types/", StringComparison.OrdinalIgnoreCase))
			.Where(path => AllowedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
			.OrderBy(path => path.StartsWith("hooks/", StringComparison.OrdinalIgnoreCase) ? 0 : path.EndsWith(".test.tsx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
			.ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		_suppressEditorEvents = true;
		EditorFileCombo.Items.Clear();
		int selectIndex = 0;
		for (int i = 0; i < files.Length; i++)
		{
			EditorFileCombo.Items.Add(files[i]);
			if (preferredRelative != null && string.Equals(files[i], preferredRelative, StringComparison.OrdinalIgnoreCase))
				selectIndex = i;
		}
		_suppressEditorEvents = false;
		if (EditorFileCombo.Items.Count > 0)
		{
			EditorFileCombo.SelectedIndex = selectIndex;
			LoadSelectedFile(files[selectIndex]);
		}
	}

	private void EditorFileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_suppressEditorEvents || _editingMod == null)
			return;
		if (EditorFileCombo.SelectedItem is string nextFile)
		{
			if (_isDirty && _editingFile != null)
				SaveCurrentFileToDisk();
			LoadSelectedFile(nextFile);
		}
	}

	private void LoadSelectedFile(string relativePath)
	{
		if (_editingMod == null)
			return;
		string folder = Path.Combine(Paths.Plugins, _editingMod);
		string? fullPath = SafeModFilePath(folder, relativePath);
		if (fullPath == null || !File.Exists(fullPath))
			return;
		_editingFile = relativePath;
		_suppressEditorEvents = true;
		try
		{
			ModCodeEditor.Text = File.ReadAllText(fullPath);
			CodeHighlighting.Apply(ModCodeEditor, relativePath);
			EditorFileStatusText.Text = _editingMod + "/" + relativePath;
			bool isProtected = string.Equals(relativePath, "hooks/register.tsx", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(relativePath, "hooks/hooks.json", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(relativePath, PluginManifest.ManifestFolder + "/plugin.json", StringComparison.OrdinalIgnoreCase);
			DeleteFileButton.IsEnabled = !isProtected;
			SetDirty(false);
		}
		finally
		{
			_suppressEditorEvents = false;
		}
	}

	private void ModCodeEditor_TextChanged(object? sender, EventArgs e)
	{
		if (_suppressEditorEvents)
			return;
		SetDirty(true);
	}

	private void ModCodeEditor_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
		{
			e.Handled = true;
			SaveAndReload();
		}
	}

	private void SetDirty(bool dirty)
	{
		_isDirty = dirty;
		EditorDirtyText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
	}

	private bool SaveCurrentFileToDisk()
	{
		if (_editingMod == null || _editingFile == null)
			return false;
		string folder = Path.Combine(Paths.Plugins, _editingMod);
		string? fullPath = SafeModFilePath(folder, _editingFile);
		if (fullPath == null)
			return false;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
			File.WriteAllText(fullPath, ModCodeEditor.Text);
			SaveManifestFieldsIfNeeded(folder);
			if (string.Equals(_editingFile, PluginManifest.ManifestFolder + "/plugin.json", StringComparison.OrdinalIgnoreCase))
				LoadManifestFields(folder);
			SetDirty(false);
			return true;
		}
		catch (Exception ex)
		{
			EditorOutputBox.Text = ex.Message;
			return false;
		}
	}

	private void SaveAndReload_Click(object sender, RoutedEventArgs e) => SaveAndReload();

	private void SaveAndReload()
	{
		if (_editingMod == null || _editingFile == null)
			return;
		string modName = _editingMod;
		string fileName = _editingFile;
		if (!SaveCurrentFileToDisk())
			return;
		string folder = Path.Combine(Paths.Plugins, modName);
		PluginHost.ClearProblems(modName);
		StringWriter writer = new();
		writer.WriteLine(string.Format(Strings.Ui_VoidstrapMods_SavedMessage, modName, fileName));
		int code = ModTools.Validate(folder, writer);
		EditorOutputBox.Text = writer.ToString().Trim();
		if (code == 0)
			Task.Run(PluginHost.Reload);
	}

	private void ValidateEditorMod_Click(object sender, RoutedEventArgs e)
	{
		if (_editingMod == null)
			return;
		if (_isDirty)
			SaveCurrentFileToDisk();
		RunValidateAndDisplay(Path.Combine(Paths.Plugins, _editingMod));
	}

	private void TestEditorMod_Click(object sender, RoutedEventArgs e)
	{
		if (_editingMod == null)
			return;
		if (_isDirty)
			SaveCurrentFileToDisk();
		RunTestsAndDisplay(Path.Combine(Paths.Plugins, _editingMod));
	}

	private void RunValidateAndDisplay(string folder)
	{
		StringWriter writer = new();
		ModTools.Validate(folder, writer);
		EditorOutputBox.Text = writer.ToString().Trim();
	}

	private void RunTestsAndDisplay(string folder)
	{
		StringWriter writer = new();
		ModTools.Test(folder, writer);
		EditorOutputBox.Text = writer.ToString().Trim();
	}

	private void ShowNewFilePanel_Click(object sender, RoutedEventArgs e)
	{
		NewFilePanel.Visibility = NewFilePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
		if (NewFilePanel.Visibility == Visibility.Visible)
		{
			NewFilePathBox.Text = "hooks/helper.ts";
			NewFilePathBox.Focus();
		}
	}

	private void CancelAddFile_Click(object sender, RoutedEventArgs e)
	{
		NewFilePanel.Visibility = Visibility.Collapsed;
	}

	private void ConfirmAddFile_Click(object sender, RoutedEventArgs e)
	{
		if (_editingMod == null)
			return;
		string relative = (NewFilePathBox.Text ?? "").Trim().Replace('\\', '/').TrimStart('/');
		if (relative.Length == 0)
			return;
		if (!AllowedExtensions.Contains(Path.GetExtension(relative).ToLowerInvariant()))
			relative += ".tsx";
		string folder = Path.Combine(Paths.Plugins, _editingMod);
		string? fullPath = SafeModFilePath(folder, relative);
		if (fullPath == null)
			return;
		if (_isDirty)
			SaveCurrentFileToDisk();
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
			if (!File.Exists(fullPath))
			{
				string seed = relative.EndsWith(".test.tsx", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase)
					? "import { test, expect, mod } from 'voidstrap/testing';\n\ntest('loads cleanly', () => {\n  mod.emit('session.start', { source: 'startup' });\n  expect(true).toBe(true);\n});\n"
					: relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
						? "{\n}\n"
						: "export const helper = () => {\n  return true;\n};\n";
				File.WriteAllText(fullPath, seed);
			}
			NewFilePanel.Visibility = Visibility.Collapsed;
			RefreshFileList(folder, relative);
		}
		catch (Exception ex)
		{
			EditorOutputBox.Text = ex.Message;
		}
	}

	private void DeleteCurrentFile_Click(object sender, RoutedEventArgs e)
	{
		if (_editingMod == null || _editingFile == null)
			return;
		if (!DeleteFileButton.IsEnabled)
			return;
		string folder = Path.Combine(Paths.Plugins, _editingMod);
		string? fullPath = SafeModFilePath(folder, _editingFile);
		if (fullPath == null || !File.Exists(fullPath))
			return;
		try
		{
			File.Delete(fullPath);
			_isDirty = false;
			RefreshFileList(folder, "hooks/register.tsx");
		}
		catch (Exception ex)
		{
			EditorOutputBox.Text = ex.Message;
		}
	}

	private void InsertSnippet_Click(object sender, RoutedEventArgs e)
	{
		if (SnippetCombo.SelectedItem is not ComboBoxItem { Tag: string snippet })
			return;
		int offset = ModCodeEditor.CaretOffset;
		ModCodeEditor.Document.Insert(offset, Environment.NewLine + snippet + Environment.NewLine);
		ModCodeEditor.Focus();
	}

	private void ToggleApiGuide_Click(object sender, RoutedEventArgs e)
	{
		ApiGuideBorder.Visibility = ApiGuideBorder.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
	}

	private void CloseEditor_Click(object sender, RoutedEventArgs e) => CloseEditor();

	private void CloseEditor()
	{
		if (_isDirty)
			SaveCurrentFileToDisk();
		_editingMod = null;
		_editingFile = null;
		EditorCard.Visibility = Visibility.Collapsed;
		RenderMods();
	}

	private void UndoCode_Click(object sender, RoutedEventArgs e)
	{
		if (ModCodeEditor.CanUndo)
			ModCodeEditor.Undo();
		ModCodeEditor.Focus();
	}

	private void RedoCode_Click(object sender, RoutedEventArgs e)
	{
		if (ModCodeEditor.CanRedo)
			ModCodeEditor.Redo();
		ModCodeEditor.Focus();
	}

	private void ZoomOutCode_Click(object sender, RoutedEventArgs e)
	{
		ModCodeEditor.FontSize = Math.Clamp(ModCodeEditor.FontSize - 1, 10, 24);
	}

	private void ZoomInCode_Click(object sender, RoutedEventArgs e)
	{
		ModCodeEditor.FontSize = Math.Clamp(ModCodeEditor.FontSize + 1, 10, 24);
	}

	private static string? SafeModFilePath(string folder, string relative)
	{
		if (string.IsNullOrWhiteSpace(relative))
			return null;
		string root = Path.GetFullPath(folder);
		string full = Path.GetFullPath(Path.Combine(root, relative));
		return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
	}

	private void ModToggle_Click(object sender, RoutedEventArgs e)
	{
		if (sender is ToggleSwitch { Tag: string name } toggle)
			PluginHost.SetEnabled(name, toggle.IsChecked == true);
	}

	private void ConnectAi_Click(object sender, RoutedEventArgs e) => Voidstrap.Integrations.Mcp.McpConnectInfo.Show();

	private void Reload_Click(object sender, RoutedEventArgs e)
	{
		Task.Run(PluginHost.Reload);
	}

	private void OnPanesChanged() => Dispatcher.BeginInvoke(new Action(RenderAll));

	private void OnPaneInvalidated(string plugin) => Dispatcher.BeginInvoke(new Action(RenderAll));

	private void OnStatusChanged() => Dispatcher.BeginInvoke(new Action(RenderStatus));

	private void RenderStatus()
	{
		StatusText.Text = string.Join("   ", PluginHost.StatusLines);
	}

	private void RenderAll()
	{
		PanesHost.Children.Clear();
		IReadOnlyList<PluginHost.PaneInfo> panes = PluginHost.Panes;
		EmptyText.Visibility = panes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		foreach (PluginHost.PaneInfo pane in panes)
		{
			StackPanel body = new();
			body.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = pane.Title,
				FontSize = 16,
				FontWeight = FontWeights.SemiBold,
				Margin = new Thickness(0, 0, 0, 8)
			});
			UIElement? content = null;
			try
			{
				string dir = Path.Combine(Paths.Plugins, pane.Plugin);
				content = PluginUiRenderer.BuildTree(PluginHost.RenderPane(pane), dir);
			}
			catch (Exception ex)
			{
				PluginHost.Problem(pane.Plugin, "the pane " + pane.Id + " could not be drawn: " + PluginHost.Describe(ex));
			}
			if (content != null)
				body.Children.Add(content);
			Border card = new()
			{
				Child = body,
				Padding = new Thickness(16),
				Margin = new Thickness(0, 0, 0, 12),
				CornerRadius = new CornerRadius(8),
				BorderThickness = new Thickness(1)
			};
			card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
			card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
			PanesHost.Children.Add(card);
		}
	}

	private void OpenFolder_Click(object sender, RoutedEventArgs e)
	{
		Directory.CreateDirectory(Paths.Plugins);
		PlatformShell.TryOpenFolder(Paths.Plugins);
	}

	private static (string Register, string Test) BuildTemplateFiles(string template, string slug, string description)
	{
		string title = string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
		if (title.Length == 0)
			title = "Custom Mod";
		switch (template)
		{
			case "sidebar-page":
				return (
					$$"""
					import type { Register } from 'voidstrap';

					const visits = atom('visits', 1);
					const note = atom('note', 'Welcome to {{title}}');

					export const register: Register = (on) => {
					  on('session.start', ($) => {
					    $.sidebar.addPage({
					      id: 'main',
					      title: '{{title}}',
					      icon: 'Sparkle24',
					      section: 'Core'
					    });
					    $.ui.open({ id: 'overview', title: '{{title}}' });
					  });

					  on('ui.render', { component: 'Page', requestId: 'main' }, ($) => {
					    const { Box, Card, Grid, Text, Button, TextInput, Badge, InfoBar } = $.ui.resolve();
					    const count = read($, visits);
					    return (
					      <Box gap={10}>
					        <InfoBar severity="info" title="{{title}}" message="This custom page was added directly to the Voidstrap sidebar by your mod." />
					        <Grid columns={2} gap={10}>
					          <Card title="Page Controls" subtitle="Interact with state and navigation">
					            <Box direction="row" gap={6}>
					              <Badge text={'Visits: ' + count} variant="primary" />
					              <Badge text={'Platform: ' + $.app.platform} variant="success" />
					            </Box>
					            <Box direction="row" gap={6}>
					              <Button key="inc" label="Add Visit" variant="primary" icon="Add24" onPress={() => update($, visits, n => n + 1)} />
					              <Button key="go-home" label="Open Home" icon="Home24" onPress={() => $.nav.open('Home')} />
					              <Button key="go-mods" label="Open Mods" icon="Code24" onPress={() => $.nav.open('VoidstrapMods')} />
					            </Box>
					          </Card>
					          <Card title="Quick Note" subtitle="Saved in session state">
					            <Text text={read($, note)} bold={true} />
					            <TextInput key="note-input" value={read($, note)} placeholder="Type a note..." onChange={text => $.state.set('note', text)} />
					          </Card>
					        </Grid>
					      </Box>
					    );
					  });

					  on('ui.render', { component: 'Pane', requestId: 'overview' }, ($) => {
					    const { Box, Text, Button } = $.ui.resolve();
					    return (
					      <Box direction="row" gap={8}>
					        <Text text="Sidebar page registered." dim={true} />
					        <Button key="open-page" label="Open Sidebar Page" variant="primary" icon="Open24" onPress={() => $.nav.open('mod:{{slug}}:main')} />
					      </Box>
					    );
					  });
					};
					""",
					"""
					import { test, expect, mod } from 'voidstrap/testing';

					test('renders custom sidebar page and increments visits', () => {
					  mod.emit('session.start', { source: 'startup' });
					  const page = mod.renderPage('main');
					  expect(page).toBeTruthy();
					  mod.press(page, 'inc');
					  const updated = mod.renderPage('main');
					  expect(JSON.stringify(updated)).toContain('Visits: 2');
					});
					"""
				);

			case "theme-customizer":
				return (
					$$$"""
					import type { Register } from 'voidstrap';

					const activePreset = atom('preset', 'default');
					const bannerOpen = atom('bannerOpen', false);

					export const register: Register = (on) => {
					  on('session.start', ($) => {
					    $.ui.open({ id: 'theme', title: '{{{title}}}' });
					  });

					  on('ui.render', { component: 'Banner', requestId: 'top-banner' }, ($) => {
					    const { Box, Text, Badge, Button } = $.ui.resolve();
					    return (
					      <Box direction="row" gap={10}>
					        <Badge text="LIVE THEME" variant="primary" />
					        <Text text={'Active preset: ' + read($, activePreset)} bold={true} />
					        <Button key="hide-banner" label="Hide Banner" onPress={() => { $.ui.closeBanner('top-banner'); $.state.set('bannerOpen', false); }} />
					      </Box>
					    );
					  });

					  on('ui.render', { component: 'Pane', requestId: 'theme' }, ($) => {
					    const { Box, Grid, Card, Text, Button, Toggle, Slider, ColorSwatch } = $.ui.resolve();
					    const currentTheme = $.theme.get();
					    const applyPreset = (name: string, bg: string, card: string, accent: string) => {
					      $.state.set('preset', name);
					      $.theme.setBrush('WindowBackgroundColorPrimary', bg);
					      $.theme.setBrush('WindowBackgroundColorSecondary', bg);
					      $.theme.setBrush('CardBackgroundFillColorDefaultBrush', card);
					      $.theme.setBrush('AccentFillColorDefaultBrush', accent);
					    };
					    return (
					      <Box gap={10}>
					        <Grid columns={2} gap={10}>
					          <Card title="Color Presets" subtitle="Override WPF resource brushes across the entire app">
					            <Box direction="row" gap={8} wrap={true}>
					              <ColorSwatch key="preset-cyber" color="#0F172A" label="Midnight Cyan" onPress={() => applyPreset('Midnight Cyan', '#0B1120', '#162032', '#38BDF8')} />
					              <ColorSwatch key="preset-crimson" color="#2A0F18" label="Crimson Glow" onPress={() => applyPreset('Crimson Glow', '#1A0B12', '#2B1420', '#F43F5E')} />
					              <ColorSwatch key="preset-emerald" color="#0B221A" label="Emerald Matrix" onPress={() => applyPreset('Emerald Matrix', '#091914', '#132A22', '#10B981')} />
					            </Box>
					            <Box direction="row" gap={6}>
					              <Button key="reset-brushes" label="Reset Default Brushes" icon="ArrowReset24" onPress={() => { $.theme.resetBrushes(); $.state.set('preset', 'default'); }} />
					            </Box>
					          </Card>
					          <Card title="Window & Banner" subtitle="Control MainWindow zoom, backdrop and banners">
					            <Slider key="zoom-slider" label="UI Zoom %" min={75} max={150} step={5} value={currentTheme.uiZoomPercent || 100} onChange={val => $.theme.set({ uiZoomPercent: val })} />
					            <Toggle key="banner-toggle" label="Show Top Window Banner" checked={read($, bannerOpen)} onChange={on => { $.state.set('bannerOpen', on); if (on) $.ui.openBanner({ id: 'top-banner', position: 'top' }); else $.ui.closeBanner('top-banner'); }} />
					          </Card>
					        </Grid>
					        <Text text={'Current preset: ' + read($, activePreset)} dim={true} />
					      </Box>
					    );
					  });
					};
					""",
					"""
					import { test, expect, mod } from 'voidstrap/testing';

					test('renders theme customizer pane and switches preset', () => {
					  mod.emit('session.start', { source: 'startup' });
					  const tree = mod.render('theme');
					  expect(tree).toBeTruthy();
					  mod.press(tree, 'preset-cyber');
					  const updated = mod.render('theme');
					  expect(JSON.stringify(updated)).toContain('Midnight Cyan');
					});
					"""
				);

			case "sidebar-manager":
				return (
					$$$"""
					import type { Register } from 'voidstrap';

					const selectedKey = atom('selectedKey', 'HomeNavItem');
					const customLabel = atom('customLabel', '');
					const customIcon = atom('customIcon', 'Sparkle24');

					export const register: Register = (on) => {
					  on('session.start', ($) => {
					    $.ui.open({ id: 'sidebar', title: '{{{title}}}' });
					  });

					  on('ui.render', { component: 'Pane', requestId: 'sidebar' }, ($) => {
					    const { Box, Grid, Card, Text, Button, Select, TextInput, Toggle } = $.ui.resolve();
					    const layout = $.sidebar.get();
					    const currentKey = read($, selectedKey);
					    const options = layout.items.map(item => ({ value: item.key, label: item.name + ' (' + item.section + ')' }));
					    return (
					      <Box gap={10}>
					        <Grid columns={2} gap={10}>
					          <Card title="Rename & Change Icon" subtitle="Customize any Voidstrap sidebar item">
					            <Select key="item-select" options={options} selected={currentKey} onChange={val => $.state.set('selectedKey', val)} />
					            <TextInput key="label-input" value={read($, customLabel)} placeholder="New sidebar label..." onChange={val => $.state.set('customLabel', val)} />
					            <Select key="icon-select" options={['Home24', 'Sparkle24', 'Rocket24', 'Star24', 'Heart24', 'Flash24', 'Shield24', 'Code24', 'Games24']} selected={read($, customIcon)} onChange={val => $.state.set('customIcon', val)} />
					            <Box direction="row" gap={6}>
					              <Button key="apply-item" label="Apply to Sidebar" variant="primary" icon="Checkmark24" onPress={() => {
					                const lbl = read($, customLabel);
					                const ico = read($, customIcon);
					                $.sidebar.set({
					                  names: lbl ? { [currentKey]: lbl } : {},
					                  icons: ico ? { [currentKey]: ico } : {}
					                });
					              }} />
					              <Button key="reset-sidebar" label="Reset All" icon="ArrowReset24" onPress={() => $.sidebar.reset()} />
					            </Box>
					          </Card>
					          <Card title="Visibility" subtitle="Show or hide optional sidebar items">
					            {layout.items.filter(i => i.canHide).map(item => (
					              <Toggle
					                key={'hide-' + item.key}
					                label={item.name}
					                checked={!layout.hidden.includes(item.key)}
					                onChange={visible => {
					                  const nextHidden = visible
					                    ? layout.hidden.filter(k => k !== item.key)
					                    : [...layout.hidden, item.key];
					                  $.sidebar.set({ hidden: nextHidden });
					                }}
					              />
					            ))}
					          </Card>
					        </Grid>
					      </Box>
					    );
					  });
					};
					""",
					"""
					import { test, expect, mod } from 'voidstrap/testing';

					test('renders sidebar manager pane', () => {
					  mod.emit('session.start', { source: 'startup' });
					  const tree = mod.render('sidebar');
					  expect(tree).toBeTruthy();
					});
					"""
				);

			case "fastflags":
				return (
					$$$"""
					import type { Register } from 'voidstrap';

					const flagName = atom('flagName', 'DFIntTaskSchedulerTargetFps');
					const flagValue = atom('flagValue', '240');

					export const register: Register = (on) => {
					  on('session.start', ($) => {
					    $.ui.open({ id: 'flags', title: '{{{title}}}' });
					  });

					  on('ui.render', { component: 'Pane', requestId: 'flags' }, ($) => {
					    const { Box, Grid, Card, Text, Button, TextInput, Toggle, Table } = $.ui.resolve();
					    const allFlags = $.fastFlags.getAll();
					    const rows = Object.entries(allFlags).slice(0, 12).map(([k, v]) => [k, String(v)]);
					    return (
					      <Box gap={10}>
					        <Grid columns={2} gap={10}>
					          <Card title="Quick FastFlag Editor" subtitle="Read and write ClientAppSettings flags directly">
					            <TextInput key="flag-key" value={read($, flagName)} placeholder="Flag name..." onChange={v => $.state.set('flagName', v)} />
					            <TextInput key="flag-val" value={read($, flagValue)} placeholder="Flag value..." onChange={v => $.state.set('flagValue', v)} />
					            <Box direction="row" gap={6}>
					              <Button key="set-flag" label="Set Flag" variant="primary" icon="Checkmark24" onPress={() => { $.fastFlags.set(read($, flagName), read($, flagValue)); $.ui.refresh(); }} />
					              <Button key="del-flag" label="Remove Flag" icon="Delete24" onPress={() => { $.fastFlags.remove(read($, flagName)); $.ui.refresh(); }} />
					            </Box>
					          </Card>
					          <Card title="Quick App Settings" subtitle="Toggle live Voidstrap preferences">
					            <Toggle key="rpc-toggle" label="Discord Rich Presence" checked={Boolean($.settings.get('UseDiscordRichPresence'))} onChange={v => { $.settings.set('UseDiscordRichPresence', v); $.ui.refresh(); }} />
					            <Toggle key="server-details" label="Show Server Details" checked={Boolean($.settings.get('ShowServerDetails'))} onChange={v => { $.settings.set('ShowServerDetails', v); $.ui.refresh(); }} />
					          </Card>
					        </Grid>
					        <Card title="Active FastFlags Preview" subtitle={'Showing ' + rows.length + ' of ' + Object.keys(allFlags).length + ' configured flags'}>
					          <Table columns={['FastFlag', 'Value']} rows={rows} />
					        </Card>
					      </Box>
					    );
					  });
					};
					""",
					"""
					import { test, expect, mod } from 'voidstrap/testing';

					test('renders fastflags controller pane', () => {
					  mod.emit('session.start', { source: 'startup' });
					  const tree = mod.render('flags');
					  expect(tree).toBeTruthy();
					});
					"""
				);

			case "game-hooks":
				return (
					$$"""
					import type { Register } from 'voidstrap';

					const joins = atom('joins', 0);
					const lastPlace = atom('lastPlace', 0);
					const launchPlace = atom('launchPlace', '1818');

					export const register: Register = (on) => {
					  on('session.start', ($) => {
					    $.ui.open({ id: 'session', title: '{{title}}' });
					  });

					  on('game.join', ($, e) => {
					    update($, joins, n => n + 1);
					    $.state.set('lastPlace', e.placeId);
					    $.ui.status('In place ' + e.placeId);
					  });

					  on('game.leave', ($) => {
					    $.ui.status('Idle');
					  });

					  on('ui.render', { component: 'Pane', requestId: 'session' }, ($) => {
					    const { Box, Grid, Card, Text, Badge, Button, TextInput } = $.ui.resolve();
					    const status = $.roblox.status();
					    return (
					      <Box gap={10}>
					        <Grid columns={2} gap={10}>
					          <Card title="Session Activity" subtitle="Live Roblox status and game join counter">
					            <Box direction="row" gap={6}>
					              <Badge text={status.isRunning ? 'Roblox Running' : 'Roblox Idle'} variant={status.isRunning ? 'success' : 'neutral'} />
					              <Badge text={'Games Joined: ' + read($, joins)} variant="primary" />
					            </Box>
					            <Text text={'Last Place ID: ' + (read($, lastPlace) || 'None')} dim={true} />
					          </Card>
					          <Card title="Quick Place Launcher" subtitle="Launch any Roblox place directly through Voidstrap">
					            <TextInput key="place-input" value={read($, launchPlace)} placeholder="Enter Place ID..." onChange={v => $.state.set('launchPlace', v)} />
					            <Button key="launch-btn" label="Launch Place" variant="primary" icon="Play24" onPress={() => $.roblox.launch({ mode: 'player', placeId: Number(read($, launchPlace)) || 0 })} />
					          </Card>
					        </Grid>
					      </Box>
					    );
					  });
					};
					""",
					"""
					import { test, expect, mod } from 'voidstrap/testing';

					test('tracks game joins in session pane', () => {
					  mod.emit('session.start', { source: 'startup' });
					  mod.emit('game.join', { placeId: 1818, universeId: 13058, jobId: 'test-job' });
					  const tree = mod.render('session');
					  expect(JSON.stringify(tree)).toContain('Games Joined: 1');
					  expect(JSON.stringify(tree)).toContain('1818');
					});
					"""
				);

			case "blank":
				return (
					$$"""
					import type { Register } from 'voidstrap';

					export const register: Register = (on) => {
					  on('session.start', ($) => {
					    $.ui.open({ id: 'main', title: '{{title}}' });
					  });

					  on('ui.render', { component: 'Pane', requestId: 'main' }, ($) => {
					    const { Box, Text } = $.ui.resolve();
					    return (
					      <Box>
					        <Text text="{{title}} is running." bold={true} />
					      </Box>
					    );
					  });
					};
					""",
					"""
					import { test, expect, mod } from 'voidstrap/testing';

					test('renders main pane', () => {
					  mod.emit('session.start', { source: 'startup' });
					  const tree = mod.render('main');
					  expect(tree).toBeTruthy();
					});
					"""
				);

			default:
				return (
					$$"""
					import type { Register } from 'voidstrap';

					const count = atom('count', 0);

					export const register: Register = (on) => {
					  on('session.start', ($) => {
					    $.ui.open({ id: 'main', title: '{{title}}' });
					  });

					  on('ui.render', { component: 'Pane', requestId: 'main' }, ($) => {
					    const { Box, Grid, Card, Text, Badge, Button, Toggle } = $.ui.resolve();
					    const value = read($, count);
					    return (
					      <Box gap={10}>
					        <Grid columns={2} gap={10}>
					          <Card title="Interactive Counter" subtitle="State updates redraw automatically">
					            <Box direction="row" gap={8}>
					              <Badge text={'Count: ' + value} variant="primary" />
					              <Badge text={'Voidstrap ' + $.app.version} variant="neutral" />
					            </Box>
					            <Box direction="row" gap={6}>
					              <Button key="add" label="Increment" variant="primary" icon="Add24" onPress={() => update($, count, n => n + 1)} />
					              <Button key="reset" label="Reset" icon="ArrowReset24" onPress={() => $.state.set('count', 0)} />
					            </Box>
					          </Card>
					          <Card title="Quick Navigation" subtitle="Jump to any page or open a dialog">
					            <Box direction="row" gap={6} wrap={true}>
					              <Button key="nav-home" label="Home" icon="Home24" onPress={() => $.nav.open('Home')} />
					              <Button key="nav-appearance" label="Appearance" icon="Color24" onPress={() => $.nav.open('Appearance')} />
					              <Button key="nav-flags" label="FastFlags" icon="Flag24" onPress={() => $.nav.open('FastFlagSettings')} />
					            </Box>
					          </Card>
					        </Grid>
					      </Box>
					    );
					  });
					};
					""",
					"""
					import { test, expect, mod } from 'voidstrap/testing';

					test('increments counter when button is pressed', () => {
					  mod.emit('session.start', { source: 'startup' });
					  const tree = mod.render('main');
					  expect(tree).toBeTruthy();
					  mod.press(tree, 'add');
					  const after = mod.render('main');
					  expect(JSON.stringify(after)).toContain('Count: 1');
					});
					"""
				);
		}
	}

	private static (string Label, string Code)[] GetSnippets() =>
	[
		(
			"Add Custom Sidebar Page ($.sidebar.addPage)",
			"""
			$.sidebar.addPage({
			  id: 'custom-page',
			  title: 'My Page',
			  icon: 'Sparkle24',
			  section: 'Core'
			});

			on('ui.render', { component: 'Page', requestId: 'custom-page' }, ($) => {
			  const { Box, Card, Text, Button } = $.ui.resolve();
			  return (
			    <Box gap={10}>
			      <Card title="Custom Sidebar Page" subtitle="Full page rendered inside Voidstrap">
			        <Text text="Hello from a custom sidebar page!" bold={true} />
			        <Button key="back-mods" label="Back to Voidstrap Mods" variant="primary" onPress={() => $.nav.open('VoidstrapMods')} />
			      </Card>
			    </Box>
			  );
			});
			"""
		),
		(
			"Open Dashboard Pane ($.ui.open)",
			"""
			$.ui.open({ id: 'my-pane', title: 'My Custom Pane' });

			on('ui.render', { component: 'Pane', requestId: 'my-pane' }, ($) => {
			  const { Box, Card, Text, Button } = $.ui.resolve();
			  return (
			    <Card title="Custom Pane" subtitle="Rendered on Voidstrap Mods page">
			      <Text text="Ready!" />
			      <Button key="toast-btn" label="Show Notification" variant="primary" onPress={() => $.ui.toast('Hello', 'Notification from mod')} />
			    </Card>
			  );
			});
			"""
		),
		(
			"Add Top or Bottom Window Banner ($.ui.openBanner)",
			"""
			$.ui.openBanner({ id: 'status-banner', position: 'top' });

			on('ui.render', { component: 'Banner', requestId: 'status-banner' }, ($) => {
			  const { Box, Badge, Text, Button } = $.ui.resolve();
			  return (
			    <Box direction="row" gap={10}>
			      <Badge text="ACTIVE" variant="success" />
			      <Text text="Custom banner displayed across Voidstrap" bold={true} />
			      <Button key="close-banner" label="Dismiss" onPress={() => $.ui.closeBanner('status-banner')} />
			    </Box>
			  );
			});
			"""
		),
		(
			"Open Interactive Modal Dialog ($.ui.openDialog)",
			"""
			on('ui.render', { component: 'Dialog', requestId: 'settings-modal' }, ($) => {
			  const { Box, Card, Text, Button } = $.ui.resolve();
			  return (
			    <Box gap={10}>
			      <Card title="Custom Modal Dialog" subtitle="Rendered inside a dedicated Voidstrap window">
			        <Text text="You can place any UI controls inside a modal dialog." />
			        <Button key="close-dlg" label="Close Dialog" variant="primary" onPress={() => $.ui.closeDialog('settings-modal')} />
			      </Card>
			    </Box>
			  );
			});

			$.ui.openDialog({ id: 'settings-modal', title: 'Mod Settings', width: 480, height: 320 });
			"""
		),
		(
			"Override App Theme & WPF Brushes ($.theme.setBrush)",
			"""
			$.theme.set({ theme: 'Dark', backdrop: 'Aero', uiZoomPercent: 100 });
			$.theme.setBrush('WindowBackgroundColorPrimary', '#0B1120');
			$.theme.setBrush('CardBackgroundFillColorDefaultBrush', '#162032');
			$.theme.setBrush('AccentFillColorDefaultBrush', '#38BDF8');
			"""
		),
		(
			"Customize Sidebar Names, Icons & Visibility ($.sidebar.set)",
			"""
			$.sidebar.set({
			  names: { HomeNavItem: 'Dashboard', ModsNavItem: 'Tweaks' },
			  icons: { HomeNavItem: 'Rocket24', ModsNavItem: 'Sparkle24' },
			  hidden: ['NewsNavItem']
			});
			"""
		),
		(
			"Read & Write Voidstrap Settings ($.settings)",
			"""
			const rpcEnabled = $.settings.get('UseDiscordRichPresence');
			$.settings.set('UseDiscordRichPresence', !rpcEnabled);
			"""
		),
		(
			"Read & Write FastFlags ($.fastFlags)",
			"""
			$.fastFlags.set('DFIntTaskSchedulerTargetFps', '240');
			const currentFps = $.fastFlags.get('DFIntTaskSchedulerTargetFps');
			$.log('Target FPS flag is now ' + currentFps);
			"""
		),
		(
			"Navigate MainWindow Page ($.nav.open)",
			"""
			$.nav.open('Appearance');
			"""
		),
		(
			"Control MainWindow Title & Dimensions ($.window.set)",
			"""
			$.window.set({
			  title: 'Voidstrap Custom Edition',
			  width: 1200,
			  height: 760
			});
			"""
		),
		(
			"Intercept Roblox Launch (on('roblox.launch'))",
			"""
			on('roblox.launch', ($, e, next) => {
			  $.log('Launching Roblox ' + e.mode + ' with args: ' + e.args);
			  return next(e);
			});
			"""
		),
		(
			"React to Game Join & Leave (on('game.join'))",
			"""
			on('game.join', ($, e) => {
			  $.ui.toast('Joined Game', 'Place ID: ' + e.placeId);
			});

			on('game.leave', ($, e) => {
			  $.ui.status('Left Place ' + e.placeId);
			});
			"""
		),
		(
			"Repeating Clock Timer ($.clock.every)",
			"""
			const timerId = $.clock.every(1000, () => {
			  $.ui.status('Updated at ' + new Date($.clock.now()).toLocaleTimeString());
			});
			"""
		),
		(
			"HTTPS Fetch Request ($.net.fetch)",
			"""
			const response = await $.net.fetch({ url: 'https://apis.roblox.com/universes/v1/places/1818/universe' });
			if (response.ok) {
			  $.log(response.text);
			}
			"""
		)
	];

	private static string BuildApiReferenceText() => """
		VOIDSTRAP MODS FULL APPLICATION API REFERENCE

		1. LIFECYCLE & APP HOOKS: on(event, matcher?, ($, e, next) => ...)
		   * 'session.start'    e: { source: 'startup' | 'reload' }
		   * 'session.end'      e: { reason: 'exit' }
		   * 'roblox.launch'    e: { mode: 'player' | 'studio', args: string } returns { ...e, cancel?: true }
		   * 'game.join'        e: { placeId: number, universeId: number, jobId: string }
		   * 'game.leave'       e: { placeId: number, universeId: number, jobId: string }
		   * 'page.navigate'    e: { page: string }
		   * 'settings.changed' e: { name: string, plugin: string }
		   * 'ui.render'        e: { component: 'Pane' | 'Page' | 'Banner' | 'Dialog', requestId: string, plugin: string }

		2. UI SURFACES ($.ui)
		   * $.ui.resolve()                                  Returns all 25 UI element constructors
		   * $.ui.open({ id, title }) / $.ui.close(id)       Open or close a pane on the Voidstrap Mods page
		   * $.ui.openBanner({ id, position: 'top'|'bottom'}) Inject a live banner at the top or bottom of MainWindow
		   * $.ui.closeBanner(id)                            Remove a MainWindow banner
		   * $.ui.openDialog({ id, title, width, height })   Open a custom interactive modal window
		   * $.ui.closeDialog(id)                            Close a custom modal window
		   * $.ui.message(text, title?)                      Show a native info dialog
		   * $.ui.confirm(text, title?)                      Show a Yes/No confirmation prompt (returns boolean)
		   * $.ui.status(text) / $.ui.toast(title, text)     Status line and notification toast
		   * $.ui.refresh()                                  Redraw all surfaces for this mod

		3. SIDEBAR & CUSTOM PAGES ($.sidebar & $.nav)
		   * $.sidebar.addPage({ id, title, icon, section }) Add a custom page to Voidstrap's sidebar ('Core'|'Configuration'|'More'|'Footer')
		   * $.sidebar.removePage(id)                        Remove a custom sidebar page
		   * $.sidebar.get()                                 List all sidebar items, custom pages, order, hidden, names and icons
		   * $.sidebar.set({ order, hidden, names, icons })  Reorder, hide, rename or change icons on any sidebar item
		   * $.sidebar.reset()                               Restore default sidebar layout, names and icons
		   * $.nav.current() / $.nav.pages()                 Inspect current page or list all pages
		   * $.nav.open(page)                                Navigate MainWindow to any built in page or 'mod:<plugin>:<id>'

		4. LIVE WPF THEME, BRUSHES & WINDOW ($.theme & $.window)
		   * $.theme.get()                                   Read current theme, backdrop, zoom, background and brush overrides
		   * $.theme.set({ theme, backdrop, uiZoomPercent, accentColor, backgroundFilePath, gradientOpacity, blackOverlayOpacity })
		   * $.theme.setBrush(resourceKey, '#RRGGBB')        Override any WPF brush or color resource live across Voidstrap
		   * $.theme.getBrush(resourceKey) / $.theme.listBrushes() / $.theme.resetBrushes()
		   * $.window.get() / $.window.set({ title, width, height, left, top, state, topMost, opacity })

		5. SETTINGS, FASTFLAGS, ROBLOX & CLIPBOARD
		   * $.settings.list() / $.settings.get(name) / $.settings.set(name, value)
		   * $.fastFlags.getAll() / $.fastFlags.get(name) / $.fastFlags.set(name, value) / $.fastFlags.remove(name) / $.fastFlags.clear()
		   * $.roblox.status() returns { isRunning, pid }
		   * $.roblox.launch({ mode: 'player' | 'studio', placeId?: number, jobId?: string })
		   * $.clipboard.get() / $.clipboard.set(text)
		   * $.store.get(key) / $.store.set(key, value)      Persistent 1 MB JSON disk store per mod
		   * $.state.get(key) / $.state.set(key, value)      In memory reactive session state (or atom/read/update)

		6. UI COMPONENTS (from const { ... } = $.ui.resolve())
		   Layout:   Box, Stack, Wrap, Grid (columns, rows, gap), Scroll (maxHeight), Card (title, subtitle), Expander (title, expanded), Tabs, Table (columns, rows), Divider, Spacer
		   Content:  Text (text, bold, italic, mono, size, dim, color), Badge (text, variant), Icon (symbol, size, color), ColorSwatch (color, label, onPress), Image (src, height, width), ProgressBar (value, max), InfoBar (title, message, severity)
		   Controls: Button (label, icon, variant, onPress), Toggle (label, checked, onChange), CheckBox (label, checked, onChange), TextInput (value, placeholder, multiline, onChange, onSubmit), Slider / NumberInput (label, value, min, max, step, onChange), Select (options, selected, onChange)
		   Style props on any element: padding, margin, width, height, minWidth, maxWidth, opacity, background, foreground, borderBrush, borderThickness, cornerRadius, align, tooltip, visible.
		""";
}
