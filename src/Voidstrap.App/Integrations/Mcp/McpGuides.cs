using System;
using System.Collections.Generic;
using System.Linq;

namespace Voidstrap.Integrations.Mcp;

public static class McpGuides
{
    public const string BootstrapperTheme = """
		CUSTOM BOOTSTRAPPER (LAUNCHER) THEMES

		WHAT IT IS
		The window Voidstrap shows while it updates and starts Roblox. A theme is a folder in Themes/Custom/<Name>/ with a Theme.xml
		and optional extra files (index.html, style.css, app.js, images, fonts). It is used only when the launcher style is Custom
		AND the theme is the selected one.

		WORKFLOW THAT ALWAYS WORKS
		1. open_editor type "bootstrapper", name "<Name>", activate true. This creates the theme, sets the launcher style to Custom
		   and selects it. Never skip activate unless the user asks.
		2. Write Theme.xml with set_editor_code (no file argument). Write extra files with set_editor_code and a file argument,
		   for example file "index.html". Extra files are written to disk at once.
		3. Read every reply. "The theme builds" means it works, anything else is an error to fix before going on.
		4. save_editor.
		5. test_bootstrapper_theme name "<Name>" to build it exactly like a real launch. Use show true to put it on screen so the
		   user can see it. Fix every reported error.
		6. get_voidstrap_overview and check "Bootstrapper Style: CustomDialog" and "SelectedCustomTheme: <Name>".

		THEME.XML RULES
		Root element: <VoidstrapCustomBootstrapper Version="2" ...>. Version is required (1 or 2).
		Maximum 100 elements. Names must be unique. No namespaces are needed. Attribute values are WPF style strings.
		Paths to files in the theme folder use theme://relative/path (for example theme://images/logo.png). Nothing outside the
		theme folder can be loaded. {Icon} as an Image Source shows the user's chosen Voidstrap icon.

		ROOT ATTRIBUTES
		Width, Height (window size in pixels, required for a good layout, for example 560 and 360), Title, Theme (Default follows
		the app, Dark or Light set the control style), WindowCornerPreference (Default, DoNotRound, Round, RoundSmall), Glass
		(defaults to true, a blurred see through window, set False and a Background for a solid one),
		IgnoreTitleBarInset (true to draw content under the title bar), Background, Foreground, BorderBrush, BorderThickness,
		FontFamily, FontSize, Padding, plus every common attribute below.

		COMMON ATTRIBUTES (every element)
		Name, Visibility (Visible, Hidden, Collapsed), IsEnabled, Margin ("8" or "8,4" or "8,4,8,4"), Width, Height,
		HorizontalAlignment (Left, Center, Right, Stretch), VerticalAlignment (Top, Center, Bottom, Stretch), Opacity (0 to 1),
		OpacityMask (brush), RenderTransformOrigin ("0.5,0.5"), Panel.ZIndex (0 to 1000), Grid.Row, Grid.RowSpan, Grid.Column,
		Grid.ColumnSpan. Child elements <X.RenderTransform> and <X.Effect> take the transforms and effects listed below.

		ELEMENTS
		TitleBar          ShowMinimize, ShowClose, Title. One per theme, top of the window.
		TextBlock         Text, FontSize, FontFamily, Foreground, Background, TextAlignment, TextWrapping, TextTrimming, LineHeight,
		                  Padding. Name="StatusText" makes it show the live launcher status.
		MarkdownTextBlock Text with **bold**, *italic* and [links](https://example.com), same text attributes.
		Button            Content="Cancel" for text, or a <Button.Content> child holding one element. Foreground, Background,
		                  BorderBrush, FontSize, Padding.
		                  Name="CancelButton" makes it the cancel button (enabled only while cancelling is allowed).
		ProgressBar       Value, Maximum, Foreground, Background. Name="PrimaryProgressBar" makes it show live progress.
		ProgressRing      A spinning ring with the same attributes. Name="PrimaryProgressRing" makes it show live progress.
		Image             Source (theme://file, {Icon}), Stretch (None, Fill, Uniform, UniformToFill), IsAnimated (true for GIF).
		Grid              Children placed with Grid.Row and Grid.Column. Define <Grid.RowDefinitions><RowDefinition Height="Auto" />
		                  and <Grid.ColumnDefinitions><ColumnDefinition Width="*" />. Height and Width accept Auto, *, 2*, or pixels.
		StackPanel        Orientation (Vertical, Horizontal).
		Border            Background, BorderBrush, BorderThickness, CornerRadius, Padding. Holds one child.
		Rectangle         RadiusX, RadiusY, Fill, Stroke, StrokeThickness.
		Ellipse           Fill, Stroke, StrokeThickness.
		Line              X1, Y1, X2, Y2, Stroke, StrokeThickness.
		WebPanel          Source (theme://index.html), CornerRadius, CanDrag (true lets the user drag the window from the page),
		                  Background. A full HTML, CSS and JavaScript page, see the web panel section.

		BRUSHES (any Background, Foreground, Fill, Stroke, BorderBrush, OpacityMask)
		A color string ("#FF2B2D31", "#2B2D31", "White"), or a child element:
		<Border.Background><LinearGradientBrush StartPoint="0,0" EndPoint="1,1"><GradientStop Color="#7B5CFF" Offset="0" />
		<GradientStop Color="#2EC5FF" Offset="1" /></LinearGradientBrush></Border.Background>
		SolidColorBrush (Color, Opacity), LinearGradientBrush (StartPoint, EndPoint, SpreadMethod, MappingMode),
		ImageBrush (ImageSource theme://file, Stretch, AlignmentX, AlignmentY, TileMode, Viewport, Viewbox).

		TRANSFORMS AND EFFECTS
		ScaleTransform (ScaleX, ScaleY), RotateTransform (Angle), SkewTransform (AngleX, AngleY), TranslateTransform (X, Y),
		BlurEffect (Radius), DropShadowEffect (BlurRadius, ShadowDepth, Direction, Opacity, Color).

		WEB PANEL (HTML, CSS, JAVASCRIPT)
		Theme.xml stays tiny and holds one WebPanel that fills the window:
		<VoidstrapCustomBootstrapper Version="2" Width="560" Height="360" Theme="Dark" WindowCornerPreference="Round">
		  <TitleBar Title="" ShowMinimize="False" ShowClose="False" />
		  <WebPanel Source="theme://index.html" CanDrag="True" />
		</VoidstrapCustomBootstrapper>
		Inside HTML, CSS and JavaScript use plain relative paths, NOT theme://: <link rel="stylesheet" href="style.css">,
		<script src="app.js"></script>, <img src="images/logo.png">, url("fonts/my.woff2"). theme:// is only for Theme.xml.
		Only files in the theme folder load, there is no internet access (cdn.jsdelivr.net is the one allowed outside host).
		Always style everything yourself: set html and body margin 0, height 100%, a font-family (for example "Segoe UI Variable",
		"Segoe UI", sans-serif), a color, and a background, or the page shows plain black serif text.
		The page gets a ready made object, window.voidstrap, before any script runs. Use exactly these members:
		  voidstrap.status         current status text, such as "Downloading Roblox"
		  voidstrap.progress       current progress value
		  voidstrap.max            progress maximum (percent = progress / max * 100)
		  voidstrap.percent        progress as 0 to 100
		  voidstrap.indeterminate  true while there is no measurable progress, show a spinner or looping bar then
		  voidstrap.cancelEnabled  true while cancelling is allowed, hide or disable your cancel button otherwise
		  voidstrap.accent         the user's Windows accent color, also available in CSS as var(--vs-accent)
		  voidstrap.onUpdate(fn)   calls fn right away and after every change, fn receives { status, progress, max,
		                           indeterminate, cancelEnabled, percent }
		  voidstrap.cancel()       cancels the launch
		  voidstrap.drag()         starts dragging the window
		The same snapshot is also sent as a window "voidstrap" event (event.detail). Do not invent other field names, they do
		not exist. Buttons, links and inputs never start a drag, add data-no-drag to any other element that must not.
		Minimal app.js:
		  voidstrap.onUpdate(function (s) {
		    document.getElementById("status").textContent = s.status || "Starting";
		    var bar = document.getElementById("bar");
		    bar.classList.toggle("loop", s.indeterminate);
		    bar.style.width = s.indeterminate ? "35%" : s.percent + "%";
		    document.getElementById("cancel").hidden = !s.cancelEnabled;
		  });
		  document.getElementById("cancel").onclick = function () { voidstrap.cancel(); };
		Keep the page background transparent or set it yourself, the window has no other background behind a WebPanel.

		NATIVE EXAMPLE (no HTML)
		<VoidstrapCustomBootstrapper Version="2" Width="520" Height="300" Theme="Dark" WindowCornerPreference="Round" Background="#FF111318">
		  <TitleBar Title="" ShowMinimize="False" ShowClose="False" />
		  <Grid Margin="24">
		    <Grid.RowDefinitions><RowDefinition Height="*" /><RowDefinition Height="Auto" /><RowDefinition Height="Auto" /><RowDefinition Height="Auto" /></Grid.RowDefinitions>
		    <Image Source="{Icon}" Width="72" Height="72" HorizontalAlignment="Center" VerticalAlignment="Center" />
		    <TextBlock Name="StatusText" Grid.Row="1" Foreground="White" FontSize="16" TextAlignment="Center" Margin="0,12,0,12" />
		    <ProgressBar Name="PrimaryProgressBar" Grid.Row="2" Height="6" Foreground="#7B5CFF" Background="#22FFFFFF" />
		    <Button Name="CancelButton" Grid.Row="3" Content="Cancel" HorizontalAlignment="Center" Margin="0,16,0,0" />
		  </Grid>
		</VoidstrapCustomBootstrapper>

		COMMON MISTAKES
		Missing Version on the root. Forgetting activate true or open_editor, so the theme exists but is never used.
		Using theme:// inside HTML or CSS (use relative paths). Using http URLs or ../ paths (blocked). Creating a second theme
		instead of editing the open one (check list_editors and reuse its editorId). Guessing JavaScript APIs instead of
		window.voidstrap. More than 100 elements.
		Two elements with the same Name. Not calling test_bootstrapper_theme before telling the user it is done.
		""";

