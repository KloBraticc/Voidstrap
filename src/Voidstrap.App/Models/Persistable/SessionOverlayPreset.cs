using System;
using System.Text.Json;

namespace Voidstrap.Models.Persistable;

public sealed class SessionOverlayPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public SessionOverlayPresetData Settings { get; set; } = new();
}

public sealed class SessionOverlayPresetData
{
    public OverlayLayoutSettings OverlayLayout { get; set; } = new();

    public NotificationAppearance NotificationAppearance { get; set; } = new();

    public bool ShowServerDetailsUI { get; set; }
    public bool CurrentTimeDisplay { get; set; }
    public bool Clock24Hour { get; set; }

    public bool Crosshair { get; set; }
    public int CrosshairShapeIndex { get; set; }
    public string CrosshairColorHex { get; set; } = "#00FF00";
    public string CrosshairOutlineColorHex { get; set; } = "#000000";
    public int CrosshairSize { get; set; }
    public int CrosshairLineThickness { get; set; }
    public int CrosshairOutlineThickness { get; set; }
    public int CrosshairGap { get; set; }
    public double CrosshairOpacity { get; set; }
    public string CrosshairImagePath { get; set; } = "";

    public bool KeystrokesOverlayEnabled { get; set; }
    public bool KeystrokesShowKeys { get; set; }
    public bool KeystrokesShowMouseButtons { get; set; }
    public bool KeystrokesShowSpaceAndShift { get; set; }
    public bool KeystrokesShowCps { get; set; }

    public bool MusicIslandOverlay { get; set; }
    public MusicIslandPosition MusicIslandPosition { get; set; }

    public static SessionOverlayPresetData Capture(AppSettings settings) => new()
    {
        OverlayLayout = settings.OverlayLayout?.Copy() ?? new OverlayLayoutSettings(),
        NotificationAppearance = settings.NotificationAppearance?.Copy() ?? new NotificationAppearance(),
        ShowServerDetailsUI = settings.ShowServerDetailsUI,
        CurrentTimeDisplay = settings.CurrentTimeDisplay,
        Clock24Hour = settings.Clock24Hour,
        Crosshair = settings.Crosshair,
        CrosshairShapeIndex = settings.CrosshairShapeIndex,
        CrosshairColorHex = settings.CrosshairColorHex ?? "#00FF00",
        CrosshairOutlineColorHex = settings.CrosshairOutlineColorHex ?? "#000000",
        CrosshairSize = settings.CrosshairSize,
        CrosshairLineThickness = settings.CrosshairLineThickness,
        CrosshairOutlineThickness = settings.CrosshairOutlineThickness,
        CrosshairGap = settings.CrosshairGap,
        CrosshairOpacity = settings.CrosshairOpacity,
        CrosshairImagePath = settings.CrosshairImagePath ?? "",
        KeystrokesOverlayEnabled = settings.KeystrokesOverlayEnabled,
        KeystrokesShowKeys = settings.KeystrokesShowKeys,
        KeystrokesShowMouseButtons = settings.KeystrokesShowMouseButtons,
        KeystrokesShowSpaceAndShift = settings.KeystrokesShowSpaceAndShift,
        KeystrokesShowCps = settings.KeystrokesShowCps,
        MusicIslandOverlay = settings.MusicIslandOverlay,
        MusicIslandPosition = settings.MusicIslandPosition,
    };

    public SessionOverlayPresetData Copy() => JsonSerializer.Deserialize<SessionOverlayPresetData>(JsonSerializer.Serialize(this)) ?? new();

    public bool Matches(SessionOverlayPresetData? other) =>
        other != null && string.Equals(JsonSerializer.Serialize(this), JsonSerializer.Serialize(other), StringComparison.Ordinal);

    public void ApplyTo(AppSettings settings)
    {
        settings.OverlayLayout = OverlayLayout?.Copy() ?? new OverlayLayoutSettings();
        settings.NotificationAppearance = NotificationAppearance?.Copy() ?? new NotificationAppearance();
        settings.ShowServerDetailsUI = ShowServerDetailsUI;
        settings.CurrentTimeDisplay = CurrentTimeDisplay;
        settings.Clock24Hour = Clock24Hour;
        settings.Crosshair = Crosshair;
        settings.CrosshairShapeIndex = CrosshairShapeIndex;
        settings.CrosshairColorHex = CrosshairColorHex ?? "#00FF00";
        settings.CrosshairOutlineColorHex = CrosshairOutlineColorHex ?? "#000000";
        settings.CrosshairSize = CrosshairSize;
        settings.CrosshairLineThickness = CrosshairLineThickness;
        settings.CrosshairOutlineThickness = CrosshairOutlineThickness;
        settings.CrosshairGap = CrosshairGap;
        settings.CrosshairOpacity = CrosshairOpacity;
        settings.CrosshairImagePath = CrosshairImagePath ?? "";
        settings.KeystrokesOverlayEnabled = KeystrokesOverlayEnabled;
        settings.KeystrokesShowKeys = KeystrokesShowKeys;
        settings.KeystrokesShowMouseButtons = KeystrokesShowMouseButtons;
        settings.KeystrokesShowSpaceAndShift = KeystrokesShowSpaceAndShift;
        settings.KeystrokesShowCps = KeystrokesShowCps;
        settings.MusicIslandOverlay = MusicIslandOverlay;
        settings.MusicIslandPosition = MusicIslandPosition;
    }
}
