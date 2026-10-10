using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Voidstrap.Models.Persistable;

namespace Voidstrap.UI;

// What a notification is about, decides whether it gets the Voidstrap header
public enum NotificationKind
{
	General,
	Server,
	Friends
}

// Shared by the in game notification and the preview in its settings, so the preview is exactly what shows
internal static class NotificationStyle
{
	private const int FrameRate = 60;

	// Room the soft shadow needs around the card
	public const double ShadowSize = 24;

	public static NotificationAppearance Current
	{
		get
		{
			NotificationAppearance appearance = App.Settings.Prop.NotificationAppearance ??= new NotificationAppearance();
			appearance.Migrate();
			return appearance;
		}
	}

	// No room on a side flush with an edge, the gap's worth against an edge when floating, full room elsewhere,
	// so a flush notification stays flush and the shadow never spills far outside the game
	public static Thickness ShadowMargins(NotificationAppearance appearance)
	{
		double edge = Math.Min(ShadowSize, appearance.SafeEdgeSpacing);
		return new Thickness(
			appearance.SafeHorizontal <= 0 ? edge : ShadowSize,
			appearance.SafeVertical <= 0 ? edge : ShadowSize,
			appearance.SafeHorizontal >= 100 ? edge : ShadowSize,
			appearance.SafeVertical >= 100 ? edge : ShadowSize);
	}

	// Where the card's top left corner goes inside an area, the sliders run from one edge to the other
	public static Point Place(NotificationAppearance appearance, double areaWidth, double areaHeight, double cardWidth, double cardHeight, double gap)
	{
		double x = gap + Math.Max(0, areaWidth - cardWidth - gap * 2) * appearance.SafeHorizontal / 100;
		double y = gap + Math.Max(0, areaHeight - cardHeight - gap * 2) * appearance.SafeVertical / 100;
		return new Point(x, y);
	}

	// A corner that touches an edge stays square, the rest are rounded
	public static CornerRadius Corners(NotificationAppearance appearance)
	{
		double radius = appearance.SafeCornerRadius;
		bool left = appearance.TouchesLeft;
		bool right = appearance.TouchesRight;
		bool top = appearance.TouchesTop;
		bool bottom = appearance.TouchesBottom;
		return new CornerRadius(
			top || left ? 0 : radius,
			top || right ? 0 : radius,
			bottom || right ? 0 : radius,
			bottom || left ? 0 : radius);
	}

	public static Thickness Borders(NotificationAppearance appearance)
		=> new(appearance.TouchesLeft ? 0 : 1, appearance.TouchesTop ? 0 : 1, appearance.TouchesRight ? 0 : 1, appearance.TouchesBottom ? 0 : 1);

	public static Brush Background(FrameworkElement owner, NotificationAppearance appearance)
	{
		Color color = owner.TryFindResource("SolidBackgroundFillColorBaseBrush") is SolidColorBrush brush ? brush.Color : Color.FromRgb(0x20, 0x20, 0x20);
		SolidColorBrush background = new(color) { Opacity = appearance.SafeBackgroundOpacity };
		background.Freeze();
		return background;
	}

	public static int Length(NotificationAppearance appearance, bool intro)
	{
		NotificationMotion motion = intro ? appearance.Intro : appearance.Outro;
		if (motion == NotificationMotion.None || !SystemParameters.ClientAreaAnimation)
			return 0;
		return intro ? appearance.SafeIntroMilliseconds : appearance.SafeOutroMilliseconds;
	}

	// Puts the card where an intro starts from, before it is shown
	public static void PrepareIntro(UIElement card, TranslateTransform translate, ScaleTransform scale, NotificationAppearance appearance, Size size)
	{
		Stop(card, translate, scale);
		bool animate = Length(appearance, true) > 0;
		Vector offset = animate && appearance.Intro == NotificationMotion.Slide ? SlideOffset(appearance, size) : default;
		card.Opacity = animate ? 0 : 1;
		translate.X = offset.X;
		translate.Y = offset.Y;
		scale.ScaleX = scale.ScaleY = animate && appearance.Intro == NotificationMotion.Pop ? 0.9 : 1;
	}