    public const string KeystrokeTheme = """
		KEYSTROKE OVERLAY THEMES

		The on screen key display. A theme is one object written in the editor's current language (csharp, html, rust or json),
		all four describe the same fields. Workflow:
		1. open_editor type "keystroke", name "<Name>", activate true (optionally baseTheme to start from a built in theme).
		2. get_editor_code first and keep its exact shape, only change values. set_editor_language switches between languages
		   without losing anything, html is the easiest to read.
		3. set_editor_code or edit_editor, read the reply, then save_editor.
		Colors are #RRGGBB or #AARRGGBB, or the word Accent for the Windows accent color. The preview in the editor window
		updates after every change. A reply that is not a success means nothing was applied.
		""";

    public const string AppTheme = """
		VOIDSTRAP APP THEME (THE LOOK OF VOIDSTRAP ITSELF)

		manage_app_theme action schema lists every color key with its label and group. Workflow:
		1. action get to read the current colors (baseTheme to start from Dark, Light, Voidstrap, Berry and so on).
		2. action set_colors with only the keys you want to change, and optionally a window gradient:
		   { "colors": { "ApplicationBackgroundColor": "#FF14161C" }, "gradient": { "angle": 135, "stops": [
		     { "color": "#FF241A3A", "offset": 0 }, { "color": "#FF0E1A2B", "offset": 1 } ] } }
		3. It applies to every window at once and switches the app theme to Custom. undo_last_change reverts it.
		4. save_profile keeps it under a name the user can pick later.
		Keep text readable: light text needs dark backgrounds and the reverse.
		""";

