using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#if CROSSPLAT
using System.Windows.Media.ProGPU;
#endif
using System.Windows.Threading;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations.Overlays;

internal readonly record struct LinuxHomepageVisualSettings(
	string Mode,
	string Color,
	string GradientColor,
	double GradientAngle,
	string MediaPath,
	double Saturation = 100d,
	double Contrast = 100d,
	double ColorTemperature = 0d,
	bool ColorBlindnessEnabled = false,
	int ColorBlindnessType = 1,
	double ColorBlindnessSeverity = 100d,
	bool ColorBlindnessSimulate = false)
{
	public static LinuxHomepageVisualSettings Read()
	{
		return new LinuxHomepageVisualSettings(
			OverlaySettings.HomepageBackgroundMode,
			App.Settings.Prop.HomepageBackgroundOverlayColor ?? string.Empty,
			App.Settings.Prop.HomepageBackgroundOverlayGradientColor ?? string.Empty,
			App.Settings.Prop.HomepageBackgroundOverlayGradientAngle,
			App.Settings.Prop.HomepageBackgroundOverlayMediaPath ?? string.Empty,
			App.Settings.Prop.Saturation,
			App.Settings.Prop.Contrast,
			App.Settings.Prop.ColorTemperature,
			App.Settings.Prop.ColorBlindnessEnabled,
			App.Settings.Prop.ColorBlindnessType,
			App.Settings.Prop.ColorBlindnessSeverity,
			App.Settings.Prop.ColorBlindnessSimulate);
	}

	public HomepageBackgroundColorTransform CreateColorTransform()
	{
		return HomepageBackgroundColorTransform.Create(
			Saturation,
			Contrast,
			ColorTemperature,
			ColorBlindnessEnabled,
			ColorBlindnessType,
			ColorBlindnessSeverity,
			ColorBlindnessSimulate);
	}
}

