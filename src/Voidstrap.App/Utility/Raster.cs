using System;
using System.IO;

namespace Voidstrap.Utility;

internal enum RasterFilter
{
	Bicubic = 0,
	Lanczos3 = 1,
	Bilinear = 2,
	Nearest = 3
}

internal enum PngLevel
{
	Fast = 0,
	Default = 1,
	Best = 2
}

internal sealed class Raster
{
	public const long DefaultMaxBytes = 256L * 1024 * 1024;

	public int Width { get; }

	public int Height { get; }

	public byte[] Pixels { get; }

	public Raster(int width, int height, byte[] pixels)
	{
		if (width <= 0 || height <= 0 || pixels == null || pixels.LongLength != (long)width * height * 4)
			throw new ArgumentException("Pixel buffer does not match the image size");
		Width = width;
		Height = height;
		Pixels = pixels;
	}

	public static Raster Blank(int width, int height)
	{
		return new Raster(width, height, new byte[checked(width * height * 4)]);
	}

	public static bool TryIdentify(byte[] bytes, out int width, out int height)
	{
		if (NativeImaging.TryIdentify(bytes, out width, out height))
			return true;
		try
		{
			SixLabors.ImageSharp.ImageInfo info = SixLabors.ImageSharp.Image.Identify(bytes);
			width = info?.Width ?? 0;
			height = info?.Height ?? 0;
			return width > 0 && height > 0;
		}
		catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException or NotSupportedException)
		{
			width = 0;
			height = 0;
			return false;
		}
	}

	public static Raster Load(string path, long maxBytes = DefaultMaxBytes)
	{
		return Decode(File.ReadAllBytes(path), maxBytes);
	}

	public static Raster Decode(byte[] bytes, long maxBytes = DefaultMaxBytes)
	{
		if (NativeImaging.TryDecodeBgra(bytes, 0, maxBytes, out int width, out int height, out byte[] pixels))
			return new Raster(width, height, pixels);
		SixLabors.ImageSharp.ImageInfo info = SixLabors.ImageSharp.Image.Identify(bytes);
		if (info == null || info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height * 4 > maxBytes)
			throw new InvalidDataException("The image is too large or unreadable");
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Bgra32>(bytes);
		SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(image, static context => SixLabors.ImageSharp.Processing.AutoOrientExtensions.AutoOrient(context));
		byte[] managed = new byte[checked(image.Width * image.Height * 4)];
		image.CopyPixelDataTo(managed);
		return new Raster(image.Width, image.Height, managed);
	}

	public Raster Resize(int width, int height, RasterFilter filter = RasterFilter.Bicubic)
	{
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width));
		if (width == Width && height == Height)
			return Clone();
		if (NativeImaging.TryResizeBgra(Pixels, Width, Height, width, height, (int)filter, out byte[] resized))
			return new Raster(width, height, resized);
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(Pixels, Width, Height);
		SixLabors.ImageSharp.Processing.Processors.Transforms.IResampler sampler = filter switch
		{
			RasterFilter.Lanczos3 => SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3,
			RasterFilter.Bilinear => SixLabors.ImageSharp.Processing.KnownResamplers.Triangle,
			RasterFilter.Nearest => SixLabors.ImageSharp.Processing.KnownResamplers.NearestNeighbor,
			_ => SixLabors.ImageSharp.Processing.KnownResamplers.Bicubic
		};
		SixLabors.ImageSharp.Processing.ProcessingExtensions.Mutate(image, context => SixLabors.ImageSharp.Processing.ResizeExtensions.Resize(context, width, height, sampler));
		byte[] managed = new byte[checked(width * height * 4)];
		image.CopyPixelDataTo(managed);
		return new Raster(width, height, managed);
	}

	public Raster ResizeToCover(int width, int height, RasterFilter filter = RasterFilter.Bicubic)
	{
		double scale = Math.Max((double)width / Width, (double)height / Height);
		int scaledWidth = Math.Max(width, (int)Math.Round(Width * scale));
		int scaledHeight = Math.Max(height, (int)Math.Round(Height * scale));
		Raster scaled = Resize(scaledWidth, scaledHeight, filter);
		return scaled.Crop((scaledWidth - width) / 2, (scaledHeight - height) / 2, width, height);
	}

	public Raster ResizeToPad(int width, int height, RasterFilter filter = RasterFilter.Bicubic)
	{
		double scale = Math.Min((double)width / Width, (double)height / Height);
		int scaledWidth = Math.Clamp((int)Math.Round(Width * scale), 1, width);
		int scaledHeight = Math.Clamp((int)Math.Round(Height * scale), 1, height);
		Raster scaled = Resize(scaledWidth, scaledHeight, filter);
		Raster canvas = Blank(width, height);
		int left = Math.Clamp((int)Math.Round((width - Width * scale) / 2), 0, width - scaledWidth);
		int top = Math.Clamp((int)Math.Round((height - Height * scale) / 2), 0, height - scaledHeight);
		for (int row = 0; row < scaledHeight; row++)
			Buffer.BlockCopy(scaled.Pixels, row * scaledWidth * 4, canvas.Pixels, ((top + row) * width + left) * 4, scaledWidth * 4);
		return canvas;
	}

	public Raster Crop(int x, int y, int width, int height)
	{
		if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
			throw new ArgumentOutOfRangeException(nameof(width));
		byte[] result = new byte[checked(width * height * 4)];
		for (int row = 0; row < height; row++)
			Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, result, row * width * 4, width * 4);
		return new Raster(width, height, result);
	}

	public Raster FlipHorizontal()
	{
		byte[] result = new byte[Pixels.Length];
		for (int y = 0; y < Height; y++)
		{
			int row = y * Width;
			for (int x = 0; x < Width; x++)
				Buffer.BlockCopy(Pixels, (row + x) * 4, result, (row + Width - 1 - x) * 4, 4);
		}
		return new Raster(Width, Height, result);
	}

	public Raster FlipVertical()
	{
		byte[] result = new byte[Pixels.Length];
		int stride = Width * 4;
		for (int y = 0; y < Height; y++)
			Buffer.BlockCopy(Pixels, y * stride, result, (Height - 1 - y) * stride, stride);
		return new Raster(Width, Height, result);
	}

	public Raster RotateClockwise()
	{
		byte[] result = new byte[Pixels.Length];
		int newWidth = Height;
		for (int y = 0; y < Height; y++)
		{
			for (int x = 0; x < Width; x++)
				Buffer.BlockCopy(Pixels, (y * Width + x) * 4, result, (x * newWidth + (newWidth - 1 - y)) * 4, 4);
		}
		return new Raster(newWidth, Width, result);
	}

	public Raster Clone()
	{
		return new Raster(Width, Height, (byte[])Pixels.Clone());
	}

	public byte[] EncodePng(PngLevel level = PngLevel.Default)
	{
		if (NativeImaging.TryEncodeBgra(Pixels, Width, Height, 0, (int)level, out byte[] encoded))
			return encoded;
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(Pixels, Width, Height);
		using MemoryStream output = new MemoryStream();
		image.Save(output, new SixLabors.ImageSharp.Formats.Png.PngEncoder
		{
			ColorType = SixLabors.ImageSharp.Formats.Png.PngColorType.RgbWithAlpha,
			CompressionLevel = level switch
			{
				PngLevel.Fast => SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed,
				PngLevel.Best => SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestCompression,
				_ => SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.DefaultCompression
			}
		});
		return output.ToArray();
	}

	public byte[] EncodeJpeg(int quality)
	{
		quality = Math.Clamp(quality, 1, 100);
		if (NativeImaging.TryEncodeBgra(Pixels, Width, Height, 1, quality, out byte[] encoded))
			return encoded;
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(Pixels, Width, Height);
		using MemoryStream output = new MemoryStream();
		image.Save(output, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
		return output.ToArray();
	}

	public byte[] EncodeBmp()
	{
		if (NativeImaging.TryEncodeBgra(Pixels, Width, Height, 2, 0, out byte[] encoded))
			return encoded;
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Bgra32> image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Bgra32>(Pixels, Width, Height);
		using MemoryStream output = new MemoryStream();
		image.Save(output, new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder());
		return output.ToArray();
	}

	public void SavePng(string path, PngLevel level = PngLevel.Default)
	{
		File.WriteAllBytes(path, EncodePng(level));
	}
}