    public const string Fixing = """
		FIXING VOIDSTRAP PROBLEMS

		1. diagnose_app first. It lists recent in app errors and crashes and broken state, each problem with a fix id.
		2. For an error you do not understand, get_logs_and_activity with source "voidstrap" and filter set to the error's source
		   name, or read_voidstrap_file on its log path, to read the full stack trace.
		3. fix_app_issue with the fix id (and target when shown). Most fixes can be undone with undo_last_change.
		4. Run diagnose_app again to confirm the problem is gone, and tell the user what changed.
		Never delete user files to fix something when a reset or disable fix exists.
		""";

    public const string Architecture = """
		VOIDSTRAP ARCHITECTURE (THE CODE THAT BUILT VOIDSTRAP)

		WHERE THE CODE LIVES
		manage_app_code lists every file of the exact source that built the running app. The projects:
		  Voidstrap.App            the WPF application. Bootstrapper.cs is the launch pipeline, LaunchHandler.cs routes command line flags,
		                           App.xaml.cs wires startup and shutdown, Watcher.cs is the in game companion process.
		  Voidstrap.Core           platform neutral models, FastFlag definitions, Roblox install data.
		  Voidstrap.Cross          the same App sources compiled for Linux (Sober player, Vinegar Studio).
		  Voidstrap.Desktop.*      per platform hosts (Windows, Linux, macOS) behind Voidstrap.Platform.* abstractions.
		  ClientServer             helper programs for the session browser and web server features.
		Two rules hold everywhere: the same code runs on Windows and Linux unless a file says otherwise, and every feature either
		works completely on Linux or is hidden there.

		THE LAUNCH PIPELINE
		Command line flags land in LaunchHandler.ProcessLaunchArgs: installer, settings, watcher, Roblox launch, Bloxshade, quiet.
		A Roblox launch creates Bootstrapper.Run, which shows the launch dialog, checks the Roblox version, applies modifications in
		this order: ApplySkyboxModifications, custom font, CursorManager.ApplyOnLaunch, FileModManager.ApplyFromSettings (textures
		through a shared folder gate), ModAutoFixer.PrepareModSources (converts sky textures), the mods folder enumeration,
		ModGenerator.RefreshOutdatedAsync, ManagedModStore.ScanEnabledFiles, then a parallel copy of every selected file into the
		Roblox version folder, FastFlags written to ClientAppSettings.json, and the process start. Sober (Linux) and macOS reuse the
		same steps through PrepareSoberLaunchAsync and PrepareMacLaunchAsync with MacModSync applying the bundle.

		THE THREE MOD SYSTEMS
		  1 Roblox content mods. Plain files under Mods/ copied over the Roblox client on launch, plus indexed Managed Mods under
		    ManagedModPackages with a JSON index (ManagedModStore), variant slots (ModVariantStore), a crash guard (ModCrashGuard)
		    that turns off recently changed mods after a Roblox crash, and community installs from GameBanana and the marketplace
		    (Integrations/CommunityMods). All folder writes share FileModManager.FolderGate, so launch and live edits never collide.
		  2 Voidstrap mods. Sandboxed TypeScript or JavaScript mods in Plugins/<name>/ with plugin.json, loaded by Jint through
		    PluginHost. They render panes, banners, widgets, dialogs, sidebar pages and keyboard anchors, react to events
		    (session.start, roblox.launch, cursor.move and more) and get the $ API. PluginHost.MutationGate serializes reloads with
		    folder edits, so a save during a reload heals instead of breaking.
		  3 RiShade and friends. Integrations/RiShade, AntiAliasing and FrameGeneration manage shaders, upscalers and frame
		    generation around the Roblox process, with overlays in Integrations/Overlays.

		KEY SERVICES TO KNOW
		  GlobalBackground            the app wide background (image or video, gradient, shade) driven by BackgroundSettings.json.
		  AppControl                  live text overrides on any window and the background control API used by mods and MCP.
		  AppSourceStore              the embedded copy of the full source tree, with user edits kept as copies under AppCode.
		  TranslationService          optional machine translation of the whole UI with a reverse restore path.
		  JsonManager<T>              atomic JSON settings with deferred saves, external change merge and backup recovery.
		  VoidstrapMatchmaker         picks the closest Roblox server by datacenter geo for the Smart Join chip.
		  Watcher                     keeps overlays, rich presence, key routing and window management alive during a session.
		Everything is wired in App.xaml.cs StartAsync. Logging goes through App.Logger, writes are atomic, and the settings files
		live under Paths.Config. Read the real code any time with manage_app_code, it is always more current than any description.

		""";