internal static class LinuxHomepageBackgroundMask
{
	internal const byte OriginalBlue = 21;
	internal const byte SoberBlue = 24;
	private static readonly byte[] DistanceWeights = CreateDistanceWeights();
	private static readonly byte[] RegionWeights = CreateRegionWeights();
	private static readonly ParallelOptions Parallelism = new()
	{
		MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 12)
	};

	public static bool IsLinuxSourceKey(byte red, byte green, byte blue)
	{
		return red == 18 && green == 18 && (blue == OriginalBlue || blue == SoberBlue);
	}

	public static int Build(byte[] bgra, int width, int height, byte[] ownWeights, byte[] mask)
	{
		int pixels = checked(width * height);
		if (width <= 0
			|| height <= 0
			|| bgra.Length < checked(pixels * 4)
			|| ownWeights.Length < pixels
			|| mask.Length < pixels)
			throw new ArgumentException("The homepage mask buffers do not match the requested dimensions");

		HomepageBackgroundSourceKeys sourceKeys = HomepageBackgroundSourceKeys.ReadConfigured();
		if (height >= 256)
			Parallel.For(0, height, Parallelism, y => BuildOwnWeightRow(bgra, width, y, ownWeights, sourceKeys));
		else
			for (int y = 0; y < height; y++)
				BuildOwnWeightRow(bgra, width, y, ownWeights, sourceKeys);

		int matched = 0;
		if (height >= 256)
		{
			Parallel.For(0, height, Parallelism, () => 0, (y, _, local) =>
				BuildMaskRow(width, height, y, ownWeights, mask, local), local => Interlocked.Add(ref matched, local));
		}
		else
		{
			for (int y = 0; y < height; y++)
				matched = BuildMaskRow(width, height, y, ownWeights, mask, matched);
		}
		return matched;
	}

	public static int BuildSampled(
		byte[] bgra,
		int sourceWidth,
		int sourceHeight,
		int sampleScale,
		byte[] ownWeights,
		byte[] mask)
	{
		if (sampleScale <= 1)
			return Build(bgra, sourceWidth, sourceHeight, ownWeights, mask);
		int width = (sourceWidth + sampleScale - 1) / sampleScale;
		int height = (sourceHeight + sampleScale - 1) / sampleScale;
		int pixels = checked(width * height);
		if (sourceWidth <= 0
			|| sourceHeight <= 0
			|| bgra.Length < checked(sourceWidth * sourceHeight * 4)
			|| ownWeights.Length < pixels
			|| mask.Length < pixels)
			throw new ArgumentException("The sampled homepage mask buffers do not match the requested dimensions");
		HomepageBackgroundSourceKeys sourceKeys = HomepageBackgroundSourceKeys.ReadConfigured();
		Action<int> buildOwnRow = y =>
		{
			int pixel = y * width;
			int sourceY = Math.Min(sourceHeight - 1, y * sampleScale);
			for (int x = 0; x < width; x++, pixel++)
			{
				int sourceX = Math.Min(sourceWidth - 1, x * sampleScale);
				int source = (sourceY * sourceWidth + sourceX) * 4;
				int distance = SourceDistance(bgra[source + 2], bgra[source + 1], bgra[source], sourceKeys);
				ownWeights[pixel] = DistanceWeights[Math.Min(distance, DistanceWeights.Length - 1)];
			}
		};
		if (height >= 128)
			Parallel.For(0, height, Parallelism, buildOwnRow);
		else
			for (int y = 0; y < height; y++)
				buildOwnRow(y);
		int matched = 0;
		if (height >= 128)
		{
			Parallel.For(0, height, Parallelism, () => 0, (y, _, local) =>
				BuildMaskRow(width, height, y, ownWeights, mask, local), local => Interlocked.Add(ref matched, local));
		}
		else
		{
			for (int y = 0; y < height; y++)
				matched = BuildMaskRow(width, height, y, ownWeights, mask, matched);
		}
		return matched;
	}

	public static int ExcludePointer(byte[] ownWeights, byte[] mask, int width, int height, int pointerX, int pointerY)
	{
		int pixels = checked(width * height);
		if (width <= 0
			|| height <= 0
			|| ownWeights.Length < pixels
			|| mask.Length < pixels)
			throw new ArgumentException("The homepage cursor buffers do not match the requested dimensions");
		if (pointerX < 0 || pointerY < 0 || pointerX >= width || pointerY >= height)
			return 0;

		int left = Math.Max(0, pointerX - 24);
		int top = Math.Max(0, pointerY - 24);
		int right = Math.Min(width - 1, pointerX + 96);
		int bottom = Math.Min(height - 1, pointerY + 96);
		int cleared = 0;
		for (int y = top; y <= bottom; y++)
		{
			int row = y * width;
			for (int x = left; x <= right; x++)
			{
				if (ownWeights[row + x] > 48)
					continue;
				int clearTop = Math.Max(top, y - 2);
				int clearBottom = Math.Min(bottom, y + 2);
				int clearLeft = Math.Max(left, x - 2);
				int clearRight = Math.Min(right, x + 2);
				for (int clearY = clearTop; clearY <= clearBottom; clearY++)
				{
					int clearRow = clearY * width;
					for (int clearX = clearLeft; clearX <= clearRight; clearX++)
					{
						int pixel = clearRow + clearX;
						if (mask[pixel] == 0)
							continue;
						mask[pixel] = 0;
						cleared++;
					}
				}
			}
		}
		return cleared;
	}

	private static void BuildOwnWeightRow(
		byte[] bgra,
		int width,
		int y,
		byte[] ownWeights,
		HomepageBackgroundSourceKeys sourceKeys)
	{
		int pixel = y * width;
		int source = pixel * 4;
		int end = pixel + width;
		while (pixel < end)
		{
			int distance = SourceDistance(bgra[source + 2], bgra[source + 1], bgra[source], sourceKeys);
			ownWeights[pixel] = DistanceWeights[Math.Min(distance, DistanceWeights.Length - 1)];
			pixel++;
			source += 4;
		}
	}

	private static int SourceDistance(byte red, byte green, byte blue, HomepageBackgroundSourceKeys keys)
	{
		return Math.Min(
			Math.Min(SourceDistance(red, green, blue, keys.Original), SourceDistance(red, green, blue, keys.Sober)),
			Math.Min(SourceDistance(red, green, blue, keys.AdjustedOriginal), SourceDistance(red, green, blue, keys.AdjustedSober)));
	}

	private static int SourceDistance(byte red, byte green, byte blue, HomepageBackgroundSourceKey key)
	{
		return Math.Max(
			Math.Abs(red - key.Red),
			Math.Max(Math.Abs(green - key.Green), Math.Abs(blue - key.Blue)));
	}

	private static HomepageBackgroundSourceKey NearestSourceKey(
		byte red,
		byte green,
		byte blue,
		HomepageBackgroundSourceKeys keys)
	{
		HomepageBackgroundSourceKey nearest = keys.Original;
		int distance = SourceDistance(red, green, blue, nearest);
		SelectNearest(keys.Sober, red, green, blue, ref nearest, ref distance);
		SelectNearest(keys.AdjustedOriginal, red, green, blue, ref nearest, ref distance);
		SelectNearest(keys.AdjustedSober, red, green, blue, ref nearest, ref distance);
		return nearest;
	}

	private static void SelectNearest(
		HomepageBackgroundSourceKey candidate,
		byte red,
		byte green,
		byte blue,
		ref HomepageBackgroundSourceKey nearest,
		ref int distance)
	{
		int candidateDistance = SourceDistance(red, green, blue, candidate);
		if (candidateDistance >= distance)
			return;
		distance = candidateDistance;
		nearest = candidate;
	}

	private static int BuildMaskRow(int width, int height, int y, byte[] ownWeights, byte[] mask, int matched)
	{
		int top = Math.Max(0, y - 2) * width;
		int middle = y * width;
		int bottom = Math.Min(height - 1, y + 2) * width;
		for (int x = 0; x < width; x++)
		{
			int left = Math.Max(0, x - 2);
			int right = Math.Min(width - 1, x + 2);
			int support = ownWeights[top + left]
				+ ownWeights[top + x]
				+ ownWeights[top + right]
				+ ownWeights[middle + left]
				+ ownWeights[middle + x]
				+ ownWeights[middle + right]
				+ ownWeights[bottom + left]
				+ ownWeights[bottom + x]
				+ ownWeights[bottom + right];
			byte region = RegionWeights[(support + 4) / 9];
			byte alpha = (byte)((ownWeights[middle + x] * region + 127) / 255);
			mask[middle + x] = alpha;
			if (alpha != 0)
				matched++;
		}
		return matched;
	}

	private static byte[] CreateDistanceWeights()
	{
		byte[] weights = new byte[256];
		for (int distance = 0; distance < weights.Length; distance++)
		{
			if (distance <= 1)
			{
				weights[distance] = 255;
				continue;
			}
			if (distance >= 10)
				continue;
			double value = (distance - 1) / 9d;
			double smooth = value * value * (3d - 2d * value);
			weights[distance] = (byte)Math.Clamp((int)Math.Round((1d - smooth) * 255d), 0, 255);
		}
		return weights;
	}

	private static byte[] CreateRegionWeights()
	{
		byte[] weights = new byte[256];
		for (int support = 0; support < weights.Length; support++)
		{
			double value = Math.Clamp((support / 255d - 0.30d) / 0.25d, 0d, 1d);
			double smooth = value * value * (3d - 2d * value);
			weights[support] = (byte)Math.Clamp((int)Math.Round(smooth * 255d), 0, 255);
		}
		return weights;
	}

	public static void BuildBackground(
		int width,
		int height,
		LinuxHomepageVisualSettings settings,
		byte[]? media,
		int mediaWidth,
		int mediaHeight,
		byte[] output,
		int[] mediaXMap,
		int[] mediaYMap)
	{
		int bytes = checked(width * height * 4);
		if (width <= 0 || height <= 0 || output.Length < bytes)
			throw new ArgumentException("The homepage background buffer does not match the requested dimensions");

		Color sourceFirst = ParseColor(settings.Color, Color.FromRgb(18, 18, 21));
		Color sourceSecond = ParseColor(settings.GradientColor, Color.FromRgb(91, 46, 255));
		HomepageBackgroundColorTransform colorTransform = settings.CreateColorTransform();
		colorTransform.Apply(sourceFirst.R, sourceFirst.G, sourceFirst.B, out byte firstRed, out byte firstGreen, out byte firstBlue);
		Color first = Color.FromRgb(firstRed, firstGreen, firstBlue);
		bool useGradient = settings.Mode == "Gradient";
		bool useMedia = settings.Mode == "Media"
			&& media != null
			&& mediaWidth > 0
			&& mediaHeight > 0
			&& media.Length >= checked(mediaWidth * mediaHeight * 4);
		double radians = settings.GradientAngle * Math.PI / 180d;
		double directionX = Math.Cos(radians);
		double directionY = Math.Sin(radians);
		double length = Math.Abs(directionX) + Math.Abs(directionY);
		double startX = 0.5d - directionX * length * 0.5d;
		double startY = 0.5d - directionY * length * 0.5d;
		double vectorX = directionX * length;
		double vectorY = directionY * length;
		double vectorLength = vectorX * vectorX + vectorY * vectorY;
		double mediaScale = useMedia ? Math.Max(width / (double)mediaWidth, height / (double)mediaHeight) : 1d;
		double mediaVisibleWidth = width / mediaScale;
		double mediaVisibleHeight = height / mediaScale;
		double mediaStartX = (mediaWidth - mediaVisibleWidth) * 0.5d;
		double mediaStartY = (mediaHeight - mediaVisibleHeight) * 0.5d;
		if (useMedia)
		{
			for (int x = 0; x < width; x++)
				mediaXMap[x] = CreateMediaMapCoordinate(
					mediaStartX + (x + 0.5d) * mediaVisibleWidth / width - 0.5d,
					mediaWidth);
			for (int y = 0; y < height; y++)
				mediaYMap[y] = CreateMediaMapCoordinate(
					mediaStartY + (y + 0.5d) * mediaVisibleHeight / height - 0.5d,
					mediaHeight);
		}

		Action<int> buildRow = y =>
		{
			int offset = y * width * 4;
			int verticalCoordinate = useMedia ? mediaYMap[y] : 0;
			int mediaTopRow = useMedia ? (verticalCoordinate >> 8) * mediaWidth : 0;
			int mediaBottomRow = useMedia ? Math.Min((verticalCoordinate >> 8) + 1, mediaHeight - 1) * mediaWidth : 0;
			int verticalAmount = verticalCoordinate & 255;
			double gradientAmount = vectorLength > 0d
				? (((0.5d / width) - startX) * vectorX + (((y + 0.5d) / height) - startY) * vectorY) / vectorLength
				: 0d;
			double gradientStep = vectorLength > 0d ? vectorX / width / vectorLength : 0d;
			for (int x = 0; x < width; x++, offset += 4)
			{
				byte blue = first.B;
				byte green = first.G;
				byte red = first.R;
				if (useMedia)
				{
					int horizontalCoordinate = mediaXMap[x];
					int mediaLeft = horizontalCoordinate >> 8;
					int mediaRight = Math.Min(mediaLeft + 1, mediaWidth - 1);
					int horizontalAmount = horizontalCoordinate & 255;
					int topLeftOffset = (mediaTopRow + mediaLeft) * 4;
					int topRightOffset = (mediaTopRow + mediaRight) * 4;
					int bottomLeftOffset = (mediaBottomRow + mediaLeft) * 4;
					int bottomRightOffset = (mediaBottomRow + mediaRight) * 4;
					blue = Interpolate(
						Interpolate(media![topLeftOffset], media[topRightOffset], horizontalAmount),
						Interpolate(media[bottomLeftOffset], media[bottomRightOffset], horizontalAmount),
						verticalAmount);
					green = Interpolate(
						Interpolate(media[topLeftOffset + 1], media[topRightOffset + 1], horizontalAmount),
						Interpolate(media[bottomLeftOffset + 1], media[bottomRightOffset + 1], horizontalAmount),
						verticalAmount);
					red = Interpolate(
						Interpolate(media[topLeftOffset + 2], media[topRightOffset + 2], horizontalAmount),
						Interpolate(media[bottomLeftOffset + 2], media[bottomRightOffset + 2], horizontalAmount),
						verticalAmount);
					colorTransform.Apply(red, green, blue, out red, out green, out blue);
				}
				else if (useGradient)
				{
					double amount = Math.Clamp(gradientAmount, 0d, 1d);
					blue = Interpolate(sourceFirst.B, sourceSecond.B, amount);
					green = Interpolate(sourceFirst.G, sourceSecond.G, amount);
					red = Interpolate(sourceFirst.R, sourceSecond.R, amount);
					colorTransform.Apply(red, green, blue, out red, out green, out blue);
				}
				gradientAmount += gradientStep;
				output[offset] = blue;
				output[offset + 1] = green;
				output[offset + 2] = red;
				output[offset + 3] = 255;
			}
		};
		if (height >= 256)
			Parallel.For(0, height, Parallelism, buildRow);
		else
			for (int y = 0; y < height; y++)
				buildRow(y);
	}

	public static void ApplyBackgroundMask(
		byte[] source,
		byte[] mask,
		byte[] background,
		int width,
		int height,
		byte[] output,
		int maskScale = 1)
	{
		int pixels = checked(width * height);
		int bytes = checked(pixels * 4);
		int normalizedMaskScale = Math.Max(1, maskScale);
		int maskWidth = (width + normalizedMaskScale - 1) / normalizedMaskScale;
		int maskHeight = (height + normalizedMaskScale - 1) / normalizedMaskScale;
		if (source.Length < bytes
			|| mask.Length < checked(maskWidth * maskHeight)
			|| background.Length < bytes
			|| output.Length < bytes)
			throw new ArgumentException("The homepage mask composition buffers do not match the requested dimensions");
		HomepageBackgroundSourceKeys sourceKeys = HomepageBackgroundSourceKeys.ReadConfigured();
		Action<int> applyRow = y =>
		{
			int pixel = y * width;
			int end = pixel + width;
			int offset = pixel * 4;
			int maskRow = Math.Min(maskHeight - 1, y / normalizedMaskScale) * maskWidth;
			int x = 0;
			while (pixel < end)
			{
				byte alpha = mask[maskRow + Math.Min(maskWidth - 1, x / normalizedMaskScale)];
				if (alpha == 0)
				{
					output[offset] = 0;
					output[offset + 1] = 0;
					output[offset + 2] = 0;
					output[offset + 3] = 0;
				}
				else
				{
					HomepageBackgroundSourceKey sourceKey = NearestSourceKey(
						source[offset + 2],
						source[offset + 1],
						source[offset],
						sourceKeys);
					int blue = Math.Clamp(source[offset] + background[offset] - sourceKey.Blue, 0, 255);
					int green = Math.Clamp(source[offset + 1] + background[offset + 1] - sourceKey.Green, 0, 255);
					int red = Math.Clamp(source[offset + 2] + background[offset + 2] - sourceKey.Red, 0, 255);
					output[offset] = (byte)((blue * alpha + 127) / 255);
					output[offset + 1] = (byte)((green * alpha + 127) / 255);
					output[offset + 2] = (byte)((red * alpha + 127) / 255);
					output[offset + 3] = alpha;
				}
				pixel++;
				x++;
				offset += 4;
			}
		};
		if (height >= 256)
			Parallel.For(0, height, Parallelism, applyRow);
		else
			for (int y = 0; y < height; y++)
				applyRow(y);
	}

	public static void Compose(
		byte[] source,
		byte[] mask,
		int width,
		int height,
		LinuxHomepageVisualSettings settings,
		byte[]? media,
		int mediaWidth,
		int mediaHeight,
		byte[] output,
		int[]? mediaXMap = null,
		int[]? mediaYMap = null)
	{
		int pixels = checked(width * height);
		int bytes = checked(pixels * 4);
		if (width <= 0
			|| height <= 0
			|| source.Length < bytes
			|| mask.Length < pixels
			|| output.Length < bytes)
			throw new ArgumentException("The homepage composition buffers do not match the requested dimensions");

		Color sourceFirst = ParseColor(settings.Color, Color.FromRgb(18, 18, 21));
		Color sourceSecond = ParseColor(settings.GradientColor, Color.FromRgb(91, 46, 255));
		HomepageBackgroundColorTransform colorTransform = settings.CreateColorTransform();
		colorTransform.Apply(sourceFirst.R, sourceFirst.G, sourceFirst.B, out byte firstRed, out byte firstGreen, out byte firstBlue);
		Color first = Color.FromRgb(firstRed, firstGreen, firstBlue);
		HomepageBackgroundSourceKeys sourceKeys = HomepageBackgroundSourceKeys.ReadConfigured();
		bool useGradient = settings.Mode == "Gradient";
		bool useMedia = settings.Mode == "Media"
			&& media != null
			&& mediaWidth > 0
			&& mediaHeight > 0
			&& media.Length >= checked(mediaWidth * mediaHeight * 4);

		double radians = settings.GradientAngle * Math.PI / 180d;
		double directionX = Math.Cos(radians);
		double directionY = Math.Sin(radians);
		double length = Math.Abs(directionX) + Math.Abs(directionY);
		double startX = 0.5d - directionX * length * 0.5d;
		double startY = 0.5d - directionY * length * 0.5d;
		double endX = 0.5d + directionX * length * 0.5d;
		double endY = 0.5d + directionY * length * 0.5d;
		double vectorX = endX - startX;
		double vectorY = endY - startY;
		double vectorLength = vectorX * vectorX + vectorY * vectorY;
		double mediaScale = useMedia ? Math.Max(width / (double)mediaWidth, height / (double)mediaHeight) : 1d;
		double mediaVisibleWidth = width / mediaScale;
		double mediaVisibleHeight = height / mediaScale;
		double mediaStartX = (mediaWidth - mediaVisibleWidth) * 0.5d;
		double mediaStartY = (mediaHeight - mediaVisibleHeight) * 0.5d;
		if (useMedia)
		{
			if (mediaXMap == null || mediaXMap.Length < width)
				mediaXMap = new int[width];
			if (mediaYMap == null || mediaYMap.Length < height)
				mediaYMap = new int[height];
			for (int x = 0; x < width; x++)
				mediaXMap[x] = CreateMediaMapCoordinate(
					mediaStartX + (x + 0.5d) * mediaVisibleWidth / width - 0.5d,
					mediaWidth);
			for (int y = 0; y < height; y++)
				mediaYMap[y] = CreateMediaMapCoordinate(
					mediaStartY + (y + 0.5d) * mediaVisibleHeight / height - 0.5d,
					mediaHeight);
		}
		int[]? horizontalMediaMap = mediaXMap;
		int[]? verticalMediaMap = mediaYMap;

		Action<int> composeRow = y =>
		{
			int pixel = y * width;
			int offset = pixel * 4;
			int verticalCoordinate = useMedia ? verticalMediaMap![y] : 0;
			int mediaTopRow = useMedia ? (verticalCoordinate >> 8) * mediaWidth : 0;
			int mediaBottomRow = useMedia ? Math.Min((verticalCoordinate >> 8) + 1, mediaHeight - 1) * mediaWidth : 0;
			int verticalAmount = verticalCoordinate & 255;
			double gradientAmount = vectorLength > 0d
				? (((0.5d / width) - startX) * vectorX + (((y + 0.5d) / height) - startY) * vectorY) / vectorLength
				: 0d;
			double gradientStep = vectorLength > 0d ? vectorX / width / vectorLength : 0d;
			for (int x = 0; x < width; x++, pixel++, offset += 4)
			{
				double pixelGradientAmount = gradientAmount;
				gradientAmount += gradientStep;
				byte alpha = mask[pixel];
				if (alpha == 0)
				{
					output[offset] = 0;
					output[offset + 1] = 0;
					output[offset + 2] = 0;
					output[offset + 3] = 0;
					continue;
				}

				byte backgroundBlue = first.B;
				byte backgroundGreen = first.G;
				byte backgroundRed = first.R;
				if (useMedia)
				{
					int horizontalCoordinate = horizontalMediaMap![x];
					int mediaLeft = horizontalCoordinate >> 8;
					int mediaRight = Math.Min(mediaLeft + 1, mediaWidth - 1);
					int horizontalAmount = horizontalCoordinate & 255;
					int topLeftOffset = (mediaTopRow + mediaLeft) * 4;
					int topRightOffset = (mediaTopRow + mediaRight) * 4;
					int bottomLeftOffset = (mediaBottomRow + mediaLeft) * 4;
					int bottomRightOffset = (mediaBottomRow + mediaRight) * 4;
					byte topBlue = Interpolate(media![topLeftOffset], media[topRightOffset], horizontalAmount);
					byte topGreen = Interpolate(media[topLeftOffset + 1], media[topRightOffset + 1], horizontalAmount);
					byte topRed = Interpolate(media[topLeftOffset + 2], media[topRightOffset + 2], horizontalAmount);
					byte bottomBlue = Interpolate(media[bottomLeftOffset], media[bottomRightOffset], horizontalAmount);
					byte bottomGreen = Interpolate(media[bottomLeftOffset + 1], media[bottomRightOffset + 1], horizontalAmount);
					byte bottomRed = Interpolate(media[bottomLeftOffset + 2], media[bottomRightOffset + 2], horizontalAmount);
					backgroundBlue = Interpolate(topBlue, bottomBlue, verticalAmount);
					backgroundGreen = Interpolate(topGreen, bottomGreen, verticalAmount);
					backgroundRed = Interpolate(topRed, bottomRed, verticalAmount);
					colorTransform.Apply(
						backgroundRed,
						backgroundGreen,
						backgroundBlue,
						out backgroundRed,
						out backgroundGreen,
						out backgroundBlue);
				}
				else if (useGradient)
				{
					double amount = Math.Clamp(pixelGradientAmount, 0d, 1d);
					backgroundBlue = Interpolate(sourceFirst.B, sourceSecond.B, amount);
					backgroundGreen = Interpolate(sourceFirst.G, sourceSecond.G, amount);
					backgroundRed = Interpolate(sourceFirst.R, sourceSecond.R, amount);
					colorTransform.Apply(
						backgroundRed,
						backgroundGreen,
						backgroundBlue,
						out backgroundRed,
						out backgroundGreen,
						out backgroundBlue);
				}

				HomepageBackgroundSourceKey sourceKey = NearestSourceKey(
					source[offset + 2],
					source[offset + 1],
					source[offset],
					sourceKeys);
				int overlayBlue = Math.Clamp(source[offset] + backgroundBlue - sourceKey.Blue, 0, 255);
				int overlayGreen = Math.Clamp(source[offset + 1] + backgroundGreen - sourceKey.Green, 0, 255);
				int overlayRed = Math.Clamp(source[offset + 2] + backgroundRed - sourceKey.Red, 0, 255);
				output[offset] = (byte)((overlayBlue * alpha + 127) / 255);
				output[offset + 1] = (byte)((overlayGreen * alpha + 127) / 255);
				output[offset + 2] = (byte)((overlayRed * alpha + 127) / 255);
				output[offset + 3] = alpha;
			}
		};

		if (height >= 256)
			Parallel.For(0, height, Parallelism, composeRow);
		else
			for (int y = 0; y < height; y++)
				composeRow(y);
	}

	private static byte Interpolate(byte first, byte second, double amount)
	{
		return (byte)Math.Clamp((int)Math.Round(first + (second - first) * amount), 0, 255);
	}

	private static byte Interpolate(byte first, byte second, int amount)
	{
		return (byte)((first * (255 - amount) + second * amount + 127) / 255);
	}

	private static int CreateMediaMapCoordinate(double coordinate, int length)
	{
		if (length <= 1 || coordinate <= 0d)
			return 0;
		int left = (int)Math.Floor(coordinate);
		if (left >= length - 1)
			return (length - 1) << 8;
		int amount = Math.Clamp((int)Math.Round((coordinate - left) * 255d), 0, 255);
		return (left << 8) | amount;
	}

	private static Color ParseColor(string value, Color fallback)
	{
		if (value.Length == 7
			&& value[0] == '#'
			&& int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
		{
			return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
		}
		return fallback;
	}
}

