using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Voidstrap.Utility;

internal static class IconFile
{
	public static void Write(Image<Rgba32> source, string path, IReadOnlyList<int> sizes, ResizeMode mode)
	{
		List<byte[]> frames = new List<byte[]>(sizes.Count);
		foreach (int size in sizes)
		{
			using Image<Rgba32> frame = source.Clone(context => context.Resize(new ResizeOptions
			{
				Size = new Size(size, size),
				Mode = mode,
				Sampler = KnownResamplers.Lanczos3,
				PadColor = Color.Transparent
			}));
			using MemoryStream png = new MemoryStream();
			frame.SaveAsPng(png, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
			frames.Add(png.ToArray());
		}
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string temp = path + "." + Environment.ProcessId + ".tmp";
		try
		{
			using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				writer.Write((ushort)0);
				writer.Write((ushort)1);
				writer.Write((ushort)frames.Count);
				int offset = 6 + 16 * frames.Count;
				for (int index = 0; index < frames.Count; index++)
				{
					int size = sizes[index];
					writer.Write((byte)(size >= 256 ? 0 : size));
					writer.Write((byte)(size >= 256 ? 0 : size));
					writer.Write((byte)0);
					writer.Write((byte)0);
					writer.Write((ushort)1);
					writer.Write((ushort)32);
					writer.Write(frames[index].Length);
					writer.Write(offset);
					offset += frames[index].Length;
				}
				foreach (byte[] frame in frames)
					writer.Write(frame);
			}
			File.Move(temp, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temp))
				File.Delete(temp);
		}
	}

	public static Image<Rgba32>? ReadLargestFrame(byte[] data)
	{
		if (data.Length < 6 || BitConverter.ToUInt16(data, 0) != 0 || BitConverter.ToUInt16(data, 2) is not (1 or 2))
			return null;
		int count = BitConverter.ToUInt16(data, 4);
		int bestArea = -1;
		int bestOffset = 0;
		int bestLength = 0;
		for (int index = 0; index < count; index++)
		{
			int entry = 6 + index * 16;
			if (entry + 16 > data.Length)
				break;
			int width = data[entry] == 0 ? 256 : data[entry];
			int height = data[entry + 1] == 0 ? 256 : data[entry + 1];
			int length = BitConverter.ToInt32(data, entry + 8);
			int offset = BitConverter.ToInt32(data, entry + 12);
			if (length <= 0 || offset < 0 || (long)offset + length > data.Length || width * height <= bestArea)
				continue;
			bestArea = width * height;
			bestOffset = offset;
			bestLength = length;
		}
		if (bestArea < 0)
			return null;
		ReadOnlySpan<byte> image = data.AsSpan(bestOffset, bestLength);
		if (image.Length > 8 && image[0] == 0x89 && image[1] == (byte)'P' && image[2] == (byte)'N' && image[3] == (byte)'G')
			return Image.Load<Rgba32>(image);
		return ReadDib(image);
	}

	private static Image<Rgba32>? ReadDib(ReadOnlySpan<byte> dib)
	{
		if (dib.Length < 40)
			return null;
		int headerSize = BitConverter.ToInt32(dib);
		int width = BitConverter.ToInt32(dib[4..]);
		int height = BitConverter.ToInt32(dib[8..]) / 2;
		int bits = BitConverter.ToUInt16(dib[14..]);
		if (bits != 32 || width <= 0 || height <= 0 || width > 1024 || height > 1024)
			return null;
		int stride = width * 4;
		if ((long)headerSize + (long)stride * height > dib.Length)
			return null;
		Image<Rgba32> result = new Image<Rgba32>(width, height);
		bool hasAlpha = false;
		for (int y = 0; y < height; y++)
		{
			ReadOnlySpan<byte> row = dib.Slice(headerSize + (height - 1 - y) * stride, stride);
			for (int x = 0; x < width; x++)
			{
				hasAlpha |= row[x * 4 + 3] != 0;
				result[x, y] = new Rgba32(row[x * 4 + 2], row[x * 4 + 1], row[x * 4], row[x * 4 + 3]);
			}
		}
		if (!hasAlpha)
		{
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					Rgba32 pixel = result[x, y];
					pixel.A = 255;
					result[x, y] = pixel;
				}
			}
		}
		return result;
	}
}
