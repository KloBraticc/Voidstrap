using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Voidstrap.Integrations.Plugins;

public static partial class ModFiles
{
	public const string IconFile = "icon.ico";

	public const string PackageExtension = ".vsmod";

	private const long MaxPackageBytes = 20L * 1024 * 1024;

	private const long MaxUnpackedBytes = 60L * 1024 * 1024;

	private const int MaxPackageEntries = 500;

	private const long MaxIconBytes = 1024 * 1024;

	[GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
	private static partial Regex ModNameRegex { get; }

	public sealed record Template(string Key, string DefaultName, string Description);

	public static readonly IReadOnlyList<Template> Templates =
	[
		new("blank", "my-mod", "A minimal Voidstrap mod with one pane"),
		new("dashboard", "control-center", "Interactive Voidstrap control center pane"),
		new("sidebar-page", "custom-sidebar-page", "Adds a custom page directly to the Voidstrap navigation sidebar"),
		new("theme-customizer", "theme-studio", "Customizes Voidstrap theme brushes, zoom and banners live"),
		new("sidebar-manager", "sidebar-controller", "Renames, hides and changes icons on Voidstrap sidebar items"),
		new("fastflags", "fastflag-profiles", "Quick FastFlag and Voidstrap settings controller"),
		new("game-hooks", "session-tracker", "Tracks Roblox game joins and launches places")
	];

	public static bool IsValidName(string name) => ModNameRegex.IsMatch(name);

	public static string NormalizeName(string raw)
	{
		string slug = Regex.Replace((raw ?? "").Trim().ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
		return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
	}

	public static string NextAvailableName(string baseName)
	{
		if (!Directory.Exists(Path.Combine(Paths.Plugins, baseName)))
			return baseName;
		for (int i = 2; i < 1000; i++)
		{
			string candidate = baseName + "-" + i;
			if (!Directory.Exists(Path.Combine(Paths.Plugins, candidate)))
				return candidate;
		}
		return baseName + "-" + Guid.NewGuid().ToString("N")[..6];
	}

	public static void Create(string name, string templateKey, string? iconSource)
	{
		PluginHost.MutationGate.Wait();
		try
		{
			CreateCore(name, templateKey, iconSource);
		}
		finally
		{
			PluginHost.MutationGate.Release();
		}
	}

	private static void CreateCore(string name, string templateKey, string? iconSource)
	{
		Template template = Templates.FirstOrDefault(t => t.Key == templateKey) ?? Templates[0];
		string folder = Path.Combine(Paths.Plugins, name);
		Directory.CreateDirectory(Path.Combine(folder, PluginManifest.ManifestFolder));
		Directory.CreateDirectory(Path.Combine(folder, "hooks"));
		JsonSerializerOptions indented = new() { WriteIndented = true };
		JsonObject manifest = new()
		{
			["name"] = name,
			["version"] = "0.1.0",
			["description"] = template.Description,
			["network"] = new JsonArray(),
			["userConfig"] = new JsonObject()
		};
		File.WriteAllText(Path.Combine(folder, PluginManifest.ManifestFolder, "plugin.json"), manifest.ToJsonString(indented));
		File.WriteAllText(Path.Combine(folder, "hooks", "hooks.json"), new JsonObject { ["modules"] = new JsonArray(JsonValue.Create("./register.tsx")) }.ToJsonString(indented));
		(string register, string test) = BuildTemplateFiles(template.Key, name, template.Description);
		File.WriteAllText(Path.Combine(folder, "hooks", "register.tsx"), register);
		File.WriteAllText(Path.Combine(folder, "pane.test.tsx"), test);
		ModTypes.Write(folder);
		if (!string.IsNullOrWhiteSpace(iconSource))
			SetIcon(name, iconSource);
	}

	public static string? GetIconPath(string modDirectory)
	{
		string path = Path.Combine(modDirectory, IconFile);
		return File.Exists(path) ? path : null;
	}

	public static void SetIcon(string name, string iconSource)
	{
		FileInfo info = new(iconSource);
		if (!info.Exists || !string.Equals(info.Extension, ".ico", StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("the icon must be an .ico file");
		if (info.Length > MaxIconBytes)
			throw new InvalidDataException("the icon must be 1 MB or smaller");
		byte[] bytes = File.ReadAllBytes(info.FullName);
		if (bytes.Length < 6 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0)
			throw new InvalidDataException("the file is not a valid .ico icon");
		File.WriteAllBytes(Path.Combine(Paths.Plugins, name, IconFile), bytes);
	}

	public static void Export(string name, string destination)
	{
		string folder = Path.GetFullPath(Path.Combine(Paths.Plugins, name));
		if (!Directory.Exists(folder))
			throw new DirectoryNotFoundException(name);
		string temp = destination + ".tmp";
		if (File.Exists(temp))
			File.Delete(temp);
		using (ZipArchive zip = ZipFile.Open(temp, ZipArchiveMode.Create))
		{
			foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
					continue;
				string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
				if (relative.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase))
					continue;
				zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
			}
		}
		File.Move(temp, destination, true);
	}

	public sealed record Package(PluginManifest Manifest, string ModFolder, string StagingFolder);

	public static Package OpenPackage(string packagePath)
	{
		FileInfo info = new(packagePath);
		if (!info.Exists)
			throw new FileNotFoundException(Path.GetFileName(packagePath));
		if (info.Length > MaxPackageBytes)
			throw new InvalidDataException("the file is larger than 20 MB");
		string staging = Path.Combine(Paths.Temp, "ModImport", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		try
		{
			string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
			using (ZipArchive zip = ZipFile.OpenRead(info.FullName))
			{
				if (zip.Entries.Count > MaxPackageEntries)
					throw new InvalidDataException("the file holds too many entries");
				long total = 0;
				foreach (ZipArchiveEntry entry in zip.Entries)
				{
					total += entry.Length;
					if (total > MaxUnpackedBytes)
						throw new InvalidDataException("the mod is too large when unpacked");
					string target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
					if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
						throw new InvalidDataException("the file holds an unsafe path: " + entry.FullName);
					if (entry.Name.Length == 0)
					{
						Directory.CreateDirectory(target);
						continue;
					}
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					entry.ExtractToFile(target, false);
				}
			}
			string modFolder = staging;
			if (!File.Exists(Path.Combine(modFolder, PluginManifest.ManifestFolder, "plugin.json")))
			{
				string[] children = Directory.GetDirectories(staging);
				if (children.Length == 1 && Directory.GetFiles(staging).Length == 0)
					modFolder = children[0];
			}
			PluginManifest manifest = PluginManifest.Read(modFolder) ?? throw new InvalidDataException("the file is not a Voidstrap mod, plugin.json is missing");
			return new Package(manifest, modFolder, staging);
		}
		catch
		{
			TryDelete(staging);
			throw;
		}
	}

	public static void Install(Package package)
	{
		PluginHost.MutationGate.Wait();
		try
		{
			InstallCore(package);
		}
		finally
		{
			PluginHost.MutationGate.Release();
		}
	}

	private static void InstallCore(Package package)
	{
		try
		{
			string target = Path.Combine(Paths.Plugins, package.Manifest.Name);
			Directory.CreateDirectory(Paths.Plugins);
			if (Directory.Exists(target))
				Directory.Delete(target, true);
			CopyDirectory(package.ModFolder, target);
			ModTypes.Write(target);
		}
		finally
		{
			TryDelete(package.StagingFolder);
		}
	}

	public static void TryDelete(string folder)
	{
		try
		{
			if (Directory.Exists(folder))
				Directory.Delete(folder, true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (string file in Directory.EnumerateFiles(source))
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
		foreach (string dir in Directory.EnumerateDirectories(source))
			CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
	}

	public static (string Register, string Test) BuildTemplateFiles(string template, string slug, string description)
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

					const flagName = atom('flagName', 'FIntDebugForceMSAASamples');
					const flagValue = atom('flagValue', '4');

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
