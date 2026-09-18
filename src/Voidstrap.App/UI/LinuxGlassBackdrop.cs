using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Voidstrap.UI;

internal static class LinuxGlassBackdrop
{
	private const double BaseBlurRadius = 72d;
	private const double Overscan = 96d;
	private const int NoiseSize = 64;

	public const string MaterialName = "LinuxGlassMaterial";

	private static ImageBrush? _noiseBrush;

	public static bool IsSupported => Voidstrap.Utility.Platform.IsLinux;

	public static void Attach(Window window, Image? backgroundLayer)
	{
		if (!IsSupported || window?.Content is not Grid root)
			return;

		try
		{
			window.Background = new SolidColorBrush(ResolveThemeColor("WindowBackgroundColorPrimary", Color.FromRgb(30, 11, 47)));
			UIElement material = CreateMaterial(backgroundLayer);
			int index = backgroundLayer is not null ? root.Children.IndexOf(backgroundLayer) + 1 : 0;
			root.Children.Insert(Math.Clamp(index, 0, root.Children.Count), material);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("LinuxGlassBackdrop::Attach", ex);
		}
	}

	private static Grid CreateMaterial(Image? backgroundLayer)
	{
		Grid material = new()
		{
			Name = MaterialName,
			Margin = new Thickness(0 - Overscan),
			IsHitTestVisible = false
		};
		Panel.SetZIndex(material, -1);

		material.Children.Add(new Rectangle { Fill = CreateBaseBrush() });
		material.Children.Add(CreateLightField());

		if (backgroundLayer is not null)
		{
			Image blurred = new()
			{
				Stretch = Stretch.UniformToFill,
				Opacity = 0.82d,
				Effect = new BlurEffect
				{
					Radius = BaseBlurRadius,
					KernelType = KernelType.Gaussian,
					RenderingBias = RenderingBias.Performance
				}
			};
			RenderOptions.SetBitmapScalingMode(blurred, BitmapScalingMode.LowQuality);
			blurred.SetBinding(Image.SourceProperty, new Binding(nameof(Image.Source))
			{
				Source = backgroundLayer,
				Mode = BindingMode.OneWay
			});
			material.Children.Add(blurred);
		}

		material.Children.Add(new Rectangle { Fill = CreateSheenBrush() });

		Rectangle grain = new()
		{
			Fill = CreateNoiseBrush(),
			Opacity = 0.035d
		};
		material.Children.Add(grain);

		return material;
	}

	private static LinearGradientBrush CreateBaseBrush()
	{
		Color primary = ResolveThemeColor("WindowBackgroundColorPrimary", Color.FromRgb(30, 11, 47));
		Color secondary = ResolveThemeColor("WindowBackgroundColorSecondary", Color.FromRgb(19, 7, 36));
		Color tertiary = ResolveThemeColor("WindowBackgroundColorTertiary", secondary);

		LinearGradientBrush brush = new()
		{
			StartPoint = new Point(0d, 0d),
			EndPoint = new Point(1d, 1d)
		};
		brush.GradientStops.Add(new GradientStop(Opaque(Lighten(primary, 0.16d)), 0d));
		brush.GradientStops.Add(new GradientStop(Opaque(secondary), 0.58d));
		brush.GradientStops.Add(new GradientStop(Opaque(Darken(tertiary, 0.22d)), 1d));
		Freeze(brush);
		return brush;
	}

	private static Grid CreateLightField()
	{
		Grid field = new()
		{
			Effect = new BlurEffect
			{
				Radius = BaseBlurRadius,
				KernelType = KernelType.Gaussian,
				RenderingBias = RenderingBias.Performance
			}
		};

		Color accent = ResolveThemeColor("SystemAccentColorSecondary", ResolveThemeColor("SystemAccentColor", Color.FromRgb(96, 52, 168)));
		field.Children.Add(CreateBlob(accent, 0.34d, HorizontalAlignment.Left, VerticalAlignment.Top, 0.92d));
		field.Children.Add(CreateBlob(Lighten(accent, 0.34d), 0.24d, HorizontalAlignment.Right, VerticalAlignment.Center, 0.7d));
		field.Children.Add(CreateBlob(Darken(accent, 0.3d), 0.28d, HorizontalAlignment.Center, VerticalAlignment.Bottom, 0.84d));
		return field;
	}

