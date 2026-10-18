using System;
using System.Text.Json.Serialization;

namespace Voidstrap.Models.Persistable;

public sealed class OverlayLayoutSettings
{
    public const double DefaultReadoutHorizontal = 100;
    public const double DefaultReadoutVertical = 0;
    public const double DefaultReadoutEdgeSpacing = 12;
    public const double DefaultReadoutScale = 1.0;
    public const double DefaultReadoutOpacity = 0.92;
    public const double DefaultReadoutCornerRadius = 8;

    public const double DefaultDockHorizontal = 50;
    public const double DefaultDockEdgeSpacing = 10;
    public const double DefaultDockScale = 1.0;
    public const double DefaultDockBackdropOpacity = -1;

    public const double DefaultFrameGenHudHorizontal = 2;
    public const double DefaultFrameGenHudVertical = 14;
    public const double DefaultFrameGenHudEdgeSpacing = 18;

    public double ReadoutHorizontal { get; set; } = DefaultReadoutHorizontal;
    public double ReadoutVertical { get; set; } = DefaultReadoutVertical;
    public double ReadoutEdgeSpacing { get; set; } = DefaultReadoutEdgeSpacing;
    public double ReadoutScale { get; set; } = DefaultReadoutScale;
    public double ReadoutOpacity { get; set; } = DefaultReadoutOpacity;
    public double ReadoutCornerRadius { get; set; } = DefaultReadoutCornerRadius;
    public bool ReadoutHorizontalLayout { get; set; }

    public bool DockTopEdge { get; set; }
    public double DockHorizontal { get; set; } = DefaultDockHorizontal;
    public double DockEdgeSpacing { get; set; } = DefaultDockEdgeSpacing;
    public double DockScale { get; set; } = DefaultDockScale;
    public double DockBackdropOpacity { get; set; } = DefaultDockBackdropOpacity;

    public int CrosshairOffsetX { get; set; }
    public int CrosshairOffsetY { get; set; }

    public bool FrameGenHudCustomPosition { get; set; }
    public double FrameGenHudHorizontal { get; set; } = DefaultFrameGenHudHorizontal;
    public double FrameGenHudVertical { get; set; } = DefaultFrameGenHudVertical;
    public int FrameGenHudEdgeSpacing { get; set; } = (int)DefaultFrameGenHudEdgeSpacing;

    [JsonIgnore]
    public double SafeReadoutHorizontal => Clamp(ReadoutHorizontal, 0, 100, DefaultReadoutHorizontal);

    [JsonIgnore]
    public double SafeReadoutVertical => Clamp(ReadoutVertical, 0, 100, DefaultReadoutVertical);

    [JsonIgnore]
    public double SafeReadoutEdgeSpacing => Clamp(ReadoutEdgeSpacing, 0, 64, DefaultReadoutEdgeSpacing);

    [JsonIgnore]
    public double SafeReadoutScale => Clamp(ReadoutScale, 0.70, 1.50, DefaultReadoutScale);

    [JsonIgnore]
    public double SafeReadoutOpacity => Clamp(ReadoutOpacity, 0.35, 1.0, DefaultReadoutOpacity);

    [JsonIgnore]
    public double SafeReadoutCornerRadius => Clamp(ReadoutCornerRadius, 0, 20, DefaultReadoutCornerRadius);

    [JsonIgnore]
    public double SafeDockHorizontal => Clamp(DockHorizontal, 0, 100, DefaultDockHorizontal);

    [JsonIgnore]
    public double SafeDockEdgeSpacing => Clamp(DockEdgeSpacing, 4, 48, DefaultDockEdgeSpacing);

    [JsonIgnore]
    public double SafeDockScale => Clamp(DockScale, 0.80, 1.20, DefaultDockScale);

    [JsonIgnore]
    public int SafeCrosshairOffsetX => Math.Clamp(CrosshairOffsetX, -200, 200);

    [JsonIgnore]
    public int SafeCrosshairOffsetY => Math.Clamp(CrosshairOffsetY, -200, 200);

    [JsonIgnore]
    public double SafeFrameGenHudHorizontal => Clamp(FrameGenHudHorizontal, 0, 100, DefaultFrameGenHudHorizontal);

    [JsonIgnore]
    public double SafeFrameGenHudVertical => Clamp(FrameGenHudVertical, 0, 100, DefaultFrameGenHudVertical);

    [JsonIgnore]
    public int SafeFrameGenHudEdgeSpacing => Math.Clamp(FrameGenHudEdgeSpacing, 0, 64);

    public double ResolveDockBackdropOpacity(double platformDefault)
    {
        if (!double.IsFinite(DockBackdropOpacity) || DockBackdropOpacity < 0)
            return Math.Clamp(platformDefault, 0, 0.80);
        return Math.Clamp(DockBackdropOpacity, 0, 0.80);
    }

    public OverlayLayoutSettings Copy() => new()
    {
        ReadoutHorizontal = SafeReadoutHorizontal,
        ReadoutVertical = SafeReadoutVertical,
        ReadoutEdgeSpacing = SafeReadoutEdgeSpacing,
        ReadoutScale = SafeReadoutScale,
        ReadoutOpacity = SafeReadoutOpacity,
        ReadoutCornerRadius = SafeReadoutCornerRadius,
        ReadoutHorizontalLayout = ReadoutHorizontalLayout,
        DockTopEdge = DockTopEdge,
        DockHorizontal = SafeDockHorizontal,
        DockEdgeSpacing = SafeDockEdgeSpacing,
        DockScale = SafeDockScale,
        DockBackdropOpacity = double.IsFinite(DockBackdropOpacity) ? DockBackdropOpacity : DefaultDockBackdropOpacity,
        CrosshairOffsetX = SafeCrosshairOffsetX,
        CrosshairOffsetY = SafeCrosshairOffsetY,
        FrameGenHudCustomPosition = FrameGenHudCustomPosition,
        FrameGenHudHorizontal = SafeFrameGenHudHorizontal,
        FrameGenHudVertical = SafeFrameGenHudVertical,
        FrameGenHudEdgeSpacing = SafeFrameGenHudEdgeSpacing
    };

    public void ResetReadout()
    {
        ReadoutHorizontal = DefaultReadoutHorizontal;
        ReadoutVertical = DefaultReadoutVertical;
        ReadoutEdgeSpacing = DefaultReadoutEdgeSpacing;
        ReadoutScale = DefaultReadoutScale;
        ReadoutOpacity = DefaultReadoutOpacity;
        ReadoutCornerRadius = DefaultReadoutCornerRadius;
        ReadoutHorizontalLayout = false;
    }

    public void ResetDock()
    {
        DockTopEdge = false;
        DockHorizontal = DefaultDockHorizontal;
        DockEdgeSpacing = DefaultDockEdgeSpacing;
        DockScale = DefaultDockScale;
        DockBackdropOpacity = DefaultDockBackdropOpacity;
    }

    public void ResetCrosshairOffset()
    {
        CrosshairOffsetX = 0;
        CrosshairOffsetY = 0;
    }

    public void ResetFrameGenHud()
    {
        FrameGenHudCustomPosition = false;
        FrameGenHudHorizontal = DefaultFrameGenHudHorizontal;
        FrameGenHudVertical = DefaultFrameGenHudVertical;
        FrameGenHudEdgeSpacing = (int)DefaultFrameGenHudEdgeSpacing;
    }

    public void ResetAll()
    {
        ResetReadout();
        ResetDock();
        ResetCrosshairOffset();
        ResetFrameGenHud();
    }

    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