internal sealed class LinuxHomepageOverlayWindow : Window
{
	internal LinuxHomepageOverlayWindow(Border surface, int surfaceId)
	{
		Title = "Voidstrap Homepage Background Overlay "
			+ Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
			+ "-"
			+ surfaceId.ToString(CultureInfo.InvariantCulture);
		Width = 1;
		Height = 1;
		Left = -32000;
		Top = -32000;
		AllowsTransparency = true;
		WindowStyle = WindowStyle.None;
		ResizeMode = ResizeMode.NoResize;
		ShowActivated = false;
		ShowInTaskbar = false;
		Topmost = true;
		Focusable = false;
		IsHitTestVisible = false;
		Background = Brushes.Transparent;
		Content = surface;
	}
}

internal sealed class LinuxHomepageBackgroundOverlay : IDisposable
{
	private readonly Dispatcher _dispatcher;
	private readonly bool _forceRenderer;
	private LinuxHomepageBackgroundOverlayRenderer? _renderer;
	private IDisposable? _trackerLease;
	private int _reconcilePending;
	private bool _trackerSubscribed;
	private int _disposed;

	public static bool IsSupported => Voidstrap.Utility.Platform.IsLinux
		&& LinuxOverlaySurface.IsSupported
		&& LinuxWindowInterop.HasActiveX11Compositor;