	private static Ellipse CreateBlob(
		Color color,
		double strength,
		HorizontalAlignment horizontal,
		VerticalAlignment vertical,
		double scale)
	{
		RadialGradientBrush brush = new()
		{
			GradientOrigin = new Point(0.5d, 0.5d),
			Center = new Point(0.5d, 0.5d),
			RadiusX = 0.5d,
			RadiusY = 0.5d
		};
		brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255d * strength), color.R, color.G, color.B), 0d));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1d));
		Freeze(brush);

		return new Ellipse
		{
			Fill = brush,
			Width = 420d * scale,
			Height = 320d * scale,
			HorizontalAlignment = horizontal,
			VerticalAlignment = vertical
		};
	}

	private static LinearGradientBrush CreateSheenBrush()
	{
		LinearGradientBrush brush = new()
		{
			StartPoint = new Point(0d, 0d),
			EndPoint = new Point(0.35d, 1d)
		};
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(38, 255, 255, 255), 0d));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(12, 255, 255, 255), 0.42d));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.72d));
		brush.GradientStops.Add(new GradientStop(Color.FromArgb(26, 0, 0, 0), 1d));
		Freeze(brush);
		return brush;
	}

	private static ImageBrush CreateNoiseBrush()
	{
		if (_noiseBrush is not null)
			return _noiseBrush;

		WriteableBitmap bitmap = new(NoiseSize, NoiseSize, 96d, 96d, PixelFormats.Bgra32, null);
		byte[] pixels = new byte[NoiseSize * NoiseSize * 4];
		Random random = new(0x5EED);
		for (int i = 0; i < pixels.Length; i += 4)
		{
			byte level = (byte)random.Next(96, 224);
			pixels[i] = level;
			pixels[i + 1] = level;
			pixels[i + 2] = level;
			pixels[i + 3] = 255;
		}

		bitmap.WritePixels(new Int32Rect(0, 0, NoiseSize, NoiseSize), pixels, NoiseSize * 4, 0);
		bitmap.Freeze();

		ImageBrush brush = new(bitmap)
		{
			TileMode = TileMode.Tile,
			Viewport = new Rect(0d, 0d, NoiseSize, NoiseSize),
			ViewportUnits = BrushMappingMode.Absolute,
			Stretch = Stretch.None
		};
		Freeze(brush);
		_noiseBrush = brush;
		return brush;
	}

	private static Color ResolveThemeColor(string key, Color fallback)
	{
		try
		{
			if (Application.Current?.Resources[key] is Color color)
				return color;
		}
		catch (Exception)
		{
		}

		return fallback;
	}

	internal static Color Opaque(Color color)
	{
		return Color.FromRgb(color.R, color.G, color.B);
	}

	private static Color Lighten(Color color, double factor)
	{
		return Color.FromRgb(
			(byte)Math.Clamp(color.R + (255 - color.R) * factor, 0d, 255d),
			(byte)Math.Clamp(color.G + (255 - color.G) * factor, 0d, 255d),
			(byte)Math.Clamp(color.B + (255 - color.B) * factor, 0d, 255d));
	}

	private static Color Darken(Color color, double factor)
	{
		return Color.FromRgb(
			(byte)Math.Clamp(color.R * (1d - factor), 0d, 255d),
			(byte)Math.Clamp(color.G * (1d - factor), 0d, 255d),
			(byte)Math.Clamp(color.B * (1d - factor), 0d, 255d));
	}

	private static void Freeze(Freezable value)
	{
		if (value.CanFreeze)
			value.Freeze();
	}
}