    public const string ModApi = """
		VOIDSTRAP MOD $ API REFERENCE (TypeScript or JavaScript)

		register(on) runs once when the mod loads. on('event', matcher?, handler) subscribes. A handler receives $, the event
		payload and next. Call next(e) to pass the event on, return a value to replace it, return a Promise to go async (settled
		up to 10 seconds). Elements are built with h(type, props, ...children), types listed by $.ui.resolve().

		EVENTS
		  session.start            fired on load and after every reload, payload { source }.
		  roblox.launch            fired when a Roblox launch starts, promises are settled before the launch continues.
		  session.end              fired on app exit with { reason }.
		  ui.render                render a pane, page, banner, dialog or widget. Payload { component, requestId, plugin, surface }.
		  cursor.move              cursor moved, throttled to about 16 ms. cursor.down, cursor.up, cursor.enter, cursor.leave.
		  cursor.scroll            mouse wheel, throttled to about 80 ms, payload adds delta.

		$ API
		  $.log(text)                                   write to the Voidstrap log.
		  $.toast(title, text)                          system notification.
		  $.app.version, $.app.platform                 app version and windows or linux.
		  $.app.texts()                                 every active text override as { 'text:Hello': 'Hi' }.
		  $.app.setText(target, text)                   override any UI text. target is 'text:<exact original>' or 'name:<element>'.
		                                                Empty text removes the override. Persists across restarts.
		  $.app.clearText(target)                       restore one original text.
		  $.app.background()                            current background state { backgroundFilePath, gradientOpacity, blackOverlayOpacity, displayEverywhere }.
		  $.app.setBackground(request)                  change the whole application background. request is a file path string or
		                                                { file, gradientOpacity, blackOverlayOpacity, displayEverywhere }.
		  $.app.clearBackground()                       back to the default look.
		  $.ui.open({ id, title }) and close(id)        panes shown in the main window.
		  $.ui.openBanner / closeBanner                 banners above or below the content.
		  $.ui.attach({ id, anchor, placement })        widget attached to a real element, $.ui.anchors() lists live anchors.
		  $.ui.style(preset or roles)                   theme every window in one call: $.ui.style('glass'). Use this first for themes.
		  $.ui.windows / listen / press                 list windows, get ui.event on clicks, click a button in any window.
		  $.ui.tree / query                             read the real UI structure of any window.
		  $.ui.rule / move / setResources / redesign    restyle, rearrange and repaint any window, read guide mod_ui.
		  $.ui.resources / undo / reset / changes       list theme resources, remove changes, read back what is active.
		  $.ui.openDialog / closeDialog / message / confirm
		  $.ui.status(text)                             status line text. $.ui.refresh() redraws your surfaces.
		  $.settings.list / get / set                   read and change Voidstrap settings.
		  $.fastFlags.getAll / get / set / remove / clear
		  $.store.get / set(key, value)                 per mod persistent storage.
		  $.clock.after(ms, fn), $.clock.every, cancel  timers, 500 ms minimum for repeat.
		  $.net.fetch({ url, method, body, headers })   https only, hosts must be listed in plugin.json network. Promise based.
		  $.roblox.launch(request)                      start a place. $.roblox.request needs the roblox-account permission.
		  $.clipboard.set / get                         clipboard access.
		  $.cursor.get()                                live cursor state { ready, x, y, screenX, screenY, windowWidth, windowHeight,
		                                                insideWindow, isDown, button, windowTitle }. ready is false until the first
		                                                mouse event, request it from a cursor.move hook for real values.
		  $.sidebar.addPage / removePage / set / get    sidebar pages.
		  $.modpages.addPage / removePage               pages inside the Voidstrap Mods tab.
		  $.nav.open / pages / current                  navigation.
		  $.theme.set({ backgroundFilePath, gradientOpacity, blackOverlayOpacity, displayEverywhere, accentColor, zoom, ... })
		  $.window.get / set                            main window geometry and state.

		LIMITS AND GUARDS
		  Hooks run on a single engine per mod with a 2 second CPU budget and 64 MB memory cap. At most 32 timers, 32 attached
		  widgets and a 10 second promise wait. The UI needs approval before a mod loads, revoke it any time from the Mods tab.
		  The sandbox has no filesystem, no process and no shell access. A mod that throws is reported in the Mods tab problems
		  list and skipped, it never takes Voidstrap down.

		""";

