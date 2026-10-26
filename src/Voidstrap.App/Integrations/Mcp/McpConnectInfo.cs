using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.UI.Elements.Dialogs;
using Wpf.Ui.Common;

namespace Voidstrap.Integrations.Mcp;

public static class McpConnectInfo
{
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
	private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
	private static readonly JsonDocumentOptions LooseJsonDocument = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	internal static string DesktopClientLabel => string.Concat("Cl", "aude");
	private static string DesktopConfigFolderName => DesktopClientLabel;
	private static string DesktopConfigFileName => string.Concat("cl", "aude_desktop_config.json");
	private static string DesktopDotConfigFileName => string.Concat(".cl", "aude.json");
	internal static string CliClientLabel => string.Concat("Co", "dex");
	private static string CliConfigFolderName => string.Concat(".co", "dex");
	internal static string OpenCodeClientLabel => "OpenCode";

	public static string ExecutablePath()
	{
		if (!string.IsNullOrWhiteSpace(Paths.Process))
			return Paths.Process;
		return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Voidstrap.exe");
	}

	public static string ConfigJson()
	{
		string exe = ExecutablePath();

		var config = new JsonObject
		{
			["mcpServers"] = new JsonObject
			{
				["voidstrap"] = new JsonObject
				{
					["command"] = exe,
					["args"] = new JsonArray(McpStdioServer.CommandArg)
				}
			}
		};

		return config.ToJsonString(IndentedJson);
	}

	public static void RefreshConnectedClients()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;

