local StudioService = game:GetService("StudioService")
local RunService = game:GetService("RunService")
local Selection = game:GetService("Selection")
local HttpService = game:GetService("HttpService")
local MarketplaceService = game:GetService("MarketplaceService")
local Players = game:GetService("Players")
local TweenService = game:GetService("TweenService")

local BRIDGE = "http://127.0.0.1:40404"
local BRIDGE_HOST = BRIDGE:gsub("^https?://", "")
local ICON = "rbxasset://textures/VoidstrapStudio.png"

local TWEEN_FAST = TweenInfo.new(0.15, Enum.EasingStyle.Quad, Enum.EasingDirection.Out)
local TWEEN_FADE = TweenInfo.new(0.25, Enum.EasingStyle.Quad, Enum.EasingDirection.Out)

local theme = {
	accent = Color3.fromRGB(0, 120, 212),
	section = Color3.fromRGB(37, 39, 46),
	row = Color3.fromRGB(46, 49, 56),
	text = Color3.fromRGB(233, 236, 242),
	sub = Color3.fromRGB(150, 155, 165),
	toggleOff = Color3.fromRGB(70, 74, 82),
	white = Color3.fromRGB(255, 255, 255),
	connected = Color3.fromRGB(88, 196, 120),
}

local painters = {}
local function repaint()
	for _, fn in ipairs(painters) do
		pcall(fn)
	end
end

local function hexToColor(hex)
	if type(hex) ~= "string" then
		return nil
	end
	hex = hex:gsub("#", "")
	if #hex < 6 then
		return nil
	end
	local r = tonumber(hex:sub(1, 2), 16)
	local g = tonumber(hex:sub(3, 4), 16)
	local b = tonumber(hex:sub(5, 6), 16)
	if r and g and b then
		return Color3.fromRGB(r, g, b)
	end
	return nil
end

local function applyPalette(p)
	if type(p) ~= "table" then
		return
	end
	local c = hexToColor(p.accent)
	if c and c ~= theme.accent then
		theme.accent = c
		repaint()
	end
end

local function refreshStudioTheme()
	local ok, studioTheme = pcall(function()
		return settings().Studio.Theme
	end)
	if not ok or not studioTheme then
		return
	end
	local function pick(name, fallback)
		local okc, col = pcall(function()
			return studioTheme:GetColor(Enum.StudioStyleGuideColor[name])
		end)
		if okc and col then
			return col
		end
		return fallback
	end
	theme.section = pick("Titlebar", theme.section)
	theme.row = pick("InputFieldBackground", theme.row)
	theme.text = pick("MainText", theme.text)
	theme.sub = pick("SubText", theme.sub)
	theme.toggleOff = pick("Button", theme.toggleOff)
	repaint()
end

local function loadBool(key, default)
	local value = plugin:GetSetting(key)
	if value == nil then
		return default
	end
	return value == true
end

local function loadString(key, default)
	local value = plugin:GetSetting(key)
	if type(value) == "string" then
		return value
	end
	return default
end

local state = {
	reporting = loadBool("reporting", true),
	sharePlace = loadBool("sharePlace", true),
	shareScript = loadBool("shareScript", true),
	shareMode = loadBool("shareMode", true),
	shareSelection = loadBool("shareSelection", false),
	custom = loadString("custom", ""),
}

local pushState

local toolbar = plugin:CreateToolbar("Voidstrap")
local openButton = toolbar:CreateButton("VoidstrapPanel", "Open the Voidstrap panel", ICON, "Voidstrap")
openButton.ClickableWhenViewportHidden = true

local widgetInfo = DockWidgetPluginGuiInfo.new(Enum.InitialDockState.Right, false, false, 300, 460, 260, 360)
local widget = plugin:CreateDockWidgetPluginGui("VoidstrapStudioPanel", widgetInfo)
widget.Title = "Voidstrap"

local root = Instance.new("CanvasGroup")
root.Size = UDim2.fromScale(1, 1)
root.BackgroundTransparency = 1
root.BorderSizePixel = 0
root.Parent = widget

local header = Instance.new("Frame")
header.Size = UDim2.new(1, 0, 0, 48)
header.BorderSizePixel = 0
header.Parent = root
table.insert(painters, function()
	header.BackgroundColor3 = theme.section
end)

local logo = Instance.new("ImageLabel")
logo.Size = UDim2.fromOffset(26, 26)
logo.Position = UDim2.new(0, 14, 0.5, -13)
logo.BackgroundTransparency = 1
logo.Image = ICON
logo.Parent = header

