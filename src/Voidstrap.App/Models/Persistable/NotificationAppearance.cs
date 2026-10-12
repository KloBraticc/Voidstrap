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

// Corner sits flush against the edges it touches, Floating keeps a gap from every edge
public enum NotificationLayout
{
	Corner,
	Floating
}

// Which notifications show the small Voidstrap header above their text
public enum NotificationHeader
{
	Off,
	Server,
	Friends,
	Both
}

// How the in game notification looks and moves. Values are clamped when read so a hand edited settings
// file can never produce a broken notification.
public sealed class NotificationAppearance
{
	public const double BaseWidth = 392;
	public const double MinWidth = 320;
	public const double MaxWidth = 480;
	public const double FloatingGap = 16;

	// Only read to move settings saved before the position sliders existed
	public NotificationPosition Position { get; set; } = NotificationPosition.BottomRight;

	// Gap from the screen edges in the Floating layout; 0 means the usual 16
	public double EdgeSpacing { get; set; } = 0;

	// Width of the card before Size scales it
	public double Width { get; set; } = BaseWidth;

	public bool PositionMigrated { get; set; }

	public NotificationLayout Layout { get; set; } = NotificationLayout.Corner;

	// 0 is the left or top edge, 100 the right or bottom edge
	public double Horizontal { get; set; } = 100;

	public double Vertical { get; set; } = 100;

	public double CornerRadius { get; set; } = 8;

	public double Size { get; set; } = 100;

	public double TextSize { get; set; } = 100;

	public double BackgroundOpacity { get; set; } = 1.0;

	public NotificationMotion Intro { get; set; } = NotificationMotion.Slide;

	public NotificationMotion Outro { get; set; } = NotificationMotion.Slide;

	public int IntroMilliseconds { get; set; } = 260;

	public int OutroMilliseconds { get; set; } = 320;

	public double SecondsOnScreen { get; set; } = 8;

	public bool PauseWhileHovered { get; set; } = true;

	public bool CloseButtonOnHover { get; set; } = true;

	public NotificationHeader Header { get; set; } = NotificationHeader.Off;

	public double SafeHorizontal => Clamp(Horizontal, 0, 100, 100);

	public double SafeVertical => Clamp(Vertical, 0, 100, 100);

	public double SafeEdgeSpacing => Layout != NotificationLayout.Floating ? 0
		: double.IsFinite(EdgeSpacing) && EdgeSpacing > 0 ? Math.Clamp(EdgeSpacing, 4, 48) : FloatingGap;

	public double SafeCardWidth => Clamp(Width, MinWidth, MaxWidth, BaseWidth);

	public double SafeCornerRadius => Clamp(CornerRadius, 0, 20, 8);

	public double SafeScale => Clamp(Size, 80, 130, 100) / 100;

	public double SafeTextScale => Clamp(TextSize, 80, 140, 100) / 100;

	// Width of the card on screen, the card is laid out at its width and then scaled
	public double SafeWidth => SafeCardWidth * SafeScale;

	public double SafeBackgroundOpacity => Clamp(BackgroundOpacity, 0.6, 1.0, 1.0);

	public int SafeIntroMilliseconds => (int)Clamp(IntroMilliseconds, 100, 1000, 260);

	public int SafeOutroMilliseconds => (int)Clamp(OutroMilliseconds, 100, 1000, 320);

	public double SafeSecondsOnScreen => Clamp(SecondsOnScreen, 2, 20, 8);

	public bool IsTop => SafeVertical < 50;

	public bool IsLeft => SafeHorizontal <= 0;

	public bool IsRight => SafeHorizontal >= 100;

	// The edges the card actually touches, only in the Corner layout
	public bool TouchesLeft => Layout == NotificationLayout.Corner && SafeHorizontal <= 0;

	public bool TouchesRight => Layout == NotificationLayout.Corner && SafeHorizontal >= 100;

	public bool TouchesTop => Layout == NotificationLayout.Corner && SafeVertical <= 0;

	public bool TouchesBottom => Layout == NotificationLayout.Corner && SafeVertical >= 100;

	public bool ShowsHeader(bool friends) => Header == NotificationHeader.Both || Header == (friends ? NotificationHeader.Friends : NotificationHeader.Server);

	public NotificationAppearance Copy() => (NotificationAppearance)MemberwiseClone();

	// Settings saved before the sliders used six fixed positions and a spacing value
	public void Migrate()
	{
		if (PositionMigrated)
			return;
		PositionMigrated = true;
		Horizontal = Position switch
		{
			NotificationPosition.TopLeft or NotificationPosition.BottomLeft => 0,
			NotificationPosition.TopCenter or NotificationPosition.BottomCenter => 50,
			_ => 100
		};
		Vertical = Position is NotificationPosition.TopLeft or NotificationPosition.TopCenter or NotificationPosition.TopRight ? 0 : 100;
		Layout = double.IsFinite(EdgeSpacing) && EdgeSpacing > 0 ? NotificationLayout.Floating : NotificationLayout.Corner;
	}

	private static double Clamp(double value, double min, double max, double fallback)
		=> double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
