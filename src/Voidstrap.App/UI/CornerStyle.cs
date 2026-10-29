using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI;

internal static class CornerStyle
{
	public const double MinScale = 0.0;

	public const double MaxScale = 2.0;

	private sealed class Entry
	{
		public CornerRadius Original;

		public CornerRadius Written;
	}

	private static readonly ConditionalWeakTable<Border, Entry> Tracked = new();

	private static readonly List<WeakReference<Border>> Borders = new();

	private static bool _installed;

	private static bool _applyQueued;

	public static WindowCornerStyle WindowStyle => App.Settings?.Prop?.WindowCornerStyle ?? WindowCornerStyle.Rounded;

	public static double ControlScale
	{
		get
		{
			double scale = App.Settings?.Prop?.UiCornerScale ?? 1.0;
			return double.IsFinite(scale) ? Math.Clamp(scale, MinScale, MaxScale) : 1.0;
		}
	}

	public static Wpf.Ui.Appearance.WindowCornerPreference WindowPreference => WindowStyle switch
	{
		WindowCornerStyle.SlightlyRounded => Wpf.Ui.Appearance.WindowCornerPreference.RoundSmall,
		WindowCornerStyle.Square => Wpf.Ui.Appearance.WindowCornerPreference.DoNotRound,
		_ => Wpf.Ui.Appearance.WindowCornerPreference.Round
	};

	public static double WindowRadius(double full)
	{
		return WindowStyle switch
		{
			WindowCornerStyle.SlightlyRounded => full / 2.0,
			WindowCornerStyle.Square => 0.0,
			_ => full
		};
	}

	public static void Install()
	{
		if (_installed)
			return;
		_installed = true;
		EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnBorderSizeChanged));
	}

	public static void ApplyWindowCorners()
	{
		Application app = Application.Current;
		if (app == null)
			return;
		foreach (Window window in app.Windows)
		{
			if (window is not WpfUiWindow uiWindow)
				continue;
			try
			{
				if (Voidstrap.Utility.Platform.IsLinux)
					RoundedWindowChrome.Refresh(uiWindow);
				else
					uiWindow.RefreshCornerStyle();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("CornerStyle::ApplyWindowCorners", "Could not update the corners of " + window.Title + ": " + ex.Message);
			}
		}
	}

	public static void ApplyControlScale()
	{
		if (_applyQueued)
			return;
		Application app = Application.Current;
		if (app == null)
			return;
		_applyQueued = true;
		app.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ApplyQueued));
	}

	private static void ApplyQueued()
	{
		_applyQueued = false;
		double scale = ControlScale;
		for (int index = Borders.Count - 1; index >= 0; index--)
		{
			if (!Borders[index].TryGetTarget(out Border? border) || !Tracked.TryGetValue(border, out Entry? entry))
			{
				Borders.RemoveAt(index);
				continue;
			}
			Apply(border, entry, scale);
		}
	}

	private static void OnBorderSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (sender is not Border border || border is Wpf.Ui.Controls.ClientAreaBorder || Tracked.TryGetValue(border, out _))
			return;
		CornerRadius radius = border.CornerRadius;
		double largest = Math.Max(Math.Max(radius.TopLeft, radius.TopRight), Math.Max(radius.BottomLeft, radius.BottomRight));
		if (largest <= 0.0)
			return;
		double shortest = Math.Min(e.NewSize.Width, e.NewSize.Height);
		if (shortest <= 0.0 || largest >= shortest / 2.0 - 0.5)
			return;
		Entry entry = new Entry { Original = radius, Written = radius };
		Tracked.Add(border, entry);
		Borders.Add(new WeakReference<Border>(border));
		double scale = ControlScale;
		if (scale != 1.0)
			Apply(border, entry, scale);
	}

	private static void Apply(Border border, Entry entry, double scale)
	{
		CornerRadius current = border.CornerRadius;
		if (current != entry.Written)
		{
			entry.Original = current;
		}
		CornerRadius target = scale == 1.0
			? entry.Original
			: new CornerRadius(entry.Original.TopLeft * scale, entry.Original.TopRight * scale, entry.Original.BottomRight * scale, entry.Original.BottomLeft * scale);
		entry.Written = target;
		if (current != target)
			border.SetCurrentValue(Border.CornerRadiusProperty, target);
	}
}
