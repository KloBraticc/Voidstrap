using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Resources;

namespace Voidstrap.Integrations.Studio;

public static class StudioPluginInstaller
{
	private const string LogTag = "StudioPlugin";

	private const string PluginSource = "local StudioService = game:GetService(\"StudioService\")\nlocal RunService = game:GetService(\"RunService\")\nlocal Selection = game:GetService(\"Selection\")\nlocal HttpService = game:GetService(\"HttpService\")\nlocal MarketplaceService = game:GetService(\"MarketplaceService\")\nlocal Players = game:GetService(\"Players\")\nlocal TweenService = game:GetService(\"TweenService\")\n\nlocal BRIDGE = \"http://127.0.0.1:40404\"\nlocal BRIDGE_HOST = BRIDGE:gsub(\"^https?://\", \"\")\nlocal ICON = \"rbxasset://textures/VoidstrapStudio.png\"\n\nlocal TWEEN_FAST = TweenInfo.new(0.15, Enum.EasingStyle.Quad, Enum.EasingDirection.Out)\nlocal TWEEN_FADE = TweenInfo.new(0.25, Enum.EasingStyle.Quad, Enum.EasingDirection.Out)\n\nlocal theme = {\n\taccent = Color3.fromRGB(0, 120, 212),\n\tsection = Color3.fromRGB(37, 39, 46),\n\trow = Color3.fromRGB(46, 49, 56),\n\ttext = Color3.fromRGB(233, 236, 242),\n\tsub = Color3.fromRGB(150, 155, 165),\n\ttoggleOff = Color3.fromRGB(70, 74, 82),\n\twhite = Color3.fromRGB(255, 255, 255),\n\tconnected = Color3.fromRGB(88, 196, 120),\n}\n\nlocal painters = {}\nlocal function repaint()\n\tfor _, fn in ipairs(painters) do\n\t\tpcall(fn)\n\tend\nend\n\nlocal function hexToColor(hex)\n\tif type(hex) ~= \"string\" then\n\t\treturn nil\n\tend\n\thex = hex:gsub(\"#\", \"\")\n\tif #hex < 6 then\n\t\treturn nil\n\tend\n\tlocal r = tonumber(hex:sub(1, 2), 16)\n\tlocal g = tonumber(hex:sub(3, 4), 16)\n\tlocal b = tonumber(hex:sub(5, 6), 16)\n\tif r and g and b then\n\t\treturn Color3.fromRGB(r, g, b)\n\tend\n\treturn nil\nend\n\nlocal function applyPalette(p)\n\tif type(p) ~= \"table\" then\n\t\treturn\n\tend\n\tlocal c = hexToColor(p.accent)\n\tif c and c ~= theme.accent then\n\t\ttheme.accent = c\n\t\trepaint()\n\tend\nend\n\nlocal function refreshStudioTheme()\n\tlocal ok, studioTheme = pcall(function()\n\t\treturn settings().Studio.Theme\n\tend)\n\tif not ok or not studioTheme then\n\t\treturn\n\tend\n\tlocal function pick(name, fallback)\n\t\tlocal okc, col = pcall(function()\n\t\t\treturn studioTheme:GetColor(Enum.StudioStyleGuideColor[name])\n\t\tend)\n\t\tif okc and col then\n\t\t\treturn col\n\t\tend\n\t\treturn fallback\n\tend\n\ttheme.section = pick(\"Titlebar\", theme.section)\n\ttheme.row = pick(\"InputFieldBackground\", theme.row)\n\ttheme.text = pick(\"MainText\", theme.text)\n\ttheme.sub = pick(\"SubText\", theme.sub)\n\ttheme.toggleOff = pick(\"Button\", theme.toggleOff)\n\trepaint()\nend\n\nlocal function loadBool(key, default)\n\tlocal value = plugin:GetSetting(key)\n\tif value == nil then\n\t\treturn default\n\tend\n\treturn value == true\nend\n\nlocal function loadString(key, default)\n\tlocal value = plugin:GetSetting(key)\n\tif type(value) == \"string\" then\n\t\treturn value\n\tend\n\treturn default\nend\n\nlocal state = {\n\treporting = loadBool(\"reporting\", true),\n\tsharePlace = loadBool(\"sharePlace\", true),\n\tshareScript = loadBool(\"shareScript\", true),\n\tshareMode = loadBool(\"shareMode\", true),\n\tshareSelection = loadBool(\"shareSelection\", false),\n\tcustom = loadString(\"custom\", \"\"),\n}\n\nlocal pushState\n\nlocal toolbar = plugin:CreateToolbar(\"Voidstrap\")\nlocal openButton = toolbar:CreateButton(\"VoidstrapPanel\", \"Open the Voidstrap panel\", ICON, \"Voidstrap\")\nopenButton.ClickableWhenViewportHidden = true\n\nlocal widgetInfo = DockWidgetPluginGuiInfo.new(Enum.InitialDockState.Right, false, false, 300, 460, 260, 360)\nlocal widget = plugin:CreateDockWidgetPluginGui(\"VoidstrapStudioPanel\", widgetInfo)\nwidget.Title = \"Voidstrap\"\n\nlocal root = Instance.new(\"CanvasGroup\")\nroot.Size = UDim2.fromScale(1, 1)\nroot.BackgroundTransparency = 1\nroot.BorderSizePixel = 0\nroot.Parent = widget\n\nlocal header = Instance.new(\"Frame\")\nheader.Size = UDim2.new(1, 0, 0, 48)\nheader.BorderSizePixel = 0\nheader.Parent = root\ntable.insert(painters, function()\n\theader.BackgroundColor3 = theme.section\nend)\n\nlocal logo = Instance.new(\"ImageLabel\")\nlogo.Size = UDim2.fromOffset(26, 26)\nlogo.Position = UDim2.new(0, 14, 0.5, -13)\nlogo.BackgroundTransparency = 1\nlogo.Image = ICON\nlogo.Parent = header\n\nlocal body = Instance.new(\"Frame\")\nbody.Size = UDim2.new(1, 0, 1, -48)\nbody.Position = UDim2.fromOffset(0, 48)\nbody.BackgroundTransparency = 1\nbody.Parent = root\n\nlocal bodyPad = Instance.new(\"UIPadding\")\nbodyPad.PaddingTop = UDim.new(0, 12)\nbodyPad.PaddingBottom = UDim.new(0, 12)\nbodyPad.PaddingLeft = UDim.new(0, 14)\nbodyPad.PaddingRight = UDim.new(0, 14)\nbodyPad.Parent = body\n\nlocal list = Instance.new(\"UIListLayout\")\nlist.Padding = UDim.new(0, 8)\nlist.SortOrder = Enum.SortOrder.LayoutOrder\nlist.Parent = body\n\nlocal statusCard = Instance.new(\"Frame\")\nstatusCard.Size = UDim2.new(1, 0, 0, 46)\nstatusCard.BorderSizePixel = 0\nstatusCard.LayoutOrder = 1\nstatusCard.Parent = body\nlocal statusCorner = Instance.new(\"UICorner\")\nstatusCorner.CornerRadius = UDim.new(0, 8)\nstatusCorner.Parent = statusCard\ntable.insert(painters, function()\n\tstatusCard.BackgroundColor3 = theme.section\nend)\n\nlocal statusDot = Instance.new(\"Frame\")\nstatusDot.Size = UDim2.fromOffset(10, 10)\nstatusDot.Position = UDim2.new(0, 14, 0.5, -5)\nstatusDot.BorderSizePixel = 0\nstatusDot.Parent = statusCard\nlocal dotCorner = Instance.new(\"UICorner\")\ndotCorner.CornerRadius = UDim.new(1, 0)\ndotCorner.Parent = statusDot\n\nlocal statusLabel = Instance.new(\"TextLabel\")\nstatusLabel.Size = UDim2.new(1, -40, 1, 0)\nstatusLabel.Position = UDim2.fromOffset(34, 0)\nstatusLabel.BackgroundTransparency = 1\nstatusLabel.Font = Enum.Font.GothamMedium\nstatusLabel.TextSize = 13\nstatusLabel.TextXAlignment = Enum.TextXAlignment.Left\nstatusLabel.Text = \"Voidstrap app not found\"\nstatusLabel.Parent = statusCard\ntable.insert(painters, function()\n\tstatusLabel.TextColor3 = theme.text\nend)\n\nlocal isConnected = false\nlocal function setConnected(connected)\n\tisConnected = connected\n\tlocal target = connected and theme.connected or theme.toggleOff\n\tTweenService:Create(statusDot, TWEEN_FAST, { BackgroundColor3 = target }):Play()\n\tif connected then\n\t\tstatusLabel.Text = state.reporting and (\"Connected to Voidstrap, \" .. BRIDGE_HOST) or \"Connected, presence off\"\n\telse\n\t\tstatusLabel.Text = \"Voidstrap app not found\"\n\tend\nend\ntable.insert(painters, function()\n\tsetConnected(isConnected)\nend)\n\nlocal orderCounter = 1\nlocal function nextOrder()\n\torderCounter = orderCounter + 1\n\treturn orderCounter\nend\n\nlocal function makeToggleRow(labelText, key)\n\tlocal row = Instance.new(\"TextButton\")\n\trow.Size = UDim2.new(1, 0, 0, 38)\n\trow.AutoButtonColor = false\n\trow.Text = \"\"\n\trow.BorderSizePixel = 0\n\trow.LayoutOrder = nextOrder()\n\trow.Parent = body\n\tlocal corner = Instance.new(\"UICorner\")\n\tcorner.CornerRadius = UDim.new(0, 8)\n\tcorner.Parent = row\n\n\tlocal label = Instance.new(\"TextLabel\")\n\tlabel.Size = UDim2.new(1, -70, 1, 0)\n\tlabel.Position = UDim2.fromOffset(12, 0)\n\tlabel.BackgroundTransparency = 1\n\tlabel.Font = Enum.Font.Gotham\n\tlabel.TextSize = 13\n\tlabel.TextXAlignment = Enum.TextXAlignment.Left\n\tlabel.Text = labelText\n\tlabel.Parent = row\n\n\tlocal track = Instance.new(\"Frame\")\n\ttrack.Size = UDim2.fromOffset(40, 22)\n\ttrack.Position = UDim2.new(1, -52, 0.5, -11)\n\ttrack.BorderSizePixel = 0\n\ttrack.Parent = row\n\tlocal trackCorner = Instance.new(\"UICorner\")\n\ttrackCorner.CornerRadius = UDim.new(1, 0)\n\ttrackCorner.Parent = track\n\n\tlocal knob = Instance.new(\"Frame\")\n\tknob.Size = UDim2.fromOffset(16, 16)\n\tknob.BorderSizePixel = 0\n\tknob.Parent = track\n\tlocal knobCorner = Instance.new(\"UICorner\")\n\tknobCorner.CornerRadius = UDim.new(1, 0)\n\tknobCorner.Parent = knob\n\n\tlocal function knobPos()\n\t\treturn state[key] and UDim2.new(1, -19, 0.5, -8) or UDim2.new(0, 3, 0.5, -8)\n\tend\n\tknob.Position = knobPos()\n\n\tlocal hovered = false\n\tlocal function rowColor()\n\t\tif hovered then\n\t\t\treturn theme.row:Lerp(theme.white, 0.06)\n\t\tend\n\t\treturn theme.row\n\tend\n\n\tlocal function paint()\n\t\trow.BackgroundColor3 = rowColor()\n\t\tlabel.TextColor3 = theme.text\n\t\ttrack.BackgroundColor3 = state[key] and theme.accent or theme.toggleOff\n\t\tknob.BackgroundColor3 = theme.white\n\tend\n\ttable.insert(painters, paint)\n\n\trow.MouseEnter:Connect(function()\n\t\thovered = true\n\t\tTweenService:Create(row, TWEEN_FAST, { BackgroundColor3 = rowColor() }):Play()\n\tend)\n\trow.MouseLeave:Connect(function()\n\t\thovered = false\n\t\tTweenService:Create(row, TWEEN_FAST, { BackgroundColor3 = rowColor() }):Play()\n\tend)\n\trow.Activated:Connect(function()\n\t\tstate[key] = not state[key]\n\t\tplugin:SetSetting(key, state[key])\n\t\tTweenService:Create(track, TWEEN_FAST, { BackgroundColor3 = state[key] and theme.accent or theme.toggleOff }):Play()\n\t\tTweenService:Create(knob, TWEEN_FAST, { Position = knobPos() }):Play()\n\t\ttask.spawn(pushState)\n\tend)\nend\n\nmakeToggleRow(\"Place\", \"sharePlace\")\nmakeToggleRow(\"Active script\", \"shareScript\")\nmakeToggleRow(\"Mode\", \"shareMode\")\nmakeToggleRow(\"Selection\", \"shareSelection\")\n\nlocal customBox = Instance.new(\"TextBox\")\ncustomBox.Size = UDim2.new(1, 0, 0, 32)\ncustomBox.BorderSizePixel = 0\ncustomBox.ClearTextOnFocus = false\ncustomBox.Font = Enum.Font.Gotham\ncustomBox.TextSize = 13\ncustomBox.PlaceholderText = \"Custom status\"\ncustomBox.TextXAlignment = Enum.TextXAlignment.Left\ncustomBox.Text = state.custom\ncustomBox.LayoutOrder = nextOrder()\ncustomBox.Parent = body\nlocal customCorner = Instance.new(\"UICorner\")\ncustomCorner.CornerRadius = UDim.new(0, 8)\ncustomCorner.Parent = customBox\nlocal customPad = Instance.new(\"UIPadding\")\ncustomPad.PaddingLeft = UDim.new(0, 10)\ncustomPad.PaddingRight = UDim.new(0, 10)\ncustomPad.Parent = customBox\ntable.insert(painters, function()\n\tcustomBox.BackgroundColor3 = theme.row\n\tcustomBox.TextColor3 = theme.text\n\tcustomBox.PlaceholderColor3 = theme.sub\nend)\ncustomBox.FocusLost:Connect(function()\n\tif customBox.Text == state.custom then\n\t\treturn\n\tend\n\tstate.custom = customBox.Text\n\tplugin:SetSetting(\"custom\", state.custom)\n\ttask.spawn(pushState)\nend)\n\nlocal shareButton = Instance.new(\"TextButton\")\nshareButton.Size = UDim2.new(1, 0, 0, 40)\nshareButton.BorderSizePixel = 0\nshareButton.AutoButtonColor = false\nshareButton.Font = Enum.Font.GothamBold\nshareButton.TextSize = 14\nshareButton.Text = state.reporting and \"Presence on\" or \"Presence off\"\nshareButton.LayoutOrder = nextOrder()\nshareButton.Parent = body\nlocal shareCorner = Instance.new(\"UICorner\")\nshareCorner.CornerRadius = UDim.new(0, 8)\nshareCorner.Parent = shareButton\n\nlocal shareHovered = false\nlocal function shareColor()\n\tlocal base = state.reporting and theme.accent or theme.toggleOff\n\tif shareHovered then\n\t\treturn base:Lerp(theme.white, 0.08)\n\tend\n\treturn base\nend\ntable.insert(painters, function()\n\tshareButton.BackgroundColor3 = shareColor()\n\tshareButton.TextColor3 = theme.white\nend)\nshareButton.MouseEnter:Connect(function()\n\tshareHovered = true\n\tTweenService:Create(shareButton, TWEEN_FAST, { BackgroundColor3 = shareColor() }):Play()\nend)\nshareButton.MouseLeave:Connect(function()\n\tshareHovered = false\n\tTweenService:Create(shareButton, TWEEN_FAST, { BackgroundColor3 = shareColor() }):Play()\nend)\nshareButton.Activated:Connect(function()\n\tstate.reporting = not state.reporting\n\tplugin:SetSetting(\"reporting\", state.reporting)\n\tshareButton.Text = state.reporting and \"Presence on\" or \"Presence off\"\n\tTweenService:Create(shareButton, TWEEN_FAST, { BackgroundColor3 = shareColor() }):Play()\n\tsetConnected(isConnected)\n\ttask.spawn(pushState)\nend)\n\nrefreshStudioTheme()\npcall(function()\n\tsettings().Studio.ThemeChanged:Connect(refreshStudioTheme)\nend)\nrepaint()\n\nlocal placeInfo = { id = -1, name = \"\", creator = \"\", triedAt = 0 }\n\nlocal function resolvePlace()\n\tlocal pid = game.PlaceId\n\tlocal retry = placeInfo.name == \"\" and (os.clock() - placeInfo.triedAt) > 60\n\tif pid ~= placeInfo.id or retry then\n\t\tplaceInfo.id = pid\n\t\tplaceInfo.triedAt = os.clock()\n\t\tplaceInfo.name = \"\"\n\t\tplaceInfo.creator = \"\"\n\t\tif pid > 0 then\n\t\t\tlocal ok, info = pcall(function()\n\t\t\t\treturn MarketplaceService:GetProductInfo(pid)\n\t\t\tend)\n\t\t\tif ok and type(info) == \"table\" then\n\t\t\t\tif type(info.Name) == \"string\" then\n\t\t\t\t\tplaceInfo.name = info.Name\n\t\t\t\tend\n\t\t\t\tif type(info.Creator) == \"table\" and type(info.Creator.Name) == \"string\" then\n\t\t\t\t\tplaceInfo.creator = info.Creator.Name\n\t\t\t\tend\n\t\t\tend\n\t\tend\n\tend\n\tlocal name = placeInfo.name\n\tif name == \"\" then\n\t\tname = game.Name\n\tend\n\treturn name, placeInfo.creator\nend\n\nlocal function currentMode()\n\tif RunService:IsRunning() then\n\t\treturn \"Playtesting\"\n\tend\n\tlocal ok, players = pcall(function()\n\t\treturn Players:GetPlayers()\n\tend)\n\tif ok and #players > 0 then\n\t\treturn \"Team Create\"\n\tend\n\treturn \"Editing\"\nend\n\nlocal lastScriptRef = nil\nlocal lastScriptLen = -1\nlocal lastScriptLines = 0\n\nlocal function collectState()\n\tif not state.reporting then\n\t\treturn { sharing = false }\n\tend\n\n\tlocal scriptName = \"\"\n\tlocal scriptLines = 0\n\tif state.shareScript then\n\t\tlocal okScript, activeScript = pcall(function()\n\t\t\treturn StudioService.ActiveScript\n\t\tend)\n\t\tif okScript and activeScript then\n\t\t\tscriptName = activeScript.Name\n\t\t\tlocal ok, source = pcall(function()\n\t\t\t\treturn activeScript.Source\n\t\t\tend)\n\t\t\tif ok and typeof(source) == \"string\" then\n\t\t\t\tif activeScript ~= lastScriptRef or #source ~= lastScriptLen then\n\t\t\t\t\tlastScriptRef = activeScript\n\t\t\t\t\tlastScriptLen = #source\n\t\t\t\t\tlocal lines = 0\n\t\t\t\t\tif source ~= \"\" then\n\t\t\t\t\t\tlines = 1\n\t\t\t\t\t\tfor _ in string.gmatch(source, \"\\n\") do\n\t\t\t\t\t\t\tlines = lines + 1\n\t\t\t\t\t\tend\n\t\t\t\t\tend\n\t\t\t\t\tlastScriptLines = lines\n\t\t\t\tend\n\t\t\t\tscriptLines = lastScriptLines\n\t\t\tend\n\t\tend\n\tend\n\n\tlocal placeName, creator = \"\", \"\"\n\tlocal placeId, universeId = 0, 0\n\tif state.sharePlace then\n\t\tplaceName, creator = resolvePlace()\n\t\tplaceId = game.PlaceId\n\t\tuniverseId = game.GameId\n\tend\n\n\tlocal selectionCount = 0\n\tlocal selectionClass = \"\"\n\tif state.shareSelection then\n\t\tlocal ok, sel = pcall(function()\n\t\t\treturn Selection:Get()\n\t\tend)\n\t\tif ok and #sel > 0 then\n\t\t\tselectionCount = #sel\n\t\t\tselectionClass = sel[1].ClassName\n\t\tend\n\tend\n\n\treturn {\n\t\tsharing = true,\n\t\tplace = placeName,\n\t\tplaceId = placeId,\n\t\tuniverseId = universeId,\n\t\tcreator = creator,\n\t\tscript = scriptName,\n\t\tscriptLines = scriptLines,\n\t\tmode = state.shareMode and currentMode() or \"\",\n\t\tselection = selectionCount,\n\t\tselectionClass = selectionClass,\n\t\tcustom = state.custom,\n\t}\nend\n\nlocal function post(payload)\n\tlocal encoded = HttpService:JSONEncode(payload)\n\tlocal ok, response = pcall(function()\n\t\treturn HttpService:RequestAsync({\n\t\t\tUrl = BRIDGE .. \"/rpc\",\n\t\t\tMethod = \"POST\",\n\t\t\tHeaders = { [\"Content-Type\"] = \"application/json\" },\n\t\t\tBody = encoded,\n\t\t})\n\tend)\n\tif ok and response ~= nil and response.Success == true then\n\t\tlocal okJson, decoded = pcall(function()\n\t\t\treturn HttpService:JSONDecode(response.Body)\n\t\tend)\n\t\tif okJson and type(decoded) == \"table\" then\n\t\t\tapplyPalette(decoded.palette)\n\t\t\tif type(decoded.version) == \"string\" and decoded.version ~= \"\" then\n\t\t\t\tlocal title = \"Voidstrap \" .. decoded.version\n\t\t\t\tif widget.Title ~= title then\n\t\t\t\t\twidget.Title = title\n\t\t\t\tend\n\t\t\tend\n\t\tend\n\t\treturn true\n\tend\n\treturn false\nend\n\nlocal alive = true\nplugin.Unloading:Connect(function()\n\talive = false\nend)\n\nlocal posting = false\nlocal pending = false\npushState = function()\n\tif not alive then\n\t\treturn\n\tend\n\tif posting then\n\t\tpending = true\n\t\treturn\n\tend\n\tposting = true\n\trepeat\n\t\tpending = false\n\t\tsetConnected(post(collectState()))\n\tuntil not pending or not alive\n\tposting = false\nend\n\ntask.spawn(function()\n\twhile alive do\n\t\tpushState()\n\t\ttask.wait(3)\n\tend\nend)\n\nwidget:GetPropertyChangedSignal(\"Enabled\"):Connect(function()\n\topenButton:SetActive(widget.Enabled)\n\tif widget.Enabled then\n\t\troot.GroupTransparency = 1\n\t\tTweenService:Create(root, TWEEN_FADE, { GroupTransparency = 0 }):Play()\n\tend\nend)\n\nopenButton.Click:Connect(function()\n\twidget.Enabled = not widget.Enabled\nend)";