    public const string ModUi = """
		REDESIGNING THE VOIDSTRAP INTERFACE FROM A MOD

		A mod can restyle, hide, resize, rearrange, move and click any element of any open Voidstrap window, and override any theme
		resource. Every change is declarative, re-applied when pages load, and undone automatically when the mod is reloaded,
		turned off or deleted.

		THE SHORT WAY: THEME THE WHOLE APP IN ONE LINE
		  Do this first for any theme request. Do not write long lists of resources or rules for a theme.
		    on('session.start', $ => { $.ui.style('glass'); });
		  Presets: glass, midnight, light. Add only what should differ:
		    $.ui.style({ preset: 'glass', accent: '#FF2D55', radius: '20' });
		  Roles: background, surface, border, text, muted, accent, radius, backdrop. Colors are #RRGGBB or #AARRGGBB, lower alpha is more see through.
		  It repaints every Voidstrap window. backdrop is a saved setting, Mica, Aero or Acrylic; blur depends on the system and on Linux may stay flat,
		  so check the result. Add rules afterwards only for details style cannot reach.

		EVERY WINDOW
		  Rules, moves, press and listen reach every open Voidstrap window, also ones that open later. Add window to a target to limit it to one
		  window by class name or title: { window: 'KeystrokeThemeEditorWindow', type: 'Button' }. $.ui.windows() lists open windows.
		  Open the window first to read it: $.ui.tree({ window: 'MusicPlayer' }). $.ui.attach also finds anchors in other windows.

		ADDING REAL FEATURES
		  A mod is code with state, timers, network, settings and its own UI. Combine these:
		    Own UI: $.sidebar.addPage, $.ui.open, $.ui.openDialog (its own window), $.ui.attach into any existing panel.
		    React to the existing UI: $.ui.listen({ id: 'save', target: { name: 'SaveButton' } }), then on('ui.event', { id: 'save' }, ($, e) => { ... }).
		    Drive the existing UI: $.ui.press({ name: 'SomeButton' }) clicks a button or toggle as a user would.
		    Also: page.navigate, game.join, game.leave, roblox.launch, settings.changed events, $.net.fetch, $.store, $.clock.
		  Mods stay sandboxed: no files, processes, or direct .NET. Only clicks are reported, never typed text.

		POWERFUL MODS: REAL APP CODE, FILES AND WEB WINDOWS
		  List what is needed under permissions in plugin.json: native, files, web. Voidstrap shows a strong warning and the user must
		  approve this exact version. ANY change to any file of the mod voids the approval and asks again, including updates.
		  Until approved nothing works and the mod stays off. Every native type used and file written is logged.

		  native: $.native.type('System.Windows.Controls.Button') returns a real .NET type to construct or call. Touch UI only inside
		  $.native.ui(() => { ... }), which runs on the UI thread, limit 1.5 seconds to start. Mods use WPF, Wpf.Ui and Voidstrap.UI, Models,
		  Enums and Resources. Always blocked: files, processes, registry, network, reflection, assembly loading, XAML loading, data providers,
		  dispatchers, clipboard, and anything named for accounts, cookies, tokens, passwords, secrets, logins, install or download. Undo is the mod's job
		  for what it builds natively: restore it on session.end, it is not journaled.

		  files: $.files reads and writes only in <install>/ModFiles/<mod>, deleted along with the mod. No other folder, no links, no ".." and no
		  absolute paths. Only data, text, media and font types. Executables, scripts such as .bat or .ps1, and archives can never be stored, and
		  program headers are checked inside any file. 16 MB per file, 256 MB and 4096 files per mod. There is no way to run anything.

		  web: $.web.open({ url, title, width, height }) opens a WebView2 window, Windows only, returns ok false elsewhere. https only, hosts must be under
		  network in plugin.json, never roblox.com. Own browser profile per mod, so no Roblox login. Downloads, popups, permissions, dev tools,
		  host objects and web messages are all off.

		  Other libraries: bundle JavaScript libraries as mod modules. Loading .NET or native DLLs is never allowed.

		THE WORKFLOW
		  1. Look before you change. manage_voidstrap_mods action ui_tree, or $.ui.tree(), returns the real structure.
		     Arguments: root (a target), depth (default 8, up to 40), namedOnly. Open the page you care about first, because
		     pages only exist in the tree while they are shown.
		  2. Apply everything from session.start, so it returns after each reload and restart:
		       export default {
		         register(on) {
		           on('session.start', $ => {
		             const result = $.ui.redesign({ replace: true, resources: {...}, moves: [...], rules: [...] });
		             if (!result.ok) $.log(result.errors.join(', '));
		           });
		         }
		       };
		  3. Read the result. { ok, matched, errors }. A "matched nothing yet" entry is normal for an element on a page that is
		     not open, the rule applies when the page appears. Any other error names what was refused.
		  4. Use action reload, then action ui_tree and action problems to confirm the outcome. action test runs in a sandbox
		     where these calls return empty results, so it cannot prove a visual change.

		TARGETS
		  A target is an object. All fields you give must match.
		    name          exact x:Name. A trailing * matches a prefix: 'TopNav*'.
		    type          control class or any base class: 'Button', 'TextBlock', 'Panel'.
		    text          exact visible text. Text changes with the language, so prefer name.
		    textContains  case insensitive part of the text.
		    parent        name of the direct visual parent.
		    ancestor      name of any visual ancestor. Use it to scope type: { type: 'Button', ancestor: 'TopBar' }.
		    page          only while this page class is open: 'FastFlagsPage'. page.navigate reports the current page.
		    index         keep only the nth match, from 0.
		    all           true allows a target with no other filter, useful with page.
		  The window itself can never be a target.

		$.ui.rule({ id, target, set })
		  Sets properties on every match, now and on every element that appears later. Reusing an id replaces that rule.
		  Any writable WPF dependency property of the element works, in camelCase or PascalCase, through its normal text format:
		    opacity: 0.5                     width: 240, height: 40, minWidth, maxWidth, minHeight, maxHeight
		    margin: '8,4,8,4'                padding: '12,6'
		    background: '#202028'            foreground: '#FFFFFF', borderBrush: '#40FFFFFF', borderThickness: 1
		    cornerRadius: 12                 fontSize: 15, fontWeight: 'SemiBold', fontFamily: 'Segoe UI'
		    horizontalAlignment: 'Right'     verticalAlignment: 'Center'
		    orientation: 'Horizontal'        toolTip: 'Text', content: 'Text', text: 'Text', isEnabled: false
		    isHitTestVisible: false          clipToBounds: true, cursor: 'Hand'
		  Layout in the parent panel: 'Grid.Row', 'Grid.Column', 'Grid.RowSpan', 'Grid.ColumnSpan', 'DockPanel.Dock', 'Panel.ZIndex',
		    'Canvas.Left', 'Canvas.Top', 'Canvas.Right', 'Canvas.Bottom'.
		  Shortcuts: hidden: true collapses the element. rotate, scale, scaleX, scaleY, translateX, translateY set a render transform.
		  Theme link: 'theme:KeyName' as a value follows that resource live: background: 'theme:SystemAccentBrush'.
		  Refused on purpose: DataContext, Template, Style, ItemsSource, ItemTemplate, ContentTemplate, Command and
		  CommandParameter, and any property two way bound to a setting. Writing those would break features or change saved settings.
		  Visible text must not be hardcoded when it replaces a translated string for everyone; a personal mod may do as it likes.

		$.ui.move({ id, target, into, index })
		  Moves the first match into the first Panel matching into, at index, or at the end. The element must sit directly in a
		  Panel, Border or ContentControl. Panels generated from a list cannot be the destination. Elements of a list, such as
		  sidebar entries, are rearranged with $.sidebar.set instead. Combine it with a rule for 'Grid.Row' or 'DockPanel.Dock'.
		  Moves run before rules on each pass.

		$.ui.resources() and $.ui.setResources({ KeyName: value })
		  Lists colors, brushes, thicknesses, corner radii and numbers, and overrides them for every window. Every control using
		  that key through DynamicResource changes at once, which is the fastest way to repaint the app. An empty value removes the
		  override. Values use the type of the existing resource, so an unknown key is refused. Controls using StaticResource keep their color; target those with a rule.

		$.ui.redesign({ replace, resources, moves, rules })
		  Applies a whole design in one call. replace: true forgets this mod's earlier changes first. Prefer it for a complete redesign.

		$.ui.undo(id), $.ui.reset(), $.ui.changes()
		  Remove one rule or move, remove everything from this mod, and read back what is active.

		REPLACING WHOLE AREAS
		  Hide the original with a rule, then draw your own with $.ui.attach, panes, banners or $.sidebar.addPage:
		    $.ui.rule({ id: 'hide-top', target: { name: 'TopBarPanel' }, set: { hidden: true } });
		    $.ui.attach({ id: 'my-top', anchor: 'SomePanel', placement: 'start' });
		  Up to 32 attached widgets per mod. Names here are examples, take real ones from ui_tree.

		EXAMPLE: A COMPACT REDESIGN
		  $.ui.redesign({
		    replace: true,
		    resources: { ApplicationBackgroundBrush: '#101018', CardBackgroundFillColorDefaultBrush: '#1A1A26' },
		    rules: [
		      { id: 'cards', target: { type: 'CardControl' }, set: { cornerRadius: 14 } },
		      { id: 'flags-title', target: { page: 'FastFlagsPage', type: 'TextBlock', index: 0 }, set: { fontSize: 26 } },
		      { id: 'quiet-toggle', target: { name: 'SidebarToggle' }, set: { opacity: 0.6, scale: 0.9 } }
		    ]
		  });

		WHAT GETS UNDONE AND WHEN
		  UI rules, moves and resource overrides exist only while the mod runs. They are removed the moment it reloads, turns off or
		  is deleted, and it reapplies them from session.start when it stays on.
		  Saved changes are journaled per mod: $.settings.set, $.fastFlags, $.sidebar.set and reset, $.theme.set, $.app.setText and
		  clearText, $.app.setBackground and clearBackground. Turning the mod off or deleting it puts back the values from before its
		  first change, also when its folder was removed by hand. A value the user changed afterwards is left alone. $.store data is
		  kept when turning off and deleted with the mod.

		PANIC SWITCH
		  Ctrl+Alt+Shift+M in the main window, for every window, suspends every mod UI rule, move and resource override, and pressing it again resumes
		  them. Tell the user about it if a design hides navigation. Suspension lasts until the app closes.

		LIMITS
		  256 rules, 64 moves, 256 listeners and 256 resource overrides per mod. The tree returns at most 1500 nodes. Rules re-apply at most twice a second, and only while a rule exists.
		  The tree never reports the contents of password fields.

		""";

