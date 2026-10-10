using System;

namespace Voidstrap.Models.Persistable;

public enum NotificationPosition
{
	TopLeft,
	TopCenter,
	TopRight,
	BottomLeft,
	BottomCenter,
	BottomRight
}

public enum NotificationMotion
{
	Slide,
	Fade,
	Pop,
	None
}

// How the in game notification looks and moves. Values are clamped when read so a hand edited settings
// file can never produce a broken notification.
public sealed class NotificationAppearance
{
	public const double MinWidth = 320;
	public const double MaxWidth = 480;

	public NotificationPosition Position { get; set; } = NotificationPosition.BottomRight;

	public double EdgeSpacing { get; set; } = 0;

	public double CornerRadius { get; set; } = 8;

	public double Width { get; set; } = 392;

	public double BackgroundOpacity { get; set; } = 1.0;

	public NotificationMotion Intro { get; set; } = NotificationMotion.Slide;

	public NotificationMotion Outro { get; set; } = NotificationMotion.Slide;

	public int IntroMilliseconds { get; set; } = 260;

	public int OutroMilliseconds { get; set; } = 320;

	public double SecondsOnScreen { get; set; } = 8;

	public bool PauseWhileHovered { get; set; } = true;

	public bool CloseButtonOnHover { get; set; } = true;

	public double SafeEdgeSpacing => Clamp(EdgeSpacing, 0, 48, 0);

	public double SafeCornerRadius => Clamp(CornerRadius, 0, 20, 8);

	public double SafeWidth => Clamp(Width, MinWidth, MaxWidth, 392);

	public double SafeBackgroundOpacity => Clamp(BackgroundOpacity, 0.6, 1.0, 1.0);

	public int SafeIntroMilliseconds => (int)Clamp(IntroMilliseconds, 100, 1000, 260);

	public int SafeOutroMilliseconds => (int)Clamp(OutroMilliseconds, 100, 1000, 320);

	public double SafeSecondsOnScreen => Clamp(SecondsOnScreen, 2, 20, 8);

	public bool IsTop => Position is NotificationPosition.TopLeft or NotificationPosition.TopCenter or NotificationPosition.TopRight;

	public bool IsLeft => Position is NotificationPosition.TopLeft or NotificationPosition.BottomLeft;

	public bool IsRight => Position is NotificationPosition.TopRight or NotificationPosition.BottomRight;

	public NotificationAppearance Copy() => (NotificationAppearance)MemberwiseClone();

	private static double Clamp(double value, double min, double max, double fallback)
		=> double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