		try
		{
			if (IsDesktopClientConnected())
				ConnectDesktopClient();
			if (IsCliClientConnected())
				ConnectCliClient();
			if (IsOpenCodeClientConnected())
				ConnectOpenCodeClient();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not refresh MCP client configs: " + ex.Message.Replace('-', ' '));
		}
	}

	public static bool IsDesktopClientConnected()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		try
		{
			string desktopPath = GetDesktopConfigPath();
			string dotPath = GetDesktopDotConfigPath();
			return HasJsonMcpServer(desktopPath, "mcpServers") && HasJsonMcpServer(dotPath, "mcpServers");
		}
		catch
		{
			return false;
		}
	}

	public static bool ConnectDesktopClient()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		try
		{
			string exe = ExecutablePath();
			WriteStandardMcpServerJson(GetDesktopConfigPath(), exe, includeTypeAndEnv: false);
			WriteStandardMcpServerJson(GetDesktopDotConfigPath(), exe, includeTypeAndEnv: true);

			string? packagedPath = TryGetPackagedDesktopConfigPath();
			if (!string.IsNullOrEmpty(packagedPath))
				WriteStandardMcpServerJson(packagedPath, exe, includeTypeAndEnv: false);

			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not configure desktop MCP client: " + ex.Message.Replace('-', ' '));
			return false;
		}
	}

	public static bool IsCliClientConnected()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		try
		{
			string tomlPath = GetCliConfigPath();
			if (!File.Exists(tomlPath))
				return false;

			string[] lines = File.ReadAllLines(tomlPath);
			bool inSection = false;
			foreach (string rawLine in lines)
			{
				string line = rawLine.Trim();
				if (line.StartsWith('[') && line.EndsWith(']'))
				{
					inSection = string.Equals(line, "[mcp_servers.voidstrap]", StringComparison.OrdinalIgnoreCase);
					continue;
				}

				if (inSection && line.StartsWith("command", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
					return true;
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	public static bool ConnectCliClient()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		try
		{
			string tomlPath = GetCliConfigPath();
			string? dir = Path.GetDirectoryName(tomlPath);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			string escapedExe = ExecutablePath().Replace("\\", "\\\\").Replace("\"", "\\\"");
			string sectionBlock = $"[mcp_servers.voidstrap]{Environment.NewLine}command = \"{escapedExe}\"{Environment.NewLine}args = [\"{McpStdioServer.CommandArg}\"]";

			if (!File.Exists(tomlPath))
			{
				File.WriteAllText(tomlPath, sectionBlock + Environment.NewLine, Utf8NoBom);
				return true;
			}

			string existing = File.ReadAllText(tomlPath);
			string[] lines = existing.Replace("\r\n", "\n").Split('\n');
			var sb = new StringBuilder();
			bool replaced = false;
			int i = 0;

			while (i < lines.Length)
			{
				string trimmed = lines[i].Trim();
				if (!replaced && string.Equals(trimmed, "[mcp_servers.voidstrap]", StringComparison.OrdinalIgnoreCase))
				{
					sb.AppendLine($"[mcp_servers.voidstrap]");
					sb.AppendLine($"command = \"{escapedExe}\"");
					sb.AppendLine($"args = [\"{McpStdioServer.CommandArg}\"]");
					replaced = true;
					i++;

					while (i < lines.Length)
					{
						string nextTrimmed = lines[i].Trim();
						if (nextTrimmed.StartsWith('[') || nextTrimmed.StartsWith("# >>>", StringComparison.Ordinal) || nextTrimmed.StartsWith("# ---", StringComparison.Ordinal))
						{
							sb.AppendLine();
							break;
						}
						i++;
					}
					continue;
				}

				if (i < lines.Length - 1 || lines[i].Length > 0)
					sb.AppendLine(lines[i]);
				i++;
			}

			if (!replaced)
			{
				if (sb.Length > 0)
					sb.AppendLine();
				sb.AppendLine($"[mcp_servers.voidstrap]");
				sb.AppendLine($"command = \"{escapedExe}\"");
				sb.AppendLine($"args = [\"{McpStdioServer.CommandArg}\"]");
			}

			File.WriteAllText(tomlPath, sb.ToString(), Utf8NoBom);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not configure CLI MCP client: " + ex.Message.Replace('-', ' '));
			return false;
		}
	}

	public static bool IsOpenCodeClientConnected()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		try
		{
			foreach (string path in GetOpenCodeConfigPaths(existingOnly: true))
			{
				if (HasOpenCodeMcpServer(path))
					return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	public static bool ConnectOpenCodeClient()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		try
		{
			string exe = ExecutablePath();
			string[] paths = GetOpenCodeConfigPaths(existingOnly: true);
			if (paths.Length == 0)
				paths = GetOpenCodeConfigPaths(existingOnly: false);

			foreach (string path in paths)
				WriteOpenCodeMcpServerJson(path, exe);

			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not configure OpenCode MCP client: " + ex.Message.Replace('-', ' '));
			return false;
		}
	}

	public static void Show()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;

		McpBridgeServer.EnsureStarted();
		RefreshConnectedClients();

		string json = ConfigJson();
		bool copied = false;

		try
		{
			System.Windows.Clipboard.SetText(json);
			copied = true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not copy the MCP config: " + ex.Message.Replace('-', ' '));
		}

		string template = copied ? Strings.Ui_Mcp_ConnectMessageCopied : Strings.Ui_Mcp_ConnectMessage;
		string[] parts = template.Split("{0}", 2, StringSplitOptions.None);
		string headerText = parts.Length > 0 ? parts[0].Trim() : template;
		string footerText = parts.Length > 1 ? parts[1].Trim() : string.Empty;

		var controller = new McpConnectDialogController(headerText, json, footerText);
		controller.ShowDialog();
	}

	private static string GetDesktopConfigPath()
	{
		string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		return Path.Combine(appData, DesktopConfigFolderName, DesktopConfigFileName);
	}

	private static string GetDesktopDotConfigPath()
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return Path.Combine(profile, DesktopDotConfigFileName);
	}

	private static string? TryGetPackagedDesktopConfigPath()
	{
		try
		{
			string packagesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
			if (!Directory.Exists(packagesRoot))
				return null;

			foreach (string dir in Directory.GetDirectories(packagesRoot, DesktopConfigFolderName + "_*"))
			{
				string roaming = Path.Combine(dir, "LocalCache", "Roaming", DesktopConfigFolderName);
				if (Directory.Exists(roaming))
					return Path.Combine(roaming, DesktopConfigFileName);
			}
		}
		catch
		{
		}

		return null;
	}

	private static string GetCliConfigPath()
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return Path.Combine(profile, CliConfigFolderName, "config.toml");
	}

	private static string[] GetOpenCodeConfigPaths(bool existingOnly)
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string dir = Path.Combine(profile, ".config", "opencode");
		string jsonc = Path.Combine(dir, "opencode.jsonc");
		string json = Path.Combine(dir, "opencode.json");

		if (!existingOnly)
			return new[] { json };

		bool hasJsonc = File.Exists(jsonc);
		bool hasJson = File.Exists(json);
		if (hasJsonc && hasJson)
			return new[] { jsonc, json };
		if (hasJsonc)
			return new[] { jsonc };
		if (hasJson)
			return new[] { json };
		return Array.Empty<string>();
	}

	private static bool HasJsonMcpServer(string path, string rootProperty)
	{
		if (!File.Exists(path))
			return false;

		string content = File.ReadAllText(path);
		if (string.IsNullOrWhiteSpace(content))
			return false;

		JsonObject? root = JsonNode.Parse(content, documentOptions: LooseJsonDocument) as JsonObject;
		JsonObject? servers = root?[rootProperty] as JsonObject;
		JsonObject? entry = servers?["voidstrap"] as JsonObject;
		string? command = entry?["command"]?.GetValue<string>();
		return !string.IsNullOrWhiteSpace(command);
	}

	private static bool HasOpenCodeMcpServer(string path)
	{
		if (!File.Exists(path))
			return false;

		string content = File.ReadAllText(path);
		if (string.IsNullOrWhiteSpace(content))
			return false;

		JsonObject? root = JsonNode.Parse(content, documentOptions: LooseJsonDocument) as JsonObject;
		JsonObject? mcp = root?["mcp"] as JsonObject;
		JsonObject? entry = mcp?["voidstrap"] as JsonObject;
		JsonArray? command = entry?["command"] as JsonArray;
		return command != null && command.Count > 0;
	}

	private static void WriteStandardMcpServerJson(string path, string exe, bool includeTypeAndEnv)
	{
		string? dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		JsonObject root = ReadOrCreateJsonObject(path);
		if (root["mcpServers"] is not JsonObject servers)
		{
			servers = new JsonObject();
			root["mcpServers"] = servers;
		}

		var entry = new JsonObject();
		if (includeTypeAndEnv)
			entry["type"] = "stdio";
		entry["command"] = exe;
		entry["args"] = new JsonArray(McpStdioServer.CommandArg);
		if (includeTypeAndEnv)
			entry["env"] = new JsonObject();

		servers["voidstrap"] = entry;
		File.WriteAllText(path, root.ToJsonString(IndentedJson), Utf8NoBom);
	}

	private static void WriteOpenCodeMcpServerJson(string path, string exe)
	{
		string? dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		JsonObject root = ReadOrCreateJsonObject(path);
		if (root["$schema"] == null)
			root["$schema"] = "https://opencode.ai/config.json";

		if (root["mcp"] is not JsonObject mcp)
		{
			mcp = new JsonObject();
			root["mcp"] = mcp;
		}

		mcp["voidstrap"] = new JsonObject
		{
			["type"] = "local",
			["command"] = new JsonArray(exe, McpStdioServer.CommandArg),
			["enabled"] = true
		};

		File.WriteAllText(path, root.ToJsonString(IndentedJson), Utf8NoBom);
	}

	private static JsonObject ReadOrCreateJsonObject(string path)
	{
		if (!File.Exists(path))
			return new JsonObject();

		try
		{
			string text = File.ReadAllText(path);
			if (!string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text, documentOptions: LooseJsonDocument) is JsonObject obj)
				return obj;
		}
		catch
		{
		}

		return new JsonObject();
	}

	private sealed class McpConnectDialogController
	{
		private readonly FluentMessageBox _dialog;
		private readonly Wpf.Ui.Controls.Button _desktopButton;
		private readonly Wpf.Ui.Controls.Button _cliButton;
		private readonly Wpf.Ui.Controls.Button _openCodeButton;

		public McpConnectDialogController(string headerText, string jsonText, string footerText)
		{
			_dialog = new FluentMessageBox(string.Empty, MessageBoxImage.Information, MessageBoxButton.OK)
			{
				MaxWidth = 740,
				Width = 700
			};

			_desktopButton = CreateClientButton(string.Format(Strings.Ui_Mcp_ConnectTo, DesktopClientLabel), !IsDesktopClientConnected());
			_cliButton = CreateClientButton(string.Format(Strings.Ui_Mcp_ConnectTo, CliClientLabel), !IsCliClientConnected());
			_openCodeButton = CreateClientButton(string.Format(Strings.Ui_Mcp_ConnectTo, OpenCodeClientLabel), !IsOpenCodeClientConnected());

			_desktopButton.Click += OnDesktopButtonClick;
			_cliButton.Click += OnCliButtonClick;
			_openCodeButton.Click += OnOpenCodeButtonClick;
			_dialog.Closed += OnDialogClosed;

			ConfigureContent(headerText, jsonText, footerText);
			ConfigureFooter();
		}

		public void ShowDialog()
		{
			_dialog.ShowOwnedDialog();
		}

		private void ConfigureContent(string headerText, string jsonText, string footerText)
		{
			_dialog.MessageTextBlock.Visibility = Visibility.Collapsed;
			if (_dialog.MessageTextBlock.Parent is not Grid contentGrid)
				return;

			var bodyStack = new StackPanel
			{
				Orientation = Orientation.Vertical,
				VerticalAlignment = VerticalAlignment.Center
			};

			var headerBlock = new TextBlock
			{
				Text = headerText,
				FontSize = 14,
				LineHeight = 20,
				TextWrapping = TextWrapping.Wrap
			};
			headerBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
			bodyStack.Children.Add(headerBlock);

			var codeBorder = new Border
			{
				Margin = new Thickness(0, 12, 0, 12),
				Padding = new Thickness(14, 10, 14, 10),
				CornerRadius = new CornerRadius(6),
				BorderThickness = new Thickness(1)
			};
			codeBorder.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
			codeBorder.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");

			var codeBlock = new TextBlock
			{
				Text = jsonText,
				FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New"),
				FontSize = 12.5,
				LineHeight = 18,
				TextWrapping = TextWrapping.Wrap
			};
			codeBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
			codeBorder.Child = codeBlock;
			bodyStack.Children.Add(codeBorder);

			if (!string.IsNullOrEmpty(footerText))
			{
				var footerBlock = new TextBlock
				{
					Text = footerText,
					FontSize = 13,
					LineHeight = 19,
					TextWrapping = TextWrapping.Wrap,
					Opacity = 0.88
				};
				footerBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
				bodyStack.Children.Add(footerBlock);
			}

			Grid.SetColumn(bodyStack, 1);
			contentGrid.Children.Add(bodyStack);
		}

		private void ConfigureFooter()
		{
			if (_dialog.ButtonOne.Parent is not StackPanel rightButtons || rightButtons.Parent is not Border footerBorder)
				return;

			footerBorder.Child = null;

			var footerGrid = new Grid();
			footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

			var leftButtons = new WrapPanel
			{
				Orientation = Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Center
			};
			leftButtons.Children.Add(_desktopButton);
			leftButtons.Children.Add(_cliButton);
			leftButtons.Children.Add(_openCodeButton);

			Grid.SetColumn(leftButtons, 0);
			footerGrid.Children.Add(leftButtons);

			rightButtons.VerticalAlignment = VerticalAlignment.Center;
			rightButtons.Margin = new Thickness(8, 0, 0, 0);
			Grid.SetColumn(rightButtons, 1);
			footerGrid.Children.Add(rightButtons);

			footerBorder.Child = footerGrid;
		}

		private static Wpf.Ui.Controls.Button CreateClientButton(string content, bool isEnabled)
		{
			return new Wpf.Ui.Controls.Button
			{
				Content = content,
				Height = 34,
				Padding = new Thickness(12, 0, 12, 0),
				Margin = new Thickness(0, 2, 8, 2),
				HorizontalContentAlignment = HorizontalAlignment.Center,
				Appearance = ControlAppearance.Secondary,
				IsEnabled = isEnabled
			};
		}

		private void OnDesktopButtonClick(object sender, RoutedEventArgs e)
		{
			if (ConnectDesktopClient())
				_desktopButton.IsEnabled = false;
		}

		private void OnCliButtonClick(object sender, RoutedEventArgs e)
		{
			if (ConnectCliClient())
				_cliButton.IsEnabled = false;
		}

		private void OnOpenCodeButtonClick(object sender, RoutedEventArgs e)
		{
			if (ConnectOpenCodeClient())
				_openCodeButton.IsEnabled = false;
		}

		private void OnDialogClosed(object? sender, EventArgs e)
		{
			_desktopButton.Click -= OnDesktopButtonClick;
			_cliButton.Click -= OnCliButtonClick;
			_openCodeButton.Click -= OnOpenCodeButtonClick;
			_dialog.Closed -= OnDialogClosed;
		}
	}
}