    public const string AppControl = """
		LIVE APP CONTROL (TEXTS AND BACKGROUND)

		WHAT IT IS
		AppControl changes the running Voidstrap interface without touching program files. Text overrides replace any visible
		TextBlock, button or header text. The background control swaps the whole application background for an image or video and
		persists it. Both are exposed to MCP through manage_app_control and to mods through $.app.

		TEXT OVERRIDES
		  Identify a target in one of two ways:
		  'text:<exact original text>' replaces every element whose original text matches exactly.
		  'name:<XAML element name>' replaces that one element everywhere it appears.
		  Find names with manage_app_code action search on x:Name, or with manage_voidstrap_mods action ui_map for live elements.
		  text_set applies to every open window at once and to future windows, and persists in AppControl/TextOverrides.json.
		  text_clear or text_clear_all restores originals. If auto translate is on, overrides sit on top and reapply after it.

		BACKGROUND
		  background_set copies an image (PNG, JPG, WEBP, GIF, BMP, TIFF) or video (MP4, WEBM, MKV, MOV) into the Voidstrap data
		  folder and switches every window to it, optionally with gradientOpacity, blackOverlayOpacity and displayEverywhere.
		  The state lives in BackgroundSettings.json, the same file the Appearance page uses, so both stay in sync.
		  background_clear removes it. Mods can read and change the same state through $.app.background and $.app.setBackground.

		SAFETY
		  Overrides never modify files inside the install, they live in the data folder and are removed by text_clear_all or a
		  factory reset. A bad override can look wrong but cannot crash Voidstrap.

		""";

    public const string AppCode = """
		THE APP CODE AREA (READING AND EDITING VOIDSTRAP ITSELF)

		WHAT IT IS
		Every Voidstrap build carries a compressed copy of its own source tree: every folder, every file, every .cs and .xaml of
		the code that built it. Users browse and edit it inside the app from the Mods tab, App Code button. MCP uses manage_app_code.

		ACTIONS
		  list     walk the tree, path empty for the root, then folder by folder.
		  read     file contents, your edited copy if one exists.
		  write    save an edited copy. The original never changes, the running app never reloads, nothing can crash.
		  reset    drop the edited copy and go back to the original of this build.
		  search   file names and code text at once, returns path, line and snippet.
		  overrides  list every edited file.
		  export   write the whole tree, edits included, into a folder you choose.

		HOW EDITS ACTUALLY RUN
		  An edit is a copy under Data/AppCode/Overrides. It changes nothing while Voidstrap runs. To make it real: export, open
		  the exported folder, build Voidstrap.sln there, and run the build. That build is your edited Voidstrap. Tell the user
		  this every time an edit is saved, a saved copy is not a running change.

		WORKFLOW THAT WORKS
		  1 action search for the feature name or a string the user quoted.
		  2 action read the file, then read related files from the architecture docs (get_docs topic architecture).
		  3 action write the change as complete valid C# or XAML. A file that does not compile builds an app that does not start,
		    so change only what you understand and keep the style of the file.
		  4 Tell the user exactly which files you edited, export, and let them rebuild. undo_last_change reverts the last write.

		""";

