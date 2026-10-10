using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Voidstrap.Models.Persistable;

namespace Voidstrap.UI;

// Shared by the in game notification and the preview in its settings, so the preview is exactly what shows
internal static class NotificationStyle
{
	private const int FrameRate = 60;

	// Room the soft shadow needs around the card
	public const double ShadowSize = 24;

	// One shared, frozen shadow: a soft dark glow that sits slightly low, like a floating card
	public static readonly DropShadowEffect Shadow = CreateShadow();

	private static DropShadowEffect CreateShadow()
	{
		DropShadowEffect shadow = new()
		{
			Color = Colors.Black,
			BlurRadius = 26,
			ShadowDepth = 3,
			Direction = 270,
			Opacity = 0.55,
			RenderingBias = RenderingBias.Performance
		};
		shadow.Freeze();
		return shadow;
	}

	// Full room on the sides facing into the game, and only as much as the gap allows on a side against an edge,
	// so a flush notification stays flush and the shadow never spills outside the game
	public static Thickness ShadowMargins(NotificationAppearance appearance)
	{
		double edge = Math.Min(ShadowSize, appearance.SafeEdgeSpacing);
		bool top = appearance.IsTop;
		return new Thickness(
			appearance.IsLeft ? edge : ShadowSize,
			top ? edge : ShadowSize,
			appearance.IsRight ? edge : ShadowSize,
			top ? ShadowSize : edge);
	}

	public static NotificationAppearance Current => App.Settings.Prop.NotificationAppearance ?? new NotificationAppearance();

	// Flush with the screen, the corners that touch an edge stay square; with spacing every corner is rounded
	public static CornerRadius Corners(NotificationAppearance appearance)
	{
		double radius = appearance.SafeCornerRadius;
		if (appearance.SafeEdgeSpacing > 0)
			return new CornerRadius(radius);
		bool top = appearance.IsTop;
		bool left = appearance.IsLeft;
		bool right = appearance.IsRight;
		return new CornerRadius(
			top || left ? 0 : radius,
			top || right ? 0 : radius,
			!top || right ? 0 : radius,
			!top || left ? 0 : radius);
	}

	public static Thickness Borders(NotificationAppearance appearance)
	{
		if (appearance.SafeEdgeSpacing > 0)
			return new Thickness(1);
		return new Thickness(
			appearance.IsLeft ? 0 : 1,
			appearance.IsTop ? 0 : 1,
			appearance.IsRight ? 0 : 1,
			appearance.IsTop ? 1 : 0);
	}

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
	public static void PrepareIntro(UIElement card, TranslateTransform translate, ScaleTransform scale, NotificationAppearance appearance, double height)
	{
		Stop(card, translate, scale);
		bool animate = Length(appearance, true) > 0;
		card.Opacity = animate ? 0 : 1;
		translate.X = 0;
		translate.Y = animate && appearance.Intro == NotificationMotion.Slide ? SlideOffset(appearance, height) : 0;
		scale.ScaleX = scale.ScaleY = animate && appearance.Intro == NotificationMotion.Pop ? 0.9 : 1;
	}

	public static void Play(UIElement card, TranslateTransform translate, ScaleTransform scale, NotificationAppearance appearance, double height, bool intro)
	{
		NotificationMotion motion = intro ? appearance.Intro : appearance.Outro;
		int length = Length(appearance, intro);
		double fromOpacity = card.Opacity;
		double fromY = translate.Y;
		double fromScale = scale.ScaleX;
		Stop(card, translate, scale);
		double toOpacity = intro ? 1 : 0;
		double toY = !intro && motion == NotificationMotion.Slide ? SlideOffset(appearance, height) : 0;
		double toScale = !intro && motion == NotificationMotion.Pop ? 0.94 : 1;
		if (length == 0)
		{
			card.Opacity = toOpacity;
			translate.Y = intro ? 0 : translate.Y;
			scale.ScaleX = scale.ScaleY = 1;
			return;
		}
		IEasingFunction ease = intro
			? new CubicEase { EasingMode = EasingMode.EaseOut }
			: motion == NotificationMotion.Pop ? new QuadraticEase { EasingMode = EasingMode.EaseIn } : new SineEase { EasingMode = EasingMode.EaseInOut };
		Duration duration = new(TimeSpan.FromMilliseconds(length));
		card.BeginAnimation(UIElement.OpacityProperty, Frozen(new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease }));
		if (Math.Abs(fromY - toY) > 0.1)
			translate.BeginAnimation(TranslateTransform.YProperty, Frozen(new DoubleAnimation(fromY, toY, duration) { EasingFunction = ease }));
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
		double y = translate.Y;
		double size = scale.ScaleX;
		card.BeginAnimation(UIElement.OpacityProperty, null);
		translate.BeginAnimation(TranslateTransform.YProperty, null);
		scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
		scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
		card.Opacity = opacity;
		translate.Y = y;
		scale.ScaleX = scale.ScaleY = size;
	}

	// Slides in from and out to the screen edge it sits against
	private static double SlideOffset(NotificationAppearance appearance, double height)
		=> appearance.IsTop ? -Math.Max(1, height) : Math.Max(1, height);

	private static AnimationTimeline Frozen(AnimationTimeline timeline)
	{
		Timeline.SetDesiredFrameRate(timeline, FrameRate);
		timeline.Freeze();
		return timeline;
	}
}
