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

	public const string Workflow = """
		HOW TO WORK WITH VOIDSTRAP OVER MCP

		Start with get_voidstrap_overview. Read every tool reply fully, a reply marked as an error means nothing (or only part)
		was changed. Prefer dryRun before large settings or FastFlag changes. undo_last_change and undo_editor revert mistakes.
		Before building something, read its guide with get_guide: bootstrapper_theme (launcher themes, HTML web panels),
		keystroke_theme, app_theme (Voidstrap's own colors), fixing (errors and broken state).
		After changing something the user should see, verify it: test_bootstrapper_theme for launcher themes, get_settings for
		settings, diagnose_app for problems, and tell the user exactly what you changed.
		""";

	private static readonly Dictionary<string, string> Topics = new(StringComparer.OrdinalIgnoreCase)
	{
		["bootstrapper_theme"] = BootstrapperTheme,
		["keystroke_theme"] = KeystrokeTheme,
		["app_theme"] = AppTheme,
		["fixing"] = Fixing,
		["workflow"] = Workflow
	};

	public static IEnumerable<string> TopicNames => Topics.Keys;

	public static string? Get(string? topic)
	{
		if (string.IsNullOrWhiteSpace(topic))
			return null;
		string clean = topic.Trim().Replace(' ', '_').Replace('-', '_');
		if (Topics.TryGetValue(clean, out string? text))
			return text;
		string? match = Topics.Keys.FirstOrDefault(key => key.Contains(clean, StringComparison.OrdinalIgnoreCase) || clean.Contains(key.Split('_')[0], StringComparison.OrdinalIgnoreCase));
		return match == null ? null : Topics[match];
	}
}