    public const string LibraryPr = """
		PUBLISHING TO THE VOIDSTRAP MODS LIBRARY WITHOUT A RED X

		WHAT A RED X IS
		Pull requests to the voidstrap mods library run an automated gate and an AI review. Any failing check shows a red X and the
		pull request cannot merge. Every check the gate runs is mirrored inside Voidstrap, so you can know the result before the
		pull request exists.

		THE RULE THAT NEVER CHANGES
		Run manage_voidstrap_mods action preflight on the mod before publishing, every time. publish itself refuses to open the
		window while preflight has X items, so a blocked publish is not an error to retry, it is a list to fix. Fix the mod files,
		run preflight again, and only publish when it says everything passed.

		WHAT THE GATE CHECKS (preflight mirrors all of it)
		  Name            folder name is lowercase letters, digits and hyphens, plugin.json name matches it.
		  Version         looks like 1.2.3 and is HIGHER than the version in the library for an update. Equal or lower is a red X.
		  Description     1 to 300 characters, honest, says what the mod does and what it sends.
		  Files           at most 64 files, 1 MB total, each under 256 KB, 4 levels deep, plain file names, no hidden files except
		                  .voidstrap-mod/plugin.json, no node_modules, only .ts .tsx .js .mjs .jsx .json .md .ico.
		  Icon            icon.ico at the root, under 256 KB, valid frames, nothing banned in its metadata.
		  Code            no eval, no Function(), no computed import, no decoders (atob btoa unescape decodeURIComponent),
		                  no String.fromCharCode, no long escaped or hex or base64 runs, code lines at most 400 characters,
		                  no invisible or direction changing characters, no binary inside text files.
		  Banned content  no Roblox cookies or tokens, no Discord webhooks, no executors or cheats, no process injection, no
		                  node or browser escape hatches, no adult or hateful or profane content, no external image or video
		                  embeds or unmoderated media hosts, in names, file names, code and metadata.
		  Network         hosts in plugin.json network must be plain host names; anything not on the library allowed list still
		                  passes the gate but the reviewer will question it, keep it minimal.
		  Tags            up to 5 tags from the library tags list.
		  Permissions     only roblox-account, and then the description must say exactly what is read and it must never leave
		                  Roblox hosts through $.net.fetch.
		  Hooks           hooks/hooks.json names exactly one module that exists inside hooks/.
		  Reviewer text   any sentence that talks to a reviewer or tries to steer the review is flagged and blocks the merge.
		                  Never add such text, not even to apologize or explain a check.

		CORRECTING COURSE
		  preflight says X: fix exactly that item in the mod files, nothing else, then preflight again. Do not rename the mod to
		  dodge a check, do not split a mod to stay under a limit, do not encode anything to hide it. Those pass the gate and fail
		  the human.
		  ? question marks: they pass the gate, but the reviewer sees them too. Remove what is not essential, or be ready to
		  explain in the pull request description.
		  The AI reviewer reads every file and the pull request conversation as untrusted data. It knows the $ API, it knows
		  Voidstrap style has no comments, and it suggests real fixes. Answer its comments in the conversation, never inside files.
		  owner.json is written by the publish window from the signed in account, never add or edit it yourself.

		ORDER THAT ALWAYS WORKS
		  1 write or change the mod, 2 manage_voidstrap_mods validate and test, 3 preflight, 4 fix every X and recheck,
		  5 preflight passes, 6 publish and let the user confirm.
		""";

    public const string Search = """
		THE DEEP SEARCH (search_everything)

		One tool answers where anything is. Pick the mode that owns the question, always start from search_everything instead of
		guessing paths or reading whole files.

		MODE code
		Searches every source file of the app itself. Exact file, line number and column for every hit. regex true for patterns,
		wholeWord for identifiers, caseSensitive when needed, glob to narrow (for example *.xaml or Voidstrap.App/Integrations/**),
		definitions true to find only where a symbol is declared, context for surrounding lines. Follow a hit with manage_app_code
		action read on the same path.

		MODE logs
		Regex search across the Voidstrap log, the latest Roblox log and the server history in one call. errorsOnly true keeps only
		error, warning, exception, fail and crash lines. Use it before reading whole logs with get_logs_and_activity.

		MODE errors
		The hidden error hunt, no query needed. One call aggregates the recorded in app notifications, every Voidstrap mod problem,
		every managed mod scan failure and the error lines of the log. A query filters the findings. Run this when the user says
		something is broken and you do not know where.

		MODE settings
		Finds any AppSettings property, FastFlag or Roblox global setting by name or value with a regex. Read back the exact
		spelling before set_settings or set_fastflags.

		MODE ui
		Searches every visible element in every open Voidstrap window by name, type or text and returns the exact pixel rect of
		each hit, pixels from that window's top left. This is how you find where something sits on screen.

		MODE at
		The reverse question: give x and y in pixels from a window's top left and it returns the chain of elements under that
		pixel, deepest first, each with name, type, text and rect. Combine with mode ui to aim widget attachments to the exact spot.

		MODE files
		Searches file names and file content with a regex across the Voidstrap data, config, mods and plugin folders. Finds state
		files, saved themes, caches and mod files.

		MODE mods
		Searches managed mods, voidstrap mods and content mod files at once: names, states, file lists, failures and problems.

		HABITS THAT WORK
		Nothing is found: drop wholeWord, try regex, shorten the query, widen glob or max. Too many hits: raise specificity, add
		glob, lower max. Every result tells you the next tool to use. Verify a finding by reading the file or element before
		acting on it.
		""";

