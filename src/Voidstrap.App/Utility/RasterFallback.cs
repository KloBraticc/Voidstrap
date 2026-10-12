using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility;

internal static class RasterFallback
{
	private static readonly uint[] CrcTable = BuildCrcTable();

	public static bool TryIdentify(byte[] bytes, out int width, out int height)
	{
		width = 0;
		height = 0;
		try
		{
			if (Platform.IsWindows)
			{
				using MemoryStream stream = new MemoryStream(bytes, false);
				BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
				if (decoder.Frames.Count == 0)
					return false;
				width = decoder.Frames[0].PixelWidth;
				height = decoder.Frames[0].PixelHeight;
				return width > 0 && height > 0;
			}
			if (Platform.IsMacOS)
			{
				Voidstrap.Platform.MacOS.MacOSImageDecoder.Decode(bytes, 16384, 16384, out width, out height);
				return width > 0 && height > 0;
			}
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			width = 0;
			height = 0;
		}
		return false;
	}

	public static Raster Decode(byte[] bytes, long maxBytes)
	{
		if (Platform.IsWindows)
		{
			using MemoryStream stream = new MemoryStream(bytes, false);
			BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
			if (decoder.Frames.Count == 0)
				throw new InvalidDataException("The image has no frames");
			BitmapSource frame = decoder.Frames[0];
			if ((long)frame.PixelWidth * frame.PixelHeight * 4 > maxBytes)
				throw new InvalidDataException("The image is too large");
			if (frame.Format != PixelFormats.Bgra32)
				frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
			int stride = checked(frame.PixelWidth * 4);
			byte[] pixels = new byte[checked(stride * frame.PixelHeight)];
			frame.CopyPixels(pixels, stride, 0);
			return new Raster(frame.PixelWidth, frame.PixelHeight, pixels);
		}
		if (Platform.IsMacOS)
		{
			byte[] pixels = Voidstrap.Platform.MacOS.MacOSImageDecoder.Decode(bytes, 16384, 16384, out int width, out int height);
			if ((long)width * height * 4 > maxBytes)
				throw new InvalidDataException("The image is too large");
			return new Raster(width, height, pixels);
		}
		throw new InvalidDataException("The native image library is unavailable, so this image cannot be read");
	}

	public static byte[] Resize(byte[] pixels, int width, int height, int newWidth, int newHeight, RasterFilter filter)
	{
		if (filter == RasterFilter.Nearest)
		{
			byte[] nearest = new byte[checked(newWidth * newHeight * 4)];
			for (int y = 0; y < newHeight; y++)
			{
				int sourceY = Math.Min(height - 1, (int)((y + 0.5) * height / newHeight));
				for (int x = 0; x < newWidth; x++)
				{
					int sourceX = Math.Min(width - 1, (int)((x + 0.5) * width / newWidth));
					Buffer.BlockCopy(pixels, (sourceY * width + sourceX) * 4, nearest, (y * newWidth + x) * 4, 4);
				}
			}
			return nearest;
		}
		float[] premultiplied = new float[checked(width * height * 4)];
		for (int index = 0; index < premultiplied.Length; index += 4)
		{
			float alpha = pixels[index + 3] / 255f;
			premultiplied[index] = pixels[index] * alpha;
			premultiplied[index + 1] = pixels[index + 1] * alpha;
			premultiplied[index + 2] = pixels[index + 2] * alpha;
			premultiplied[index + 3] = pixels[index + 3];
		}
		float[] horizontal = ResamplePass(premultiplied, width, height, newWidth, true);
		float[] vertical = ResamplePass(horizontal, newWidth, height, newHeight, false);
		byte[] result = new byte[checked(newWidth * newHeight * 4)];
		for (int index = 0; index < result.Length; index += 4)
		{
			float alpha = Math.Clamp(vertical[index + 3], 0f, 255f);
			result[index + 3] = (byte)MathF.Round(alpha);
			if (alpha <= 0f)
				continue;
			float scale = 255f / alpha;
			result[index] = (byte)Math.Clamp(MathF.Round(vertical[index] * scale), 0f, 255f);
			result[index + 1] = (byte)Math.Clamp(MathF.Round(vertical[index + 1] * scale), 0f, 255f);
			result[index + 2] = (byte)Math.Clamp(MathF.Round(vertical[index + 2] * scale), 0f, 255f);
		}
		return result;
	}