	public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
	public bool IsSafeToRelease => IsDisposed && _renderer == null && _trackerLease == null;
	internal bool IsSurfaceOpen => _renderer is { IsSurfaceOpen: true };
	internal bool UsesApplicationDispatcher => _renderer is { UsesApplicationDispatcher: true };
	internal bool HasExclusiveSurface => _renderer is { HasExclusiveSurface: true };
	internal bool HasNativeSurface => _renderer is { HasNativeSurface: true };
	internal nint NativeHandle => _renderer?.NativeHandle ?? 0;
	internal string NativeTitle => _renderer?.NativeTitle ?? string.Empty;
	internal string NativeFailure => _renderer?.NativeFailure ?? string.Empty;

	public LinuxHomepageBackgroundOverlay() : this(false)
	{
	}

	internal LinuxHomepageBackgroundOverlay(bool forceRenderer)
	{
		if (!IsSupported)
			throw new InvalidOperationException("The Linux homepage overlay needs an available X11 or XWayland display");
		Application? application = Application.Current;
		if (application == null || !application.Dispatcher.CheckAccess())
			throw new InvalidOperationException("The Linux homepage overlay must be created on the application dispatcher");
		_dispatcher = application.Dispatcher;
		_forceRenderer = forceRenderer;
		if (forceRenderer)
		{
			CreateRenderer();
			return;
		}

		RobloxWindowTracker.Changed += OnTrackerChanged;
		_trackerSubscribed = true;
		try
		{
			_trackerLease = RobloxWindowTracker.Acquire();
			QueueReconcile();
		}
		catch
		{
			ReleaseTracker();
			throw;
		}
	}

	private void OnTrackerChanged(object? sender, RobloxWindowRect current)
	{
		QueueReconcile();
	}

	private void QueueReconcile()
	{
		if (_forceRenderer || IsDisposed || Interlocked.Exchange(ref _reconcilePending, 1) != 0)
			return;
		try
		{
			_dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reconcile));
		}
		catch (InvalidOperationException)
		{
			Interlocked.Exchange(ref _reconcilePending, 0);
		}
	}

	private void Reconcile()
	{
		Interlocked.Exchange(ref _reconcilePending, 0);
		if (IsDisposed)
			return;
		bool shouldRun = OverlayHub.HomepageBackgroundActive && RobloxWindowTracker.Current.Valid;
		if (!shouldRun)
		{
			LinuxHomepageBackgroundOverlayRenderer? renderer = _renderer;
			_renderer = null;
			renderer?.Dispose();
			return;
		}
		if (_renderer is { IsDisposed: false })
			return;
		try
		{
			CreateRenderer();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("LinuxHomepageBackground::Start", ex);
			OnRendererFailure();
		}
	}

	private void CreateRenderer()
	{
		if (IsDisposed || _renderer is { IsDisposed: false })
			return;
		Application application = Application.Current
			?? throw new InvalidOperationException("The Linux homepage overlay needs an active WPF application");
		Window? previousMainWindow = application.MainWindow;
		Window? mainWindowGuard = null;
		try
		{
			if (previousMainWindow == null)
			{
				mainWindowGuard = new Window();
				application.MainWindow = mainWindowGuard;
			}
			_renderer = new LinuxHomepageBackgroundOverlayRenderer(OnRendererFailure);
		}
		finally
		{
			if (ReferenceEquals(application.MainWindow, mainWindowGuard)
				|| application.MainWindow is LinuxHomepageOverlayWindow)
				application.MainWindow = previousMainWindow!;
			mainWindowGuard?.Close();
		}
	}

	private void OnRendererFailure()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		ReleaseTracker();
		LinuxHomepageBackgroundOverlayRenderer? renderer = _renderer;
		_renderer = null;
		renderer?.Dispose();
		OverlayHub.OnLinuxHomepageOverlayStopped(this);
	}

	private void ReleaseTracker()
	{
		if (_trackerSubscribed)
		{
			RobloxWindowTracker.Changed -= OnTrackerChanged;
			_trackerSubscribed = false;
		}
		IDisposable? trackerLease = _trackerLease;
		_trackerLease = null;
		trackerLease?.Dispose();
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		ReleaseTracker();
		LinuxHomepageBackgroundOverlayRenderer? renderer = _renderer;
		_renderer = null;
		renderer?.Dispose();
	}
}

internal sealed class LinuxHomepageBackgroundOverlayRenderer : IDisposable
{
	private static int _nextSurfaceId;
	private const int InactivePollMilliseconds = 83;
	private const int RefreshQueryMilliseconds = 5000;
	private const int ParkedPosition = -32000;
	private const int NativeFinalizeMaxAttempts = 50;
	private const long MaxCapturePixels = 9_437_184;

	private readonly Border _surface;
	private readonly Image _image;
	private readonly LinuxHomepageOverlayWindow _window;
	private readonly CancellationTokenSource _cancellation = new();
	private readonly Task _renderTask;
	private readonly object _nativeGate = new();
	private readonly object _frameGate = new();
	private DispatcherTimer? _nativeFinalizeTimer;
	private WriteableBitmap? _overlayBitmap;
	private byte[]? _pendingFrame;
	private byte[]? _reusableFrame;
	private int _pendingFrameWidth;
	private int _pendingFrameHeight;
	private int _presentedFrameWidth;
	private int _presentedFrameHeight;
	private int _presentationQueued;
	private long _publishedFrameGeneration;
	private long _presentedFrameGeneration;
	private long _presentedFrameCount;
	private long _droppedFrameCount;
	private long _presentationTicks;
	private TaskCompletionSource<bool>? _frameSizeReady;
	private nint _overlayHandle;
	private int _nativeFinalizePending;
	private int _nativeFinalizeAttempts;
	private int _nativeFailureQueued;
	private int _nativeStartedLogged;
	private int _startupComplete;
	private string _nativeFailure = "not started";
	private int _disposed;
	private int _stopping;
	private int _captureFailureLogged;
	private int _oversizeLogged;
	private int _inputFailureLogged;
	private int _compositorFailureLogged;
	private volatile bool _inputPassthroughReady;
	private volatile bool _nativeFinalized;
	private bool _nativeParked;
	private bool _nativePositioned;
	private int _nativeLeft;
	private int _nativeTop;
	private int _nativeWidth;
	private int _nativeHeight;
	private readonly Action _renderFailed;

	public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
	internal bool IsSurfaceOpen => _window.IsVisible && HasNativeSurface && !IsDisposed;
	internal bool UsesApplicationDispatcher => ReferenceEquals(_window.Dispatcher, Application.Current?.Dispatcher);
	internal bool HasExclusiveSurface => ReferenceEquals(_window.Content, _surface) && ReferenceEquals(_surface.Child, _image);
	internal nint NativeHandle => Volatile.Read(ref _overlayHandle);
	internal string NativeTitle => _window.Title;
	internal string NativeFailure => _nativeFailure;
	internal bool HasNativeSurface
	{
		get
		{
			nint handle = Volatile.Read(ref _overlayHandle);
			return _nativeFinalized
				&& _inputPassthroughReady
				&& handle != 0
				&& LinuxWindowInterop.IsLiveWindow(handle)
				&& LinuxWindowInterop.IsPreparedOverlayWindow(handle)
				&& LinuxWindowInterop.IsOverrideRedirectWindow(handle)
				&& LinuxWindowInterop.TryGetInputShapeRectangleCount(handle, out int count)
				&& count == 0;
		}
	}