    public const string Workflow = """
		HOW TO WORK WITH VOIDSTRAP OVER MCP

		Voidstrap manages Roblox launching, settings, content mods and its own interface. The MCP bridge controls the running app on Windows, Linux and macOS. Open Voidstrap first, then call get_voidstrap_overview to discover paths, active settings, themes and open windows. Do not assume an installed file is active or a running process has a visible window.

		CHOOSE THE TOOL THAT OWNS THE REQUEST
		Existing app option: get_settings with a filter, then set_settings using the exact returned property names and types. Do not guess names. globalSettings changes Roblox GlobalBasicSettings properties; settings changes Voidstrap options.
		Roblox FastFlags: list_fastflags, search_fastflag_tracker, then set_fastflags. A flag existing in a tracker does not prove the standard Roblox client accepts it locally. Check the current allowlist and tool validation. A successful write does not prove Roblox applied a flag.
		Voidstrap colors or background: manage_app_theme, after get_guide topic app_theme.
		Launcher window shown while Roblox starts: open_editor type bootstrapper, after get_guide topic bootstrapper_theme. Set activate true, save_editor, then test_bootstrapper_theme. The theme must build AND be the selected Custom launcher. HTML in a launcher uses window.voidstrap, not the mod $ API.
		Keystroke overlay appearance: open_editor type keystroke, after get_guide topic keystroke_theme. Read the open document and preserve its shape.
		New Voidstrap widget, cursor sprite, page, panel, sidebar change or automation: manage_voidstrap_mods. Read action guide (also get_guide topic mods), then action list and types. Use a sandboxed mod for these requests, without changing Voidstrap program files. Mods support Windows, Linux and macOS.
		Roblox content replacement: manage_mods and manage_themes_and_mods. These content files apply on the next Roblox launch. They are different from TypeScript Voidstrap mods.
		Existing page or setting on screen: navigate_ui. Read the returned page names and setting locations.
		Roblox launch or join: launch_roblox with mode and optional placeId/jobId. This tool blocks a second running instance. Use its native launch path.
		Saved accounts: list_accounts is read only. Mod account lookups use $.roblox.request with the roblox-account permission. Never read or copy login cookies or tokens.
		Errors: diagnose_app, get_logs_and_activity or read_voidstrap_file, then fix_app_issue using the returned fix id. Read get_guide topic fixing.
		Change any visible Voidstrap text or the whole application background: manage_app_control, after get_guide topic app_control.
		Read or edit the Voidstrap source code itself: manage_app_code, after get_guide topic app_code. Edits are copies, the running app never changes, tell the user to export and rebuild to run them.
		Anything about how a subsystem works inside Voidstrap: get_docs topic architecture, then manage_app_code to read the real code.

		READ, CHANGE, VERIFY
		Read existing values or files before editing. Reuse list_editors and its editorId instead of creating duplicate themes. Use dryRun for a broad settings or FastFlag change. Save only the requested changes and read every reply, including isError and any warnings. A timeout can happen after a change started; read the state before retrying.
		For editor work, get_editor_code and list_editor_files show the real document. edit_editor changes selected regions; set_editor_code replaces the whole file. save_editor persists it. undo_editor reverts an editor change. undo_last_change covers supported settings, FastFlag, mod file and RiShade changes; inspect its history before relying on it.
		For Voidstrap mods, validate checks loading, test exercises sandbox behavior, reload requests activation, list confirms loaded true, and problems reports runtime errors. These checks serve different purposes. A new mod waits for the user's approval in Voidstrap. Do not report it as running while approval is pending. A successful attach request is asynchronous and does not prove visible layout.
		Cursor sprites must use cursor.move events. $.clock.every clamps repeat intervals to 500 ms; it cannot animate at 16 or 33 ms. The mods guide has a complete WindowRoot Canvas overlay example and a test.
		After an edit, read back the changed settings or code, run the relevant test or diagnostic, and inspect visible results when possible. Say what changed, what you actually verified and what still needs the user's action. A saved file, a passing test and a visible working feature are separate claims.
		Guides: workflow, mods, bootstrapper_theme, keystroke_theme, app_theme, fixing, architecture, mod_api, app_control, app_code, search, library_pr. The same guides are available as voidstrap://guide/<topic> resources. The FastFlag reference is voidstrap://fastflags/allowlist-and-tracker.
		""";

    private static readonly Dictionary<string, string> Topics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bootstrapper_theme"] = BootstrapperTheme,
        ["keystroke_theme"] = KeystrokeTheme,
        ["app_theme"] = AppTheme,
        ["fixing"] = Fixing,
        ["mods"] = McpTools.ModLayoutGuide,
        ["workflow"] = Workflow,
        ["architecture"] = Architecture,
        ["mod_api"] = ModApi,
        ["mod_ui"] = ModUi,
        ["app_control"] = AppControl,
        ["app_code"] = AppCode,
        ["search"] = Search,
        ["library_pr"] = LibraryPr,
    };

    public static IEnumerable<string> TopicNames => Topics.Keys;

    public static string? Get(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return null;
        string clean = topic.Trim().Replace(' ', '_').Replace('-', '_');
        if (Topics.TryGetValue(clean, out string? text))
            return text;
        string? match = Topics.Keys.FirstOrDefault(key =>
            key.Contains(clean, StringComparison.OrdinalIgnoreCase) || clean.Contains(key.Split('_')[0], StringComparison.OrdinalIgnoreCase)
        );
        return match == null ? null : Topics[match];
    }
}