	private static string PluginsFolder => Path.Combine(Paths.LocalAppData, "Roblox", "Plugins");

	private static string PluginFile => Path.Combine(PluginsFolder, "VoidstrapStudio.lua");

	public static string PluginsDirectory => PluginsFolder;

	public static string PluginPath => PluginFile;

	public static bool IsInstalled
	{
		get
		{
			try
			{
				return File.Exists(PluginFile);
			}
			catch
			{
				return false;
			}
		}
	}

	public static bool IsStudioPresent()
	{
		try
		{
			if (App.IsStudioVisible)
			{
				return true;
			}
		}
		catch
		{
		}
		try
		{
			if (Process.GetProcessesByName("RobloxStudioBeta").Length != 0)
			{
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	public static void EnsureInstalled(bool force = false)
	{
		try
		{
			if (!App.Settings.Prop.StudioPluginEnabled || (!force && !IsStudioPresent()))
			{
				return;
			}
			Directory.CreateDirectory(PluginsFolder);
			bool flag = true;
			if (File.Exists(PluginFile))
			{
				try
				{
					flag = File.ReadAllText(PluginFile) != PluginSource;
				}
				catch
				{
					flag = true;
				}
			}
			if (flag)
			{
				File.WriteAllText(PluginFile, PluginSource);
				App.Logger.WriteLine(LogTag, "Voidstrap Studio plugin written to " + PluginFile);
			}
			InstallIcon();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "Install failed: " + ex.Message);
		}
	}

	private static void InstallIcon()
	{
		try
		{
			byte[]? array = ReadAppLogo();
			if (array == null || array.Length == 0)
			{
				return;
			}
			List<string> list = new List<string> { Paths.Versions };
			string studioInstallLocation = App.Settings.Prop.StudioInstallLocation;
			if (!string.IsNullOrWhiteSpace(studioInstallLocation))
			{
				list.Add(studioInstallLocation);
			}
			foreach (string item in list)
			{
				if (string.IsNullOrWhiteSpace(item) || !Directory.Exists(item))
				{
					continue;
				}
				string[] directories = Directory.GetDirectories(item);
				foreach (string path in directories)
				{
					try
					{
						if (!File.Exists(Path.Combine(path, "RobloxStudioBeta.exe")))
						{
							continue;
						}
						string text = Path.Combine(path, "content", "textures");
						if (Directory.Exists(text))
						{
							string text2 = Path.Combine(text, "VoidstrapStudio.png");
							if (!File.Exists(text2) || new FileInfo(text2).Length != array.Length)
							{
								File.WriteAllBytes(text2, array);
								App.Logger.WriteLine(LogTag, "Plugin icon written to " + text2);
							}
						}
					}
					catch
					{
					}
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "Icon install failed: " + ex.Message);
		}
	}

	private static byte[]? ReadAppLogo()
	{
		try
		{
			StreamResourceInfo resourceStream = Application.GetResourceStream(new Uri("pack://application:,,,/Voidstrap.png"));
			if (resourceStream == null)
			{
				return null;
			}
			using Stream stream = resourceStream.Stream;
			using MemoryStream memoryStream = new MemoryStream();
			stream.CopyTo(memoryStream);
			return memoryStream.ToArray();
		}
		catch
		{
			return null;
		}
	}

	public static bool Reinstall()
	{
		try
		{
			Directory.CreateDirectory(PluginsFolder);
			File.WriteAllText(PluginFile, PluginSource);
			App.Logger.WriteLine(LogTag, "Voidstrap Studio plugin written to " + PluginFile);
			InstallIcon();
			return true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "Install failed: " + ex.Message);
			return false;
		}
	}

	public static void Uninstall()
	{
		try
		{
			if (File.Exists(PluginFile))
			{
				File.Delete(PluginFile);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogTag, "Uninstall failed: " + ex.Message);
		}
	}

	private static readonly string[] ModernOnlyPlugins = new string[2] { "VoidstrapStudio.lua", "RojoManagedPlugin.rbxm" };

	private static string StashFolder => Path.Combine(Paths.Base, "StudioPluginStash");

	public static void StashForClassicClient()
	{
		int moved = 0;
		foreach (string name in ModernOnlyPlugins)
		{
			try
			{
				string live = Path.Combine(PluginsFolder, name);
				if (!File.Exists(live))
				{
					continue;
				}
				Directory.CreateDirectory(StashFolder);
				string stashed = Path.Combine(StashFolder, name);
				File.Move(live, stashed, overwrite: true);
				moved++;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogTag, "Could not move " + name + " out of the plugins folder: " + ex.Message);
			}
		}
		if (moved > 0)
		{
			App.Logger.WriteLine(LogTag, "Moved " + moved + " modern Studio plugin(s) aside, classic clients cannot parse them");
		}
	}

	public static void RestoreAfterClassicClient()
	{
		int restored = 0;
		foreach (string name in ModernOnlyPlugins)
		{
			try
			{
				string stashed = Path.Combine(StashFolder, name);
				if (!File.Exists(stashed))
				{
					continue;
				}
				Directory.CreateDirectory(PluginsFolder);
				File.Move(stashed, Path.Combine(PluginsFolder, name), overwrite: true);
				restored++;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogTag, "Could not restore " + name + ": " + ex.Message);
			}
		}
		if (restored > 0)
		{
			App.Logger.WriteLine(LogTag, "Restored " + restored + " modern Studio plugin(s)");
		}
	}
}