	public LinuxHomepageBackgroundOverlayRenderer(Action renderFailed)
	{
		if (!LinuxHomepageBackgroundOverlay.IsSupported)
			throw new InvalidOperationException("The Linux homepage overlay needs an available X11 or XWayland display");
		_renderFailed = renderFailed;

		_image = new Image
		{
			Stretch = Stretch.Fill,
			IsHitTestVisible = false,
			SnapsToDevicePixels = true
		};
		_surface = new Border
		{
			Background = Brushes.Transparent,
			Opacity = 0d,
			IsHitTestVisible = false,
			SnapsToDevicePixels = true,
			Child = _image
		};
		_window = new LinuxHomepageOverlayWindow(_surface, Interlocked.Increment(ref _nextSurfaceId));
		LinuxOverlaySurface.ReleaseMainWindowClaim(_window);
		_window.SourceInitialized += OnWindowSourceInitialized;
		_window.Loaded += OnWindowLoaded;
		_window.Closed += OnWindowClosed;
		try
		{
			_window.Show();
			LinuxOverlaySurface.ReleaseMainWindowClaim(_window);
			if (!_window.IsVisible)
				throw new InvalidOperationException("The Linux homepage overlay surface closed during startup");
			ScheduleNativeFinalization();
			_renderTask = Task.Factory.StartNew(
				() => RunAsync(_cancellation.Token),
				CancellationToken.None,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default).Unwrap();
			Interlocked.Exchange(ref _startupComplete, 1);
		}
		catch
		{
			Interlocked.Exchange(ref _disposed, 1);
			Interlocked.Exchange(ref _stopping, 1);
			try
			{
				_cancellation.Cancel();
			}
			catch (ObjectDisposedException)
			{
			}
			StopNativeFinalization();
			_window.SourceInitialized -= OnWindowSourceInitialized;
			_window.Loaded -= OnWindowLoaded;
			_window.Closed -= OnWindowClosed;
			nint handle = Volatile.Read(ref _overlayHandle);
			OverlayDiagnostics.UnregisterOverlayHandle(handle);
			LinuxWindowInterop.ForgetPreparedOverlayWindow(handle);
			if (_window.IsVisible)
				_window.Close();
			_cancellation.Dispose();
			throw;
		}
	}

	private void OnWindowSourceInitialized(object? sender, EventArgs e)
	{
		ScheduleNativeFinalization();
	}

	private void OnWindowLoaded(object sender, RoutedEventArgs e)
	{
		ScheduleNativeFinalization();
	}