local body = Instance.new("Frame")
body.Size = UDim2.new(1, 0, 1, -48)
body.Position = UDim2.fromOffset(0, 48)
body.BackgroundTransparency = 1
body.Parent = root

local bodyPad = Instance.new("UIPadding")
bodyPad.PaddingTop = UDim.new(0, 12)
bodyPad.PaddingBottom = UDim.new(0, 12)
bodyPad.PaddingLeft = UDim.new(0, 14)
bodyPad.PaddingRight = UDim.new(0, 14)
bodyPad.Parent = body

local list = Instance.new("UIListLayout")
list.Padding = UDim.new(0, 8)
list.SortOrder = Enum.SortOrder.LayoutOrder
list.Parent = body

local statusCard = Instance.new("Frame")
statusCard.Size = UDim2.new(1, 0, 0, 46)
statusCard.BorderSizePixel = 0
statusCard.LayoutOrder = 1
statusCard.Parent = body
local statusCorner = Instance.new("UICorner")
statusCorner.CornerRadius = UDim.new(0, 8)
statusCorner.Parent = statusCard
table.insert(painters, function()
	statusCard.BackgroundColor3 = theme.section
end)

local statusDot = Instance.new("Frame")
statusDot.Size = UDim2.fromOffset(10, 10)
statusDot.Position = UDim2.new(0, 14, 0.5, -5)
statusDot.BorderSizePixel = 0
statusDot.Parent = statusCard
local dotCorner = Instance.new("UICorner")
dotCorner.CornerRadius = UDim.new(1, 0)
dotCorner.Parent = statusDot

local statusLabel = Instance.new("TextLabel")
statusLabel.Size = UDim2.new(1, -40, 1, 0)
statusLabel.Position = UDim2.fromOffset(34, 0)
statusLabel.BackgroundTransparency = 1
statusLabel.Font = Enum.Font.GothamMedium
statusLabel.TextSize = 13
statusLabel.TextXAlignment = Enum.TextXAlignment.Left
statusLabel.Text = "Voidstrap app not found"
statusLabel.Parent = statusCard
table.insert(painters, function()
	statusLabel.TextColor3 = theme.text
end)

local isConnected = false
local function setConnected(connected)
	isConnected = connected
	local target = connected and theme.connected or theme.toggleOff
	TweenService:Create(statusDot, TWEEN_FAST, { BackgroundColor3 = target }):Play()
	if connected then
		statusLabel.Text = state.reporting and ("Connected to Voidstrap, " .. BRIDGE_HOST) or "Connected, presence off"
	else
		statusLabel.Text = "Voidstrap app not found"
	end
end
table.insert(painters, function()
	setConnected(isConnected)
end)

local orderCounter = 1
local function nextOrder()
	orderCounter += 1
	return orderCounter
end

local function makeToggleRow(labelText, key)
	local row = Instance.new("TextButton")
	row.Size = UDim2.new(1, 0, 0, 38)
	row.AutoButtonColor = false
	row.Text = ""
	row.BorderSizePixel = 0
	row.LayoutOrder = nextOrder()
	row.Parent = body
	local corner = Instance.new("UICorner")
	corner.CornerRadius = UDim.new(0, 8)
	corner.Parent = row

	local label = Instance.new("TextLabel")
	label.Size = UDim2.new(1, -70, 1, 0)
	label.Position = UDim2.fromOffset(12, 0)
	label.BackgroundTransparency = 1
	label.Font = Enum.Font.Gotham
	label.TextSize = 13
	label.TextXAlignment = Enum.TextXAlignment.Left
	label.Text = labelText
	label.Parent = row

	local track = Instance.new("Frame")
	track.Size = UDim2.fromOffset(40, 22)
	track.Position = UDim2.new(1, -52, 0.5, -11)
	track.BorderSizePixel = 0
	track.Parent = row
	local trackCorner = Instance.new("UICorner")
	trackCorner.CornerRadius = UDim.new(1, 0)
	trackCorner.Parent = track

	local knob = Instance.new("Frame")
	knob.Size = UDim2.fromOffset(16, 16)
	knob.BorderSizePixel = 0
	knob.Parent = track
	local knobCorner = Instance.new("UICorner")
	knobCorner.CornerRadius = UDim.new(1, 0)
	knobCorner.Parent = knob

	local function knobPos()
		return state[key] and UDim2.new(1, -19, 0.5, -8) or UDim2.new(0, 3, 0.5, -8)
	end
	knob.Position = knobPos()

	local hovered = false
	local function rowColor()
		if hovered then
			return theme.row:Lerp(theme.white, 0.06)
		end
		return theme.row
	end

	local function paint()
		row.BackgroundColor3 = rowColor()
		label.TextColor3 = theme.text
		track.BackgroundColor3 = state[key] and theme.accent or theme.toggleOff
		knob.BackgroundColor3 = theme.white
	end
	table.insert(painters, paint)

	row.MouseEnter:Connect(function()
		hovered = true
		TweenService:Create(row, TWEEN_FAST, { BackgroundColor3 = rowColor() }):Play()
	end)
	row.MouseLeave:Connect(function()
		hovered = false
		TweenService:Create(row, TWEEN_FAST, { BackgroundColor3 = rowColor() }):Play()
	end)
	row.Activated:Connect(function()
		state[key] = not state[key]
		plugin:SetSetting(key, state[key])
		TweenService:Create(track, TWEEN_FAST, { BackgroundColor3 = state[key] and theme.accent or theme.toggleOff }):Play()
		TweenService:Create(knob, TWEEN_FAST, { Position = knobPos() }):Play()
		task.spawn(pushState)
	end)
