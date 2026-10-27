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
	[GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
	private static partial Regex ModNameRegex { get; }

	public VoidstrapModsPage()
	{
		InitializeComponent();
		PopulateTemplates();
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
			if (mod.Consent == "on" && !mod.Loaded)
				text.Children.Add(PluginUiRenderer.MakeText(Strings.Ui_VoidstrapMods_NotLoaded, new JsonObject()));
			header.Children.Add(text);

			WrapPanel actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
			Wpf.Ui.Controls.Button editBtn = new()
			{
				Content = Strings.Ui_VoidstrapMods_EditButton,
				Icon = SymbolRegular.Code24,
				Appearance = ControlAppearance.Secondary,
				Margin = new Thickness(0, 0, 6, 0),
				Tag = mod.Name
			};
			editBtn.Click += EditMod_Click;
			actions.Children.Add(editBtn);

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
		_templateDescription = defaultDesc;
	}

	private string _templateDescription = "";

	private static bool IsSuggestedName(string text) => text.StartsWith("custom-sidebar-page", StringComparison.Ordinal)
		|| text.StartsWith("theme-studio-pro", StringComparison.Ordinal)
		|| text.StartsWith("sidebar-controller", StringComparison.Ordinal)
		|| text.StartsWith("fastflag-profiles", StringComparison.Ordinal)
		|| text.StartsWith("session-tracker", StringComparison.Ordinal)
		|| text.StartsWith("my-custom-mod", StringComparison.Ordinal)
		|| text.StartsWith("control-center", StringComparison.Ordinal);

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
		string version = "0.1.0";
		string description = _templateDescription;
		string[] hosts = [];
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
		}
		catch (Exception ex)
		{
			CreateModErrorText.Text = ex.Message;
			CreateModErrorText.Visibility = Visibility.Visible;
		}
	}

	private void EditMod_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string modName })
			OpenModInEditor(modName);
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
		if (Directory.Exists(Path.Combine(Paths.Plugins, modName)))
			new Voidstrap.UI.Elements.Editor.VoidstrapModEditorWindow(modName).ShowOwnedDialog();
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
}
