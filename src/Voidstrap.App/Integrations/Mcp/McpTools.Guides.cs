using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Exceptions;
using Voidstrap.UI.Elements.Bootstrapper;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
    private static McpToolResult GetGuide(JsonObject args)
    {
        string topic = McpArgs.Trimmed(args, "topic", "workflow");
        string? text = McpGuides.Get(topic);
        return text == null
            ? new McpToolResult($"No guide for '{topic}'. Topics: {string.Join(", ", McpGuides.TopicNames)}.", true)
            : new McpToolResult(text);
    }

    private static McpToolResult TestBootstrapperTheme(JsonObject args)
    {
        string name = McpArgs.Trimmed(args, "name", App.Settings.Prop.SelectedCustomTheme ?? "");
        if (!McpArgs.IsSafeName(name))
            return new McpToolResult("Provide the theme 'name'. " + UnsafeThemeName, true);
        if (!File.Exists(Path.Combine(Paths.CustomThemes, name, "Theme.xml")))
            return new McpToolResult($"The theme '{name}' has no Theme.xml. Create it with open_editor type bootstrapper.", true);

        if (McpArgs.Flag(args, "activate", false))
        {
            App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
            App.Settings.Prop.SelectedCustomTheme = name;
            App.Settings.Save();
        }

        CustomDialog dialog;
        try
        {
            dialog = new CustomDialog();
            dialog.ApplyCustomTheme(name);
        }
        catch (CustomThemeException ex)
        {
            return new McpToolResult(
                $"The theme '{name}' does not build: {ex.EnglishMessage}{(ex.InnerException != null ? " (" + ex.InnerException.Message + ")" : "")}. Fix Theme.xml, see get_guide bootstrapper_theme.",
                true
            );
        }
        catch (Exception ex)
        {
            return new McpToolResult($"The theme '{name}' does not build: {ex.Message}", true);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"The theme '{name}' builds exactly like a real launch.");
        if (App.Settings.Prop.BootstrapperStyle != BootstrapperStyle.CustomDialog)
            sb.AppendLine(
                $"Warning: the launcher style is {App.Settings.Prop.BootstrapperStyle}, so this theme is NOT used yet. Call again with activate true."
            );
        else if (!string.Equals(App.Settings.Prop.SelectedCustomTheme, name, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine(
                $"Warning: the selected theme is '{App.Settings.Prop.SelectedCustomTheme}', not this one. Call again with activate true."
            );
        else
            sb.AppendLine("It is the active launcher theme.");

        string[] files = Directory.GetFiles(Path.Combine(Paths.CustomThemes, name), "*", SearchOption.AllDirectories);
        sb.AppendLine(
            "Files: " + string.Join(", ", files.Select(file => Path.GetRelativePath(Path.Combine(Paths.CustomThemes, name), file)))
        );

        if (!McpArgs.Flag(args, "show", false))
        {
            dialog.Close();
            return new McpToolResult(sb.ToString().TrimEnd());
        }

        int seconds = McpArgs.Int(args, "seconds", 8, 3, 30);
        new BootstrapperPreviewRun(dialog, seconds).Start();
        sb.AppendLine($"It is on screen now with a simulated install and closes by itself after {seconds} seconds.");
        return new McpToolResult(sb.ToString().TrimEnd());
    }

    private sealed class BootstrapperPreviewRun
    {
        private readonly CustomDialog _dialog;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

        private readonly int _ticks;

        private int _tick;

        public BootstrapperPreviewRun(CustomDialog dialog, int seconds)
        {
            _dialog = dialog;
            _ticks = seconds * 2;
        }

        public void Start()
        {
            _dialog.Message = "Preview, checking for updates";
            _dialog.CancelEnabled = true;
            _dialog.ProgressStyle = System.Windows.Forms.ProgressBarStyle.Marquee;
            _dialog.ProgressMaximum = 100;
            _dialog.ProgressValue = 0;
            _dialog.Closed += OnClosed;
            _timer.Tick += OnTick;
            _dialog.Show();
            _dialog.Activate();
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _tick++;
            if (_tick >= _ticks)
            {
                _dialog.Close();
                return;
            }
            if (_tick == 3)
            {
                _dialog.Message = "Preview, downloading Roblox";
                _dialog.ProgressStyle = System.Windows.Forms.ProgressBarStyle.Continuous;
            }
            if (_tick >= 3)
                _dialog.ProgressValue = Math.Min(100, (_tick - 2) * 100 / Math.Max(1, _ticks - 4));
            if (_tick == _ticks - 2)
            {
                _dialog.Message = "Preview, starting Roblox";
                _dialog.CancelEnabled = false;
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _dialog.Closed -= OnClosed;
        }
    }
}