end

makeToggleRow("Place", "sharePlace")
makeToggleRow("Active script", "shareScript")
makeToggleRow("Mode", "shareMode")
makeToggleRow("Selection", "shareSelection")

local customBox = Instance.new("TextBox")
customBox.Size = UDim2.new(1, 0, 0, 32)
customBox.BorderSizePixel = 0
customBox.ClearTextOnFocus = false
customBox.Font = Enum.Font.Gotham
customBox.TextSize = 13
customBox.PlaceholderText = "Custom status"
customBox.TextXAlignment = Enum.TextXAlignment.Left
customBox.Text = state.custom
customBox.LayoutOrder = nextOrder()
customBox.Parent = body
local customCorner = Instance.new("UICorner")
customCorner.CornerRadius = UDim.new(0, 8)
customCorner.Parent = customBox
local customPad = Instance.new("UIPadding")
customPad.PaddingLeft = UDim.new(0, 10)
customPad.PaddingRight = UDim.new(0, 10)
customPad.Parent = customBox
table.insert(painters, function()
	customBox.BackgroundColor3 = theme.row
	customBox.TextColor3 = theme.text
	customBox.PlaceholderColor3 = theme.sub
end)
customBox.FocusLost:Connect(function()
	if customBox.Text == state.custom then
		return
	end
	state.custom = customBox.Text
	plugin:SetSetting("custom", state.custom)
	task.spawn(pushState)
end)

local shareButton = Instance.new("TextButton")
shareButton.Size = UDim2.new(1, 0, 0, 40)
shareButton.BorderSizePixel = 0
shareButton.AutoButtonColor = false
shareButton.Font = Enum.Font.GothamBold
shareButton.TextSize = 14
shareButton.Text = state.reporting and "Presence on" or "Presence off"
shareButton.LayoutOrder = nextOrder()
shareButton.Parent = body
local shareCorner = Instance.new("UICorner")
shareCorner.CornerRadius = UDim.new(0, 8)
shareCorner.Parent = shareButton

local shareHovered = false
local function shareColor()
	local base = state.reporting and theme.accent or theme.toggleOff
	if shareHovered then
		return base:Lerp(theme.white, 0.08)
	end
	return base
end
table.insert(painters, function()
	shareButton.BackgroundColor3 = shareColor()
	shareButton.TextColor3 = theme.white
end)
shareButton.MouseEnter:Connect(function()
	shareHovered = true
	TweenService:Create(shareButton, TWEEN_FAST, { BackgroundColor3 = shareColor() }):Play()
end)
shareButton.MouseLeave:Connect(function()
	shareHovered = false
	TweenService:Create(shareButton, TWEEN_FAST, { BackgroundColor3 = shareColor() }):Play()
end)
shareButton.Activated:Connect(function()
	state.reporting = not state.reporting
	plugin:SetSetting("reporting", state.reporting)
	shareButton.Text = state.reporting and "Presence on" or "Presence off"
	TweenService:Create(shareButton, TWEEN_FAST, { BackgroundColor3 = shareColor() }):Play()
	setConnected(isConnected)
	task.spawn(pushState)
end)

refreshStudioTheme()
pcall(function()
	settings().Studio.ThemeChanged:Connect(refreshStudioTheme)
end)
repaint()