	private static float[] ResamplePass(float[] source, int width, int height, int target, bool horizontal)
	{
		int sourceLength = horizontal ? width : height;
		int outputWidth = horizontal ? target : width;
		int outputHeight = horizontal ? height : target;
		float[] output = new float[checked(outputWidth * outputHeight * 4)];
		double scale = (double)sourceLength / target;
		double support = Math.Max(1.0, scale);
		double[] weightBuffer = new double[(int)Math.Ceiling(support) * 2 + 3];
		for (int destination = 0; destination < target; destination++)
		{
			double center = (destination + 0.5) * scale - 0.5;
			int start = Math.Max(0, (int)Math.Floor(center - support));
			int end = Math.Min(sourceLength - 1, (int)Math.Ceiling(center + support));
			double total = 0;
			Span<double> weights = weightBuffer.AsSpan(0, end - start + 1);
			for (int sample = start; sample <= end; sample++)
			{
				double weight = Math.Max(0.0, 1.0 - Math.Abs(sample - center) / support);
				weights[sample - start] = weight;
				total += weight;
			}
			if (total <= 0)
			{
				weights.Clear();
				weights[Math.Clamp((int)Math.Round(center), start, end) - start] = 1;
				total = 1;
			}
			int lines = horizontal ? height : width;
			for (int line = 0; line < lines; line++)
			{
				float blue = 0, green = 0, red = 0, alpha = 0;
				for (int sample = start; sample <= end; sample++)
				{
					float weight = (float)(weights[sample - start] / total);
					if (weight == 0f)
						continue;
					int offset = horizontal ? (line * width + sample) * 4 : (sample * width + line) * 4;
					blue += source[offset] * weight;
					green += source[offset + 1] * weight;
					red += source[offset + 2] * weight;
					alpha += source[offset + 3] * weight;
				}
				int target4 = horizontal ? (line * outputWidth + destination) * 4 : (destination * outputWidth + line) * 4;
				output[target4] = blue;
				output[target4 + 1] = green;
				output[target4 + 2] = red;
				output[target4 + 3] = alpha;
			}
		}
		return output;
	}

	public static byte[] EncodePng(byte[] pixels, int width, int height, PngLevel level)
	{
		using MemoryStream raw = new MemoryStream();
		using (ZLibStream zlib = new ZLibStream(raw, level switch
		{
			PngLevel.Fast => CompressionLevel.Fastest,
			PngLevel.Best => CompressionLevel.SmallestSize,
			_ => CompressionLevel.Optimal
		}, leaveOpen: true))
		{
			byte[] row = new byte[width * 4 + 1];
			for (int y = 0; y < height; y++)
			{
				row[0] = 0;
				for (int x = 0; x < width; x++)
				{
					int from = (y * width + x) * 4;
					int to = 1 + x * 4;
					row[to] = pixels[from + 2];
					row[to + 1] = pixels[from + 1];
					row[to + 2] = pixels[from];
					row[to + 3] = pixels[from + 3];
				}
				zlib.Write(row, 0, row.Length);
			}
		}
		using MemoryStream png = new MemoryStream();
		png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
		byte[] header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
		header[8] = 8;
		header[9] = 6;
		WriteChunk(png, "IHDR", header);
		WriteChunk(png, "IDAT", raw.ToArray());
		WriteChunk(png, "IEND", []);
		return png.ToArray();
	}

	private static void WriteChunk(Stream output, string type, byte[] data)
	{
		Span<byte> number = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
		output.Write(number);
		byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
		output.Write(typeBytes);
		output.Write(data);
		uint crc = 0xFFFFFFFFu;
		foreach (byte value in typeBytes)
			crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
		foreach (byte value in data)
			crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
		BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFFu);
		output.Write(number);
	}

	private static uint[] BuildCrcTable()
	{
		uint[] table = new uint[256];
		for (uint index = 0; index < 256; index++)
		{
			uint value = index;
			for (int bit = 0; bit < 8; bit++)
				value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
			table[index] = value;
		}
		return table;
	}

	public static byte[] EncodeBmp(byte[] pixels, int width, int height)
	{
		int imageSize = checked(width * height * 4);
		byte[] bmp = new byte[checked(14 + 40 + imageSize)];
		bmp[0] = (byte)'B';
		bmp[1] = (byte)'M';
		BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
		BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
		BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
		BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
		BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -height);
		BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
		BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
		BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), imageSize);
		Buffer.BlockCopy(pixels, 0, bmp, 54, imageSize);
		return bmp;
	}

	public static byte[] EncodeJpeg(byte[] pixels, int width, int height, int quality)
	{
		if (!Platform.IsWindows)
			throw new InvalidOperationException("JPEG encoding needs the native image library");
		BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
		JpegBitmapEncoder encoder = new JpegBitmapEncoder { QualityLevel = quality };
		encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
		using MemoryStream output = new MemoryStream();
		encoder.Save(output);
		return output.ToArray();
	}
}
