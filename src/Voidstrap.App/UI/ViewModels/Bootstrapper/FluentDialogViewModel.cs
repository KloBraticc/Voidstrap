using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Bootstrapper;

public class FluentDialogViewModel : BootstrapperDialogViewModel
{
	public Brush BackgroundColourBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

	[Obsolete("Do not use this! This is for the designer only.", true)]
	public FluentDialogViewModel()
	{
	}

	public FluentDialogViewModel(IBootstrapperDialog dialog, bool aero)
		: base(dialog)
	{
		if (aero)
		{
			BackgroundColourBrush = ResolveGlassTint();
		}
	}

	private static LinearGradientBrush ResolveGlassTint()
	{
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		//IL_0120: Unknown result type (might be due to invalid IL or missing references)
		Color c = Color.FromRgb(30, 11, 47);
		Color color = Color.FromRgb(19, 7, 36);
		Color c2 = Color.FromRgb(10, 4, 21);
		try
		{
			ResourceDictionary? resourceDictionary = Application.Current?.Resources;
			if (resourceDictionary != null)
			{
				if (resourceDictionary["WindowBackgroundColorPrimary"] is Color color2)
				{
					c = color2;
				}
				if (resourceDictionary["WindowBackgroundColorSecondary"] is Color color3)
				{
					color = color3;
				}
				c2 = ((!((resourceDictionary["WindowBackgroundColorTertiary"] ?? resourceDictionary["WindowBackgroundColorThird"]) is Color color4)) ? color : color4);
			}
		}
		catch
		{
		}
		c = Darken(c, 0.46);
		color = Darken(color, 0.46);
		c2 = Darken(c2, 0.46);
		LinearGradientBrush linearGradientBrush = new LinearGradientBrush
		{
			StartPoint = new Point(0.0, 0.0),
			EndPoint = new Point(1.0, 1.0)
		};
		linearGradientBrush.GradientStops.Add(new GradientStop(Color.FromArgb(158, c.R, c.G, c.B), 0.0));
		linearGradientBrush.GradientStops.Add(new GradientStop(Color.FromArgb(142, color.R, color.G, color.B), 0.55));
		linearGradientBrush.GradientStops.Add(new GradientStop(Color.FromArgb(126, c2.R, c2.G, c2.B), 1.0));
		if (((Freezable)linearGradientBrush).CanFreeze)
		{
			((Freezable)linearGradientBrush).Freeze();
		}
		return linearGradientBrush;
	}

	private static Color Darken(Color c, double factor)
	{
		factor = Math.Clamp(factor, 0.0, 1.0);
		return Color.FromRgb((byte)((double)(int)c.R * factor), (byte)((double)(int)c.G * factor), (byte)((double)(int)c.B * factor));
	}
}
