namespace Voidstrap.Models;

public sealed class EmoteWheelSettings
{
    public string CenterColor { get; set; } = "#0B2A55";

    public string? CenterColorEnd { get; set; }

    public string LineColor { get; set; } = "#4FA8FF";

    public string BackgroundColor { get; set; } = "#06162E";

    public double BackgroundOpacity { get; set; } = 0.6;

    public string GlowColor { get; set; } = "#2F8CFF";

    public string? GlowColorEnd { get; set; }

    public string MarkerColor { get; set; } = "#9FD0FF";

    public double WheelOpacity { get; set; } = 1.0;

    public bool RecolorEditor { get; set; } = true;

    public bool RecolorIcons { get; set; }

    public string IconColor { get; set; } = "#4FA8FF";

    public EmoteImagePlacement CenterImage { get; set; } = new EmoteImagePlacement();

    public EmoteImagePlacement BackgroundImage { get; set; } = new EmoteImagePlacement();

    public string ModName { get; set; } = "Emote wheel";

    public string RobloxVersion { get; set; } = "";

    public DateTime GeneratedUtc { get; set; }
}