	private void ScheduleNativeFinalization()
	{
		if (IsDisposed
			|| Volatile.Read(ref _stopping) != 0
			|| _nativeFinalized
			|| Interlocked.Exchange(ref _nativeFinalizePending, 1) != 0)
			return;
		try
		{
			_window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(BeginNativeFinalization));
		}
		catch (InvalidOperationException)
		{
			Interlocked.Exchange(ref _nativeFinalizePending, 0);
		}
	}

	private void BeginNativeFinalization()
	{
		Interlocked.Exchange(ref _nativeFinalizePending, 0);
		if (IsDisposed || Volatile.Read(ref _stopping) != 0)
			return;
		if (TryFinalizeNativeSurface())
			return;
		if (_nativeFinalizeTimer != null)
			return;
		_nativeFinalizeTimer = new DispatcherTimer(DispatcherPriority.Loaded)
		{
			Interval = TimeSpan.FromMilliseconds(100)
		};
		_nativeFinalizeTimer.Tick += OnNativeFinalizeTick;
		_nativeFinalizeTimer.Start();
	}

	private void OnNativeFinalizeTick(object? sender, EventArgs e)
	{
		if (IsDisposed || Volatile.Read(ref _stopping) != 0 || TryFinalizeNativeSurface())
		{
			StopNativeFinalization();
			return;
		}
		_nativeFinalizeAttempts++;
		if (_nativeFinalizeAttempts < NativeFinalizeMaxAttempts)
			return;
		StopNativeFinalization();
		App.Logger.WriteLine("LinuxHomepageBackground", "The native X11 homepage surface did not become input-transparent");
		if (Interlocked.Exchange(ref _nativeFailureQueued, 1) == 0)
			_window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RestartAfterNativeFailure));
	}

	private bool TryFinalizeNativeSurface()
	{
		if (IsDisposed || Volatile.Read(ref _stopping) != 0)
			return false;
		nint current = Volatile.Read(ref _overlayHandle);
		if (_nativeFinalized && HasNativeSurface)
			return true;
		lock (_nativeGate)
		{
			current = _overlayHandle;
			_nativeFinalized = false;
			_inputPassthroughReady = false;
			if (current != 0)
				LinuxWindowInterop.TryMoveResize(current, ParkedPosition, ParkedPosition, 1, 1);
			_overlayHandle = 0;
			_nativeParked = false;
			_nativePositioned = false;
		}
		if (current != 0)
			ReleaseNativeCandidate(current);

		nint ensuredHandle = 0;
		try
		{
			ensuredHandle = new WindowInteropHelper(_window).EnsureHandle();
		}
		catch (Exception ex)
		{
			_nativeFailure = "WPF handle creation: " + ex.Message;
		}
		List<nint> candidates = new();
#if CROSSPLAT
		if (ProGpuWpfDiagnostics.TryGetWindowHost(_window, out ProGpuWpfWindowHost? host)
			&& host?.SilkWindow?.Native?.X11 is { } x11)
		{
			nint portableHandle = (nint)x11.Window;
			if (portableHandle != 0)
				candidates.Add(portableHandle);
		}
#endif
		nint titledHandle = LinuxWindowInterop.FindOwnWindowByTitle(_window.Title);
		if (titledHandle != 0 && !candidates.Contains(titledHandle))
			candidates.Add(titledHandle);
		if (ensuredHandle != 0 && LinuxWindowInterop.IsLiveWindow(ensuredHandle) && !candidates.Contains(ensuredHandle))
			candidates.Add(ensuredHandle);
		if (candidates.Count == 0)
		{
			_nativeFailure = "native surface discovery, WPF handle 0x" + ensuredHandle.ToInt64().ToString("x", CultureInfo.InvariantCulture);
			return false;
		}

		nint handle = 0;
		string failure = "native surface preparation";
		foreach (nint candidate in candidates)
		{
			if (TryPrepareNativeCandidate(candidate, out failure))
			{
				handle = candidate;
				break;
			}
		}
		if (handle == 0)
		{
			_nativeFailure = failure;
			return false;
		}

		lock (_nativeGate)
		{
			if (IsDisposed || Volatile.Read(ref _stopping) != 0)
			{
				ReleaseNativeCandidate(handle);
				return false;
			}
			_overlayHandle = handle;
			_nativeParked = true;
			_nativePositioned = false;
			_inputPassthroughReady = true;
			_nativeFinalized = true;
		}
		_nativeFinalizeAttempts = 0;
		_nativeFailure = string.Empty;
		Interlocked.Exchange(ref _nativeFailureQueued, 0);
		StopNativeFinalization();
		if (Interlocked.Exchange(ref _nativeStartedLogged, 1) == 0)
			App.Logger.WriteLine("LinuxHomepageBackground", "Started the Sober homepage renderer with Linux color keys #121215 and #121218");
		return true;
	}

	private static bool TryPrepareNativeCandidate(nint handle, out string failure)
	{
		failure = "native surface is not live";
		if (!LinuxWindowInterop.IsLiveWindow(handle))
			return false;
		if (!LinuxWindowInterop.TryMoveResize(handle, ParkedPosition, ParkedPosition, 1, 1))
		{
			failure = "initial native parking";
			ReleaseNativeCandidate(handle);
			return false;
		}
		if (!LinuxWindowInterop.TryPrepareOverlayWindow(handle)
			|| !LinuxWindowInterop.IsLiveWindow(handle)
			|| !LinuxWindowInterop.IsPreparedOverlayWindow(handle)
			|| !LinuxWindowInterop.IsOverrideRedirectWindow(handle))
		{
			failure = "override-redirect preparation";
			ReleaseNativeCandidate(handle);
			return false;
		}
		if (!LinuxWindowInterop.TryMoveResize(handle, ParkedPosition, ParkedPosition, 1, 1)
			|| !LinuxWindowInterop.TryGetWindowGeometry(handle, out int parkedLeft, out int parkedTop, out int parkedWidth, out int parkedHeight)
			|| parkedLeft != ParkedPosition
			|| parkedTop != ParkedPosition
			|| parkedWidth != 1
			|| parkedHeight != 1)
		{
			failure = "post-prepare native parking";
			ReleaseNativeCandidate(handle);
			return false;
		}
		if (!LinuxWindowInterop.TrySetClickThrough(handle)
			|| !LinuxWindowInterop.TryGetInputShapeRectangleCount(handle, out int inputRectangles)
			|| inputRectangles != 0)
		{
			failure = "empty native input shape";
			ReleaseNativeCandidate(handle);
			return false;
		}
		OverlayDiagnostics.RegisterOverlayHandle(handle);
		LinuxWindowInterop.TrySetAlwaysOnTop(handle);
		failure = string.Empty;
		return true;
	}

	private static void ReleaseNativeCandidate(nint handle)
	{
		if (handle == 0)
			return;
		LinuxWindowInterop.TryMoveResize(handle, ParkedPosition, ParkedPosition, 1, 1);
		OverlayDiagnostics.UnregisterOverlayHandle(handle);
		LinuxWindowInterop.ForgetPreparedOverlayWindow(handle);
	}

	private void StopNativeFinalization()
	{
		Interlocked.Exchange(ref _nativeFinalizePending, 0);
		DispatcherTimer? timer = _nativeFinalizeTimer;
		_nativeFinalizeTimer = null;
		if (timer == null)
			return;
		timer.Stop();
		timer.Tick -= OnNativeFinalizeTick;
	}

	private void OnWindowClosed(object? sender, EventArgs e)
	{
		if (IsDisposed || Volatile.Read(ref _startupComplete) == 0)
			return;
		Dispose();
		_renderFailed();
	}

	private async Task RunAsync(CancellationToken token)
	{
		HomepageBackgroundMedia? media = null;
		string activeMediaPath = string.Empty;
		string retainedMediaPath = string.Empty;
		long mediaVersion = 0;
		long nextMediaRetry = 0;
		long nextInputCheck = 0;
		long nextCompositorCheck = 0;
		byte[] capture = [];
		byte[] pendingCapture = [];
		byte[] ownWeights = [];
		byte[] mask = [];
		byte[] mediaUpload = [];
		byte[] background = [];
		byte[] composed = [];
		int[] mediaXMap = [];
		int[] mediaYMap = [];
		int bufferWidth = 0;
		int bufferHeight = 0;
		int maskScale = 1;
		int maskWidth = 0;
		int maskHeight = 0;
		int mediaWidth = 0;
		int mediaHeight = 0;
		bool hasMediaFrame = false;
		bool hasFreshMask = false;
		bool hasCapturedSource = false;
		LinuxHomepageVisualSettings queuedSettings = default;
		bool hasQueuedSettings = false;
		bool surfaceActive = false;
		bool surfaceVisible = false;
		RobloxWindowRect sourceWindow = default;
		int surfaceLeft = 0;
		int surfaceTop = 0;
		int surfaceWidth = 0;
		int surfaceHeight = 0;
		double targetRefreshHz = 60d;
		long nextRefreshQuery = 0;
		long nextPerformanceReport = Environment.TickCount64 + 10_000;
		long performanceStarted = Stopwatch.GetTimestamp();
		long captureAttempts = 0;
		long sourceChanges = 0;
		long composedFrames = 0;
		long captureTicks = 0;
		long maskTicks = 0;
		long composeTicks = 0;
		long reportedPresents = 0;
		long reportedDrops = 0;
		long reportedPresentationTicks = 0;
		bool performanceActive = false;
		nint damageWindow = 0;
		nint windowDamage = 0;
		int damageEventType = 0;
		bool compositorAvailable = false;

		try
		{
			while (!token.IsCancellationRequested && !IsDisposed)
			{
				long frameStarted = Stopwatch.GetTimestamp();
				LinuxHomepageVisualSettings settings = LinuxHomepageVisualSettings.Read();
				long now = Environment.TickCount64;
				sourceWindow = RobloxWindowTracker.Current;
				if (now >= nextCompositorCheck)
				{
					compositorAvailable = LinuxWindowInterop.HasActiveX11Compositor;
					nextCompositorCheck = now + 2000;
				}
				surfaceActive = compositorAvailable
					&& OverlayHub.HomepageBackgroundActive
					&& TryResolveFocusedGeometry(sourceWindow, out surfaceLeft, out surfaceTop, out surfaceWidth, out surfaceHeight);
				if (!compositorAvailable)
				{
					if (Interlocked.Exchange(ref _compositorFailureLogged, 1) == 0)
						App.Logger.WriteLine("LinuxHomepageBackground", "The X11 compositor is unavailable, so the homepage overlay is parked until it recovers");
				}
				else
				{
					Interlocked.Exchange(ref _compositorFailureLogged, 0);
				}
				long surfacePixels = (long)surfaceWidth * surfaceHeight;
				if (surfaceActive && (surfacePixels <= 0 || surfacePixels > MaxCapturePixels))
				{
					surfaceActive = false;
					if (Interlocked.Exchange(ref _oversizeLogged, 1) == 0)
						App.Logger.WriteLine("LinuxHomepageBackground", "The Sober surface is too large for safe homepage capture");
				}
				if (surfaceActive && now >= nextRefreshQuery)
				{
					targetRefreshHz = LinuxDisplayMetrics.RefreshRateForWindow(sourceWindow.Hwnd);
					nextRefreshQuery = now + RefreshQueryMilliseconds;
				}
				if (surfaceActive && damageWindow != sourceWindow.Hwnd)
				{
					LinuxWindowInterop.ReleaseWindowCapture(damageWindow);
					LinuxWindowInterop.DestroyWindowDamage(windowDamage, damageEventType);
					damageWindow = sourceWindow.Hwnd;
					if (!LinuxWindowInterop.TryCreateWindowDamage(damageWindow, out windowDamage, out damageEventType))
					{
						windowDamage = 0;
						damageEventType = 0;
					}
					hasCapturedSource = false;
				}

				if (!OverlayHub.HomepageBackgroundActive)
					surfaceActive = false;
				if (surfaceActive && !performanceActive)
				{
					reportedPresents = Volatile.Read(ref _presentedFrameCount);
					reportedDrops = Volatile.Read(ref _droppedFrameCount);
					reportedPresentationTicks = Volatile.Read(ref _presentationTicks);
					captureAttempts = 0;
					sourceChanges = 0;
					composedFrames = 0;
					captureTicks = 0;
					maskTicks = 0;
					composeTicks = 0;
					performanceStarted = Stopwatch.GetTimestamp();
					nextPerformanceReport = now + 10_000;
				}
				performanceActive = surfaceActive;
				if (!surfaceActive)
				{
					LinuxWindowInterop.ReleaseWindowCapture(damageWindow);
					LinuxWindowInterop.DestroyWindowDamage(windowDamage, damageEventType);
					damageWindow = 0;
					windowDamage = 0;
					damageEventType = 0;
					ParkOverlay();
					if (surfaceVisible)
					{
						await SetSurfaceVisibilityAsync(false, token).ConfigureAwait(false);
						surfaceVisible = false;
					}
					StopMedia(ref media, ref activeMediaPath, ref mediaVersion);
					nextMediaRetry = 0;
					hasFreshMask = false;
					hasCapturedSource = false;
					await Task.Delay(InactivePollMilliseconds, token).ConfigureAwait(false);
					continue;
				}

				if (!_inputPassthroughReady || now >= nextInputCheck)
				{
					if (now >= nextInputCheck)
					{
						nextInputCheck = now + 2000;
						_inputPassthroughReady = await EnsureInputPassthroughAsync(token).ConfigureAwait(false);
					}
					if (!_inputPassthroughReady)
					{
						if (surfaceVisible)
						{
							await SetSurfaceVisibilityAsync(false, token).ConfigureAwait(false);
							surfaceVisible = false;
						}
						ParkOverlay();
						if (Interlocked.Exchange(ref _inputFailureLogged, 1) == 0)
							App.Logger.WriteLine("LinuxHomepageBackground", "Input passthrough is unavailable, so the homepage overlay will remain disabled");
						await Task.Delay(250, token).ConfigureAwait(false);
						continue;
					}
					Interlocked.Exchange(ref _inputFailureLogged, 0);
				}

				bool visualChanged = ReconcileMedia(
					settings,
					ref media,
					ref activeMediaPath,
					ref retainedMediaPath,
					ref mediaVersion,
					ref nextMediaRetry,
					ref hasMediaFrame,
					ref mediaWidth,
					ref mediaHeight,
					targetRefreshHz);
				bool mediaUpdated = false;
				if (media != null)
				{
					media.TryReadFrame(mediaVersion, (pixels, width, height, version) =>
					{
						int length = checked(width * height * 4);
						if (mediaUpload.Length != length)
							mediaUpload = new byte[length];
						Buffer.BlockCopy(pixels, 0, mediaUpload, 0, length);
						mediaWidth = width;
						mediaHeight = height;
						mediaVersion = version;
						mediaUpdated = true;
						hasMediaFrame = true;
					});
				}

				bool maskUpdated = false;
				bool geometryChanged = false;
				long pixels = (long)surfaceWidth * surfaceHeight;
				geometryChanged = !_nativePositioned
					|| _nativeLeft != surfaceLeft
					|| _nativeTop != surfaceTop
					|| _nativeWidth != surfaceWidth
					|| _nativeHeight != surfaceHeight;
				int requestedMaskScale = targetRefreshHz >= 120d ? 2 : 1;
				bool buffersChanged = surfaceWidth != bufferWidth
					|| surfaceHeight != bufferHeight
					|| requestedMaskScale != maskScale;
				if (buffersChanged)
				{
					int count = checked((int)pixels);
					maskScale = requestedMaskScale;
					maskWidth = (surfaceWidth + maskScale - 1) / maskScale;
					maskHeight = (surfaceHeight + maskScale - 1) / maskScale;
					int maskCount = checked(maskWidth * maskHeight);
					capture = new byte[checked(count * 4)];
					pendingCapture = new byte[checked(count * 4)];
					ownWeights = new byte[maskCount];
					mask = new byte[maskCount];
					background = [];
					composed = [];
					mediaXMap = new int[surfaceWidth];
					mediaYMap = new int[surfaceHeight];
					bufferWidth = surfaceWidth;
					bufferHeight = surfaceHeight;
					hasCapturedSource = false;
				}

				bool damageReported = windowDamage != 0
					&& LinuxWindowInterop.TryConsumeWindowDamage(windowDamage, damageEventType);
				if (damageReported)
					LinuxWindowInterop.RefreshWindowCapturePixmap(sourceWindow.Hwnd);
				bool captureRequired = !hasCapturedSource
					|| buffersChanged
					|| windowDamage == 0
					|| damageReported;
				if (!captureRequired)
				{
					Interlocked.Exchange(ref _captureFailureLogged, 0);
				}
				else if (TryCaptureFrame(sourceWindow.Hwnd, pendingCapture, ref captureTicks, out int capturedWidth, out int capturedHeight)
					&& capturedWidth == bufferWidth
					&& capturedHeight == bufferHeight)
				{
					captureAttempts++;
					bool sourceChanged = !hasCapturedSource
						|| damageReported
						|| !pendingCapture.AsSpan().SequenceEqual(capture);
					if (sourceChanged)
					{
						sourceChanges++;
						(capture, pendingCapture) = (pendingCapture, capture);
						hasCapturedSource = true;
					}
					if (sourceChanged)
					{
						long maskStarted = Stopwatch.GetTimestamp();
						int matched = LinuxHomepageBackgroundMask.BuildSampled(
							capture,
							bufferWidth,
							bufferHeight,
							maskScale,
							ownWeights,
							mask);
						if (LinuxWindowInterop.TryGetPointerPosition(out int pointerX, out int pointerY))
							LinuxHomepageBackgroundMask.ExcludePointer(
								ownWeights,
								mask,
								maskWidth,
								maskHeight,
								(pointerX - surfaceLeft) / maskScale,
								(pointerY - surfaceTop) / maskScale);
						int minimumRegion = Math.Min(
							ownWeights.Length,
							Math.Clamp(ownWeights.Length / 512, 64, 4096));
						hasFreshMask = matched >= minimumRegion;
						if (!hasFreshMask)
							Array.Clear(mask);
						maskTicks += Stopwatch.GetTimestamp() - maskStarted;
						maskUpdated = true;
					}
					Interlocked.Exchange(ref _captureFailureLogged, 0);
				}
				else
				{
					hasFreshMask = false;
					hasCapturedSource = false;
					Array.Clear(mask);
					if (Interlocked.Exchange(ref _captureFailureLogged, 1) == 0)
						App.Logger.WriteLine("LinuxHomepageBackground", "Sober pixels could not be captured, the overlay will stay transparent until capture recovers");
				}

				if (!hasFreshMask)
				{
					if (surfaceVisible)
					{
						await SetSurfaceVisibilityAsync(false, token).ConfigureAwait(false);
						surfaceVisible = false;
					}
					ParkOverlay();
					await DelayForRefreshAsync(frameStarted, targetRefreshHz, token).ConfigureAwait(false);
					continue;
				}

				bool settingsChanged = !hasQueuedSettings || settings != queuedSettings;
				bool backgroundChanged = mediaUpdated || visualChanged || settingsChanged || buffersChanged;
				if (maskUpdated || backgroundChanged)
				{
					if (geometryChanged && surfaceVisible)
					{
						await SetSurfaceVisibilityAsync(false, token).ConfigureAwait(false);
						surfaceVisible = false;
					}
					int required = checked(bufferWidth * bufferHeight * 4);
					if (background.Length != required)
					{
						background = new byte[required];
						backgroundChanged = true;
					}
					if (composed.Length != required)
						composed = new byte[required];
					byte[] output = composed;
					long composeStarted = Stopwatch.GetTimestamp();
					if (backgroundChanged)
					{
						LinuxHomepageBackgroundMask.BuildBackground(
						bufferWidth,
						bufferHeight,
						settings,
						hasMediaFrame ? mediaUpload : null,
						mediaWidth,
						mediaHeight,
						background,
						mediaXMap,
						mediaYMap);
					}
					LinuxHomepageBackgroundMask.ApplyBackgroundMask(
						capture,
						mask,
						background,
						bufferWidth,
						bufferHeight,
						output,
						maskScale);
					composeTicks += Stopwatch.GetTimestamp() - composeStarted;
					composedFrames++;
					token.ThrowIfCancellationRequested();
					(bool inputReady, byte[] reusable) = await PresentAsync(output, bufferWidth, bufferHeight, token).ConfigureAwait(false);
					composed = reusable;
					token.ThrowIfCancellationRequested();
					if (!inputReady)
					{
						ParkOverlay();
						if (Interlocked.Exchange(ref _inputFailureLogged, 1) == 0)
							App.Logger.WriteLine("LinuxHomepageBackground", "Input passthrough is unavailable, so the homepage overlay will remain disabled");
						await DelayForRefreshAsync(frameStarted, targetRefreshHz, token).ConfigureAwait(false);
						continue;
					}
					Interlocked.Exchange(ref _inputFailureLogged, 0);
					queuedSettings = settings;
					hasQueuedSettings = true;
				}

				if (now >= nextPerformanceReport)
				{
					long presented = Volatile.Read(ref _presentedFrameCount);
					long dropped = Volatile.Read(ref _droppedFrameCount);
					long presentationTicks = Volatile.Read(ref _presentationTicks);
					double uploadMilliseconds = (presentationTicks - reportedPresentationTicks) * 1000d / Stopwatch.Frequency;
					double captureMilliseconds = captureTicks * 1000d / Stopwatch.Frequency;
					double maskMilliseconds = maskTicks * 1000d / Stopwatch.Frequency;
					double composeMilliseconds = composeTicks * 1000d / Stopwatch.Frequency;
					double elapsedSeconds = Math.Max(0.001d, Stopwatch.GetElapsedTime(performanceStarted).TotalSeconds);
					long presentedFrames = presented - reportedPresents;
					App.Logger.WriteLine(
						"LinuxHomepageBackground",
						string.Create(
							CultureInfo.InvariantCulture,
							$"Cadence target {targetRefreshHz:F2} Hz, actual {presentedFrames / elapsedSeconds:F2} Hz, captured {captureAttempts / elapsedSeconds:F2} Hz, changed {sourceChanges / elapsedSeconds:F2} Hz, composed {composedFrames / elapsedSeconds:F2} Hz, dropped {dropped - reportedDrops}, capture {captureMilliseconds / Math.Max(1, captureAttempts):F2} ms, mask {maskMilliseconds / Math.Max(1, sourceChanges):F2} ms, compose {composeMilliseconds / Math.Max(1, composedFrames):F2} ms, upload {uploadMilliseconds / Math.Max(1, presentedFrames):F2} ms/frame"));
					reportedPresents = presented;
					reportedDrops = dropped;
					reportedPresentationTicks = presentationTicks;
					captureAttempts = 0;
					sourceChanges = 0;
					composedFrames = 0;
					captureTicks = 0;
					maskTicks = 0;
					composeTicks = 0;
					performanceStarted = Stopwatch.GetTimestamp();
					nextPerformanceReport = now + 10_000;
				}

				if (!surfaceVisible || geometryChanged)
				{
					bool moved = MoveOverlay(surfaceLeft, surfaceTop, surfaceWidth, surfaceHeight);
					if (moved && !surfaceVisible)
					{
						await SetSurfaceVisibilityAsync(true, token).ConfigureAwait(false);
						surfaceVisible = true;
					}
					else if (!moved)
					{
						if (surfaceVisible)
						{
							await SetSurfaceVisibilityAsync(false, token).ConfigureAwait(false);
							surfaceVisible = false;
						}
						ParkOverlay();
					}
				}

				if (damageReported)
				{
					await Task.Yield();
					token.ThrowIfCancellationRequested();
				}
				else
				{
					await DelayForRefreshAsync(frameStarted, targetRefreshHz, token).ConfigureAwait(false);
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			ParkOverlay();
			try
			{
				await SetSurfaceVisibilityAsync(false, CancellationToken.None).ConfigureAwait(false);
			}
			catch (InvalidOperationException)
			{
			}
			App.Logger.WriteException("LinuxHomepageBackground::Run", ex);
			if (!token.IsCancellationRequested && !IsDisposed)
			{
				await Task.Delay(2000, token).ConfigureAwait(false);
				Dispatcher dispatcher = _window.Dispatcher;
				if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
					_ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RestartAfterRenderFailure));
			}
		}
		finally
		{
			LinuxWindowInterop.ReleaseWindowCapture(damageWindow);
			LinuxWindowInterop.DestroyWindowDamage(windowDamage, damageEventType);
			ParkOverlay();
			media?.Dispose();
			_cancellation.Dispose();
		}
	}

	private static async Task DelayForRefreshAsync(long frameStarted, double refreshHz, CancellationToken token)
	{
		double intervalMilliseconds = 1000d / Math.Clamp(refreshHz, 24d, 360d);
		double remainingMilliseconds = intervalMilliseconds - Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds;
		if (remainingMilliseconds > 0.25d)
		{
			await Task.Delay(TimeSpan.FromMilliseconds(remainingMilliseconds), token).ConfigureAwait(false);
			return;
		}
		await Task.Yield();
		token.ThrowIfCancellationRequested();
	}

	private static bool TryCaptureFrame(
		nint window,
		byte[] destination,
		ref long elapsedTicks,
		out int width,
		out int height)
	{
		long started = Stopwatch.GetTimestamp();
		bool captured = LinuxWindowInterop.TryCaptureWindowBgrx32(window, destination, out width, out height);
		elapsedTicks += Stopwatch.GetTimestamp() - started;
		return captured;
	}

	private void RestartAfterRenderFailure()
	{
		if (IsDisposed)
			return;
		Dispose();
		_renderFailed();
	}

	private void RestartAfterNativeFailure()
	{
		if (Interlocked.Exchange(ref _nativeFailureQueued, 0) == 0 || IsDisposed || _nativeFinalized)
			return;
		RestartAfterRenderFailure();
	}

	private static void StopMedia(
		ref HomepageBackgroundMedia? media,
		ref string activeMediaPath,
		ref long mediaVersion)
	{
		media?.Dispose();
		media = null;
		activeMediaPath = string.Empty;
		mediaVersion = 0;
	}

	private static bool ReconcileMedia(
		LinuxHomepageVisualSettings settings,
		ref HomepageBackgroundMedia? media,
		ref string activeMediaPath,
		ref string retainedMediaPath,
		ref long mediaVersion,
		ref long nextMediaRetry,
		ref bool hasMediaFrame,
		ref int mediaWidth,
		ref int mediaHeight,
		double refreshHz)
	{
		string requested = settings.Mode == "Media" ? settings.MediaPath : string.Empty;
		bool visualChanged = false;
		if (!string.Equals(requested, retainedMediaPath, StringComparison.OrdinalIgnoreCase))
		{
			retainedMediaPath = requested;
			hasMediaFrame = false;
			mediaWidth = 0;
			mediaHeight = 0;
			visualChanged = true;
		}
		if (requested.Length == 0)
		{
			StopMedia(ref media, ref activeMediaPath, ref mediaVersion);
			return visualChanged;
		}
		if (media != null && string.Equals(requested, activeMediaPath, StringComparison.OrdinalIgnoreCase))
			return visualChanged;
		long now = Environment.TickCount64;
		if (now < nextMediaRetry)
			return visualChanged;
		nextMediaRetry = now + 2000;

		media?.Dispose();
		media = null;
		activeMediaPath = requested;
		mediaVersion = 0;
		if (File.Exists(requested))
		{
			try
			{
				media = new HomepageBackgroundMedia(requested, refreshHz);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("LinuxHomepageBackground", "The selected homepage media could not be opened: " + ex.Message);
			}
		}
		return visualChanged;
	}

	private static bool TryResolveFocusedGeometry(RobloxWindowRect current, out int left, out int top, out int width, out int height)
	{
		left = 0;
		top = 0;
		width = 0;
		height = 0;
		if (!current.Valid || current.Hwnd == 0)
			return false;

		nint focused = LinuxWindowInterop.GetFocusedWindow();
		nint active = LinuxWindowInterop.GetActiveTopLevelWindow();
		bool isFocused = LinuxWindowInterop.IsSameOrDescendantWindow(focused, current.Hwnd)
			|| LinuxWindowInterop.IsSameOrDescendantWindow(active, current.Hwnd)
			|| OverlayDiagnostics.IsOverlayHandle(focused)
			|| OverlayDiagnostics.IsOverlayHandle(active);
		if (!isFocused)
			return false;

		return LinuxWindowInterop.TryGetWindowGeometry(current.Hwnd, out left, out top, out width, out height)
			&& width > 0
			&& height > 0;
	}

	private bool MoveOverlay(int left, int top, int width, int height)
	{
		lock (_nativeGate)
		{
			if (Volatile.Read(ref _stopping) != 0 || !_nativeFinalized || !_inputPassthroughReady)
				return false;
			nint handle = Volatile.Read(ref _overlayHandle);
			if (handle == 0)
				return false;
			if (_nativePositioned
				&& !_nativeParked
				&& _nativeLeft == left
				&& _nativeTop == top
				&& _nativeWidth == width
				&& _nativeHeight == height)
				return true;
			if (!LinuxWindowInterop.TryMoveResize(handle, left, top, width, height))
			{
				_nativeFinalized = false;
				_inputPassthroughReady = false;
				_nativePositioned = false;
				return false;
			}
			LinuxWindowInterop.TrySetAlwaysOnTop(handle);
			_nativeLeft = left;
			_nativeTop = top;
			_nativeWidth = width;
			_nativeHeight = height;
			_nativeParked = false;
			_nativePositioned = true;
			return true;
		}
	}

	private bool ParkOverlay()
	{
		lock (_nativeGate)
			return ParkOverlayLocked();
	}

	private bool ParkOverlayLocked()
	{
		if (_nativeParked)
			return true;
		nint handle = Volatile.Read(ref _overlayHandle);
		if (handle != 0 && LinuxWindowInterop.TryMoveResize(handle, ParkedPosition, ParkedPosition, 1, 1))
		{
			_nativeParked = true;
			_nativePositioned = false;
			return true;
		}
		return false;
	}

	public bool RequestStop()
	{
		Interlocked.Exchange(ref _stopping, 1);
		try
		{
			_cancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		return ParkOverlay();
	}

	private async Task<(bool InputReady, byte[] Reusable)> PresentAsync(
		byte[] pixels,
		int width,
		int height,
		CancellationToken token)
	{
		Dispatcher dispatcher = _window.Dispatcher;
		if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
			return (false, pixels);

		Task? readiness = null;
		byte[] reusable;
		lock (_frameGate)
		{
			reusable = _pendingFrame ?? _reusableFrame ?? [];
			if (_pendingFrame != null)
				Interlocked.Increment(ref _droppedFrameCount);
			_pendingFrame = pixels;
			_reusableFrame = null;
			_pendingFrameWidth = width;
			_pendingFrameHeight = height;
			_publishedFrameGeneration++;
			if (_presentedFrameWidth != width || _presentedFrameHeight != height)
			{
				_frameSizeReady ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				readiness = _frameSizeReady.Task;
			}
		}
		SchedulePresentation();
		if (readiness != null)
			await readiness.WaitAsync(token).ConfigureAwait(false);
		return (_nativeFinalized && _inputPassthroughReady, reusable);
	}

	private void SchedulePresentation()
	{
		if (Interlocked.Exchange(ref _presentationQueued, 1) != 0)
			return;
		Dispatcher dispatcher = _window.Dispatcher;
		if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
		{
			Interlocked.Exchange(ref _presentationQueued, 0);
			return;
		}
		try
		{
			dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(PresentLatestFrame));
			LinuxOverlaySurface.WakePresentation(_window);
		}
		catch (InvalidOperationException)
		{
			Interlocked.Exchange(ref _presentationQueued, 0);
		}
	}

	private void PresentLatestFrame()
	{
		byte[]? pixels;
		int width;
		int height;
		long generation;
		lock (_frameGate)
		{
			pixels = _pendingFrame;
			width = _pendingFrameWidth;
			height = _pendingFrameHeight;
			generation = _publishedFrameGeneration;
			_pendingFrame = null;
		}

		if (pixels != null && !IsDisposed)
		{
			long started = Stopwatch.GetTimestamp();
			if (_overlayBitmap == null || _overlayBitmap.PixelWidth != width || _overlayBitmap.PixelHeight != height)
			{
				_overlayBitmap = new WriteableBitmap(width, height, 96d, 96d, PixelFormats.Pbgra32, null);
				_image.Source = _overlayBitmap;
			}
			CopyFrame(_overlayBitmap, pixels, width, height);
			Interlocked.Add(ref _presentationTicks, Stopwatch.GetTimestamp() - started);
			TaskCompletionSource<bool>? ready = null;
			lock (_frameGate)
			{
				_reusableFrame = pixels;
				_presentedFrameWidth = width;
				_presentedFrameHeight = height;
				_presentedFrameGeneration = generation;
				Interlocked.Increment(ref _presentedFrameCount);
				if (_frameSizeReady != null)
				{
					ready = _frameSizeReady;
					_frameSizeReady = null;
				}
			}
			ready?.TrySetResult(true);
		}

		Interlocked.Exchange(ref _presentationQueued, 0);
		lock (_frameGate)
		{
			if (_pendingFrame == null)
				return;
		}
		SchedulePresentation();
	}

	internal static void CopyFrame(WriteableBitmap bitmap, byte[] pixels, int width, int height)
	{
		int sourceStride = checked(width * 4);
		bitmap.Lock();
		try
		{
			unsafe
			{
				fixed (byte* source = pixels)
				{
					byte* destination = (byte*)bitmap.BackBuffer;
					if (bitmap.BackBufferStride == sourceStride)
					{
						Buffer.MemoryCopy(source, destination, pixels.Length, pixels.Length);
					}
					else
					{
						for (int row = 0; row < height; row++)
							Buffer.MemoryCopy(source + row * sourceStride, destination + row * bitmap.BackBufferStride, sourceStride, sourceStride);
					}
				}
			}
			bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
		}
		finally
		{
			bitmap.Unlock();
		}
	}

	private async Task<bool> EnsureInputPassthroughAsync(CancellationToken token)
	{
		Dispatcher dispatcher = _window.Dispatcher;
		if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
			return false;
		bool ready = false;
			DispatcherOperation operation = dispatcher.InvokeAsync(() =>
			{
				if (!IsDisposed)
					ready = TryFinalizeNativeSurface();
			}, DispatcherPriority.Send, token);
		await operation.Task.ConfigureAwait(false);
		return ready;
	}

	private async Task SetSurfaceVisibilityAsync(bool visible, CancellationToken token)
	{
		Dispatcher dispatcher = _window.Dispatcher;
		if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
			return;
		DispatcherOperation operation = dispatcher.InvokeAsync(() =>
		{
			if (!IsDisposed)
				_surface.Opacity = visible ? 1d : 0d;
		}, DispatcherPriority.Send, token);
		await operation.Task.ConfigureAwait(false);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		StopNativeFinalization();
		_nativeFinalized = false;
		_inputPassthroughReady = false;
		RequestStop();
		_window.SourceInitialized -= OnWindowSourceInitialized;
		_window.Loaded -= OnWindowLoaded;
		_window.Closed -= OnWindowClosed;
		nint handle = Volatile.Read(ref _overlayHandle);
		ReleaseNativeCandidate(handle);
		if (_window.IsVisible)
			_window.Close();
		if (Volatile.Read(ref _nativeStartedLogged) != 0)
			App.Logger.WriteLine("LinuxHomepageBackground", "Stopped the Sober homepage renderer");
	}
}