local placeInfo = { id = -1, name = "", creator = "", triedAt = 0 }

local function resolvePlace()
	local pid = game.PlaceId
	local retry = placeInfo.name == "" and (os.clock() - placeInfo.triedAt) > 60
	if pid ~= placeInfo.id or retry then
		placeInfo.id = pid
		placeInfo.triedAt = os.clock()
		placeInfo.name = ""
		placeInfo.creator = ""
		if pid > 0 then
			local ok, info = pcall(function()
				return MarketplaceService:GetProductInfo(pid)
			end)
			if ok and type(info) == "table" then
				if type(info.Name) == "string" then
					placeInfo.name = info.Name
				end
				if type(info.Creator) == "table" and type(info.Creator.Name) == "string" then
					placeInfo.creator = info.Creator.Name
				end
			end
		end
	end
	local name = placeInfo.name
	if name == "" then
		name = game.Name
	end
	return name, placeInfo.creator
end

local function currentMode()
	if RunService:IsRunning() then
		return "Playtesting"
	end
	local ok, players = pcall(function()
		return Players:GetPlayers()
	end)
	if ok and #players > 0 then
		return "Team Create"
	end
	return "Editing"
end

local lastScriptRef = nil
local lastScriptLen = -1
local lastScriptLines = 0

local function collectState()
	if not state.reporting then
		return { sharing = false }
	end

	local scriptName = ""
	local scriptLines = 0
	if state.shareScript then
		local okScript, activeScript = pcall(function()
			return StudioService.ActiveScript
		end)
		if okScript and activeScript then
			scriptName = activeScript.Name
			local ok, source = pcall(function()
				return activeScript.Source
			end)
			if ok and typeof(source) == "string" then
				if activeScript ~= lastScriptRef or #source ~= lastScriptLen then
					lastScriptRef = activeScript
					lastScriptLen = #source
					local lines = 0
					if source ~= "" then
						lines = 1
						for _ in string.gmatch(source, "\n") do
							lines += 1
						end
					end
					lastScriptLines = lines
				end
				scriptLines = lastScriptLines
			end
		end
	end

	local placeName, creator = "", ""
	local placeId, universeId = 0, 0
	if state.sharePlace then
		placeName, creator = resolvePlace()
		placeId = game.PlaceId
		universeId = game.GameId
	end

	local selectionCount = 0
	local selectionClass = ""
	if state.shareSelection then
		local ok, sel = pcall(function()
			return Selection:Get()
		end)
		if ok and #sel > 0 then
			selectionCount = #sel
			selectionClass = sel[1].ClassName
		end
	end

	return {
		sharing = true,
		place = placeName,
		placeId = placeId,
		universeId = universeId,
		creator = creator,
		script = scriptName,
		scriptLines = scriptLines,
		mode = state.shareMode and currentMode() or "",
		selection = selectionCount,
		selectionClass = selectionClass,
		custom = state.custom,
	}
end

local function post(payload)
	local encoded = HttpService:JSONEncode(payload)
	local ok, response = pcall(function()
		return HttpService:RequestAsync({
			Url = BRIDGE .. "/rpc",
			Method = "POST",
			Headers = { ["Content-Type"] = "application/json" },
			Body = encoded,
		})
	end)
	if ok and response ~= nil and response.Success == true then
		local okJson, decoded = pcall(function()
			return HttpService:JSONDecode(response.Body)
		end)
		if okJson and type(decoded) == "table" then
			applyPalette(decoded.palette)
			if type(decoded.version) == "string" and decoded.version ~= "" then
				local title = "Voidstrap " .. decoded.version
				if widget.Title ~= title then
					widget.Title = title
				end
			end
		end
		return true
	end
	return false
end

local alive = true
plugin.Unloading:Connect(function()
	alive = false
end)

local posting = false
local pending = false
pushState = function()
	if not alive then
		return
	end
	if posting then
		pending = true
		return
	end
	posting = true
	repeat
		pending = false
		setConnected(post(collectState()))
	until not pending or not alive
	posting = false
end

task.spawn(function()
	while alive do
		pushState()
		task.wait(3)
	end
end)

widget:GetPropertyChangedSignal("Enabled"):Connect(function()
	openButton:SetActive(widget.Enabled)
	if widget.Enabled then
		root.GroupTransparency = 1
		TweenService:Create(root, TWEEN_FADE, { GroupTransparency = 0 }):Play()
	end
end)

openButton.Click:Connect(function()
	widget.Enabled = not widget.Enabled
end)