	public static void Play(UIElement card, TranslateTransform translate, ScaleTransform scale, NotificationAppearance appearance, Size size, bool intro)
	{
		NotificationMotion motion = intro ? appearance.Intro : appearance.Outro;
		int length = Length(appearance, intro);
		double fromOpacity = card.Opacity;
		Vector from = new(translate.X, translate.Y);
		double fromScale = scale.ScaleX;
		Stop(card, translate, scale);
		double toOpacity = intro ? 1 : 0;
		Vector to = !intro && motion == NotificationMotion.Slide ? SlideOffset(appearance, size) : default;
		double toScale = !intro && motion == NotificationMotion.Pop ? 0.94 : 1;
		if (length == 0)
		{
			card.Opacity = toOpacity;
			if (intro)
				translate.X = translate.Y = 0;
			scale.ScaleX = scale.ScaleY = 1;
			return;
		}
		IEasingFunction ease = intro
			? new CubicEase { EasingMode = EasingMode.EaseOut }
			: motion == NotificationMotion.Pop ? new QuadraticEase { EasingMode = EasingMode.EaseIn } : new SineEase { EasingMode = EasingMode.EaseInOut };
		Duration duration = new(TimeSpan.FromMilliseconds(length));
		card.BeginAnimation(UIElement.OpacityProperty, Frozen(new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease }));
		if (Math.Abs(from.X - to.X) > 0.1)
			translate.BeginAnimation(TranslateTransform.XProperty, Frozen(new DoubleAnimation(from.X, to.X, duration) { EasingFunction = ease }));
		if (Math.Abs(from.Y - to.Y) > 0.1)
			translate.BeginAnimation(TranslateTransform.YProperty, Frozen(new DoubleAnimation(from.Y, to.Y, duration) { EasingFunction = ease }));
		if (Math.Abs(fromScale - toScale) > 0.001)
		{
			AnimationTimeline grow = Frozen(new DoubleAnimation(fromScale, toScale, duration) { EasingFunction = ease });
			scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
			scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
		}
	}

	public static void Stop(UIElement card, TranslateTransform translate, ScaleTransform scale)
	{
		double opacity = card.Opacity;
		double x = translate.X;
		double y = translate.Y;
		double size = scale.ScaleX;
		card.BeginAnimation(UIElement.OpacityProperty, null);
		translate.BeginAnimation(TranslateTransform.XProperty, null);
		translate.BeginAnimation(TranslateTransform.YProperty, null);
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		card.Opacity = opacity;
		translate.X = x;
		translate.Y = y;
		scale.ScaleX = scale.ScaleY = size;
	}

	// Slides in from and out to the screen edge it is closest to
	private static Vector SlideOffset(NotificationAppearance appearance, Size size)
	{
		Vector direction = SlideDirection(appearance);
		return new Vector(direction.X * Math.Max(1, size.Width), direction.Y * Math.Max(1, size.Height));
	}

	// Which way the nearest edge is: (0, 1) is the bottom, (1, 0) the right side
	public static Vector SlideDirection(NotificationAppearance appearance)
	{
		double horizontal = appearance.SafeHorizontal;
		double vertical = appearance.SafeVertical;
		double fromSide = Math.Min(horizontal, 100 - horizontal);
		double fromTopOrBottom = Math.Min(vertical, 100 - vertical);
		if (fromTopOrBottom <= fromSide)
			return new Vector(0, vertical < 50 ? -1 : 1);
		return new Vector(horizontal < 50 ? -1 : 1, 0);
	}

	// A soft shadow made of a few faint rounded rings, drawn once. A blur effect looks about the same but is
	// recalculated whenever anything changes, which made transparent overlay windows stutter.
	private static readonly (double Spread, double Drop, byte Alpha)[] Rings =
	{
		(2, 1, 0x14), (4, 2, 0x10), (6, 3, 0x0D), (9, 4, 0x0A), (12, 6, 0x07), (16, 8, 0x04)
	};

	public static void FillShadow(System.Windows.Controls.Panel host, CornerRadius corners)
	{
		host.Children.Clear();
		host.IsHitTestVisible = false;
		foreach ((double spread, double drop, byte alpha) in Rings)
		{
			SolidColorBrush fill = new(Color.FromArgb(alpha, 0, 0, 0));
			fill.Freeze();
			host.Children.Add(new System.Windows.Controls.Border
			{
				Margin = new Thickness(-spread, -spread + drop, -spread, -spread - drop),
				CornerRadius = new CornerRadius(corners.TopLeft + spread, corners.TopRight + spread, corners.BottomRight + spread, corners.BottomLeft + spread),
				Background = fill,
				SnapsToDevicePixels = true
			});
		}
	}

	private static AnimationTimeline Frozen(AnimationTimeline timeline)
	{
		Timeline.SetDesiredFrameRate(timeline, FrameRate);
		timeline.Freeze();
		return timeline;
	}
}
