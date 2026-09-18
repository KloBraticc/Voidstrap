using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Voidstrap.Utility;

internal static class DdsTextureCodec
{
	internal const int WaterFrameCount = 25;

	internal const int WaterSize = 256;

	internal const int WaterFileLength = 87536;

	private const int WaterLevels = 9;

	private const float HeightSlope = 4f;

	internal static byte[] CreateDds(ReadOnlySpan<byte> fourCC, ReadOnlySpan<byte> block)
	{
		byte[] bytes = new byte[128 + block.Length];
		Span<byte> span = bytes;
		"DDS "u8.CopyTo(span);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124);
		BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x81007);
		BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 4);
		BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 4);
		BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)block.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 1);
		BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 32);
		BinaryPrimitives.WriteUInt32LittleEndian(span[80..], 0x4);
		fourCC.CopyTo(span[84..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 0x1000);
		block.CopyTo(span[128..]);
		return bytes;
	}

	internal static byte PercentToByte(int percent)
	{
		return (byte)((Math.Clamp(percent, 0, 100) * 255 + 50) / 100);
	}

	internal static byte[] CreateFlatStuds(int shadePercent)
	{
		int gray = PercentToByte(shadePercent);
		int redBlue = (gray * 31 + 127) / 255;
		int green = (gray * 63 + 127) / 255;
		int color = (redBlue << 11) | (green << 5) | redBlue;
		byte low = (byte)color;
		byte high = (byte)(color >> 8);
		return CreateDds("DXT1"u8, [low, high, low, high, 0, 0, 0, 0]);
	}

	internal static byte[][] BuildWaterFrames(int style, int strengthPercent, int speed, int frozenFrame, int glitchPercent, IReadOnlyList<byte[]>? originals, IReadOnlyList<byte[]>? heightFrames)
	{
		float strength = Math.Clamp(strengthPercent, 0, 300) / 100f;
		int step = Math.Clamp(speed, 1, 4);
		int pixels = WaterSize * WaterSize;
		byte[][] output = new byte[WaterFrameCount][];
		if (style == 2)
		{
			Array.Fill(output, EncodeWaterFrame(new float[pixels], new float[pixels]));
			return output;
		}
		if (style == 5)
		{
			if (heightFrames is null || heightFrames.Count == 0)
				throw new ArgumentException("No custom water images were chosen.");
			int count = Math.Min(heightFrames.Count, WaterFrameCount);
			byte[]?[] encoded = new byte[count][];
			for (int slot = 0; slot < WaterFrameCount; slot++)
			{
				int source = slot * step * count / WaterFrameCount % count;
				if (encoded[source] is null)
				{
					float[] x = new float[pixels];
					float[] y = new float[pixels];
					HeightToNormals(heightFrames[source], x, y);
					ApplyStrength(x, y, strength);
					encoded[source] = EncodeWaterFrame(x, y);
				}
				output[slot] = encoded[source]!;
			}
			return output;
		}
		if (style is not (1 or 3 or 4))
			throw new ArgumentOutOfRangeException(nameof(style));
		if (originals is null || originals.Count != WaterFrameCount)
			throw new ArgumentException("The original water frames are missing.");
		float[][] xs = new float[WaterFrameCount][];
		float[][] ys = new float[WaterFrameCount][];
		for (int index = 0; index < WaterFrameCount; index++)
		{
			xs[index] = new float[pixels];
			ys[index] = new float[pixels];
			DecodeWaterFrame(originals[index], xs[index], ys[index]);
		}
		if (style == 3)
		{
			int held = Math.Clamp(frozenFrame, 1, WaterFrameCount) - 1;
			float[] x = (float[])xs[held].Clone();
			float[] y = (float[])ys[held].Clone();
			ApplyStrength(x, y, strength);
			Array.Fill(output, EncodeWaterFrame(x, y));
			return output;
		}
		int glitch = Math.Clamp(glitchPercent, 0, 100);
		float amount = glitch / 100f;
		Random? random = style == 4 ? new Random(unchecked(0x5EED + glitch * 7919 + step * 104729)) : null;
		for (int slot = 0; slot < WaterFrameCount; slot++)
		{
			int source = slot * step % WaterFrameCount;
			if (random is not null && random.NextSingle() < amount * 0.5f)
				source = random.Next(WaterFrameCount);
			float[] x = (float[])xs[source].Clone();
			float[] y = (float[])ys[source].Clone();
			if (random is not null)
				Glitch(random, amount, xs, ys, x, y);
			ApplyStrength(x, y, strength);
			output[slot] = EncodeWaterFrame(x, y);
		}
		return output;
	}

	internal static void DecodeWaterFrame(byte[] dds, float[] x, float[] y)
	{
		if (dds.Length < WaterFileLength
			|| !dds.AsSpan(0, 4).SequenceEqual("DDS "u8)
			|| !dds.AsSpan(84, 4).SequenceEqual("DXT5"u8)
			|| BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12)) != WaterSize
			|| BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16)) != WaterSize)
			throw new InvalidDataException("The water texture has an unexpected layout.");
		Span<byte> alphas = stackalloc byte[8];
		Span<byte> greens = stackalloc byte[4];
		int blocks = WaterSize / 4;
		for (int blockY = 0; blockY < blocks; blockY++)
		{
			for (int blockX = 0; blockX < blocks; blockX++)
			{
				int offset = 128 + (blockY * blocks + blockX) * 16;
				AlphaPalette(dds[offset], dds[offset + 1], alphas);
				ulong alphaBits = 0;
				for (int part = 0; part < 6; part++)
					alphaBits |= (ulong)dds[offset + 2 + part] << (8 * part);
				GreenPalette(BinaryPrimitives.ReadUInt16LittleEndian(dds.AsSpan(offset + 8)), BinaryPrimitives.ReadUInt16LittleEndian(dds.AsSpan(offset + 10)), greens);
				uint colorBits = BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(offset + 12));
				for (int texel = 0; texel < 16; texel++)
				{
					int index = (blockY * 4 + texel / 4) * WaterSize + blockX * 4 + texel % 4;
					x[index] = alphas[(int)(alphaBits >> (3 * texel)) & 7] / 127.5f - 1f;
					y[index] = greens[(int)(colorBits >> (2 * texel)) & 3] / 127.5f - 1f;
				}
			}
		}
	}

	internal static byte[] EncodeWaterFrame(float[] x, float[] y)
	{
		byte[] dds = new byte[WaterFileLength];
		Span<byte> span = dds;
		"DDS "u8.CopyTo(span);
		BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124);
		BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0xA1007);
		BinaryPrimitives.WriteUInt32LittleEndian(span[12..], WaterSize);
		BinaryPrimitives.WriteUInt32LittleEndian(span[16..], WaterSize);
		BinaryPrimitives.WriteUInt32LittleEndian(span[20..], WaterSize * WaterSize);
		BinaryPrimitives.WriteUInt32LittleEndian(span[28..], WaterLevels);
		BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 32);
		BinaryPrimitives.WriteUInt32LittleEndian(span[80..], 0x4);
		"DXT5"u8.CopyTo(span[84..]);
		BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 0x401008);
		int offset = 128;
		int size = WaterSize;
		float[] levelX = x;
		float[] levelY = y;
		for (int level = 0; level < WaterLevels; level++)
		{
			offset = EncodeLevel(dds, offset, levelX, levelY, size);
			if (level == WaterLevels - 1)
				break;
			int next = Math.Max(1, size / 2);
			float[] nextX = new float[next * next];
			float[] nextY = new float[next * next];
			for (int row = 0; row < next; row++)
			{
				for (int col = 0; col < next; col++)
				{
					float sumX = 0f;
					float sumY = 0f;
					float sumZ = 0f;
					for (int dy = 0; dy < 2; dy++)
					{
						for (int dx = 0; dx < 2; dx++)
						{
							int source = Math.Min(row * 2 + dy, size - 1) * size + Math.Min(col * 2 + dx, size - 1);
							float px = levelX[source];
							float py = levelY[source];
							sumX += px;
							sumY += py;
							sumZ += MathF.Sqrt(MathF.Max(0f, 1f - px * px - py * py));
						}
					}
					float length = MathF.Sqrt(sumX * sumX + sumY * sumY + sumZ * sumZ);
					if (length > 1e-6f)
					{
						nextX[row * next + col] = sumX / length;
						nextY[row * next + col] = sumY / length;
					}
				}
			}
			levelX = nextX;
			levelY = nextY;
			size = next;
		}
		if (offset != WaterFileLength)
			throw new InvalidOperationException("The water texture was not the expected size.");
		return dds;
	}

	internal static byte[] ComposeWaterPreview(byte[] dds)
	{
		int pixels = WaterSize * WaterSize;
		float[] x = new float[pixels];
		float[] y = new float[pixels];
		DecodeWaterFrame(dds, x, y);
		byte[] image = new byte[pixels * 4];
		for (int row = 0; row < WaterSize; row++)
		{
			for (int col = 0; col < WaterSize; col++)
			{
				int index = row * WaterSize + col;
				float alpha = ToByte(x[index]) / 255f;
				float green = ToByte(y[index]) / 255f;
				float checker = ((row >> 3) + (col >> 3)) % 2 == 0 ? 0.02f : 0.09f;
				float behind = checker * (1f - alpha);
				byte gray = EncodeGamma(behind);
				image[index * 4] = gray;
				image[index * 4 + 1] = EncodeGamma(alpha * green + behind);
				image[index * 4 + 2] = gray;
				image[index * 4 + 3] = 255;
			}
		}
		return image;
	}

	private static byte EncodeGamma(float value)
	{
		return (byte)Math.Clamp((int)MathF.Round(MathF.Pow(Math.Clamp(value, 0f, 1f), 1f / 2.2f) * 255f), 0, 255);
	}

	internal static void HeightToNormals(byte[] heights, float[] x, float[] y)
	{
		if (heights.Length != WaterSize * WaterSize)
			throw new ArgumentException("A water image has the wrong size.");
		for (int row = 0; row < WaterSize; row++)
		{
			int up = (row + WaterSize - 1) % WaterSize * WaterSize;
			int down = (row + 1) % WaterSize * WaterSize;
			int current = row * WaterSize;
			for (int col = 0; col < WaterSize; col++)
			{
				int left = (col + WaterSize - 1) % WaterSize;
				int right = (col + 1) % WaterSize;
				float slopeX = -(heights[current + right] - heights[current + left]) / 255f * HeightSlope;
				float slopeY = -(heights[down + col] - heights[up + col]) / 255f * HeightSlope;
				float length = MathF.Sqrt(slopeX * slopeX + slopeY * slopeY + 1f);
				x[current + col] = slopeX / length;
				y[current + col] = slopeY / length;
			}
		}
	}

	internal static void ApplyStrength(float[] x, float[] y, float strength)
	{
		if (strength == 1f)
			return;
		for (int index = 0; index < x.Length; index++)
		{
			float nx = x[index] * strength;
			float ny = y[index] * strength;
			float length = MathF.Sqrt(nx * nx + ny * ny);
			if (length > 1f)
			{
				nx /= length;
				ny /= length;
			}
			x[index] = nx;
			y[index] = ny;
		}
	}

	internal static byte ToByte(float value)
	{
		return (byte)Math.Clamp((int)MathF.Round((value + 1f) * 127.5f), 0, 255);
	}

	private static void Glitch(Random random, float amount, float[][] xs, float[][] ys, float[] x, float[] y)
	{
		int regions = Math.Max(1, (int)MathF.Round(amount * 24f));
		for (int region = 0; region < regions; region++)
		{
			int size = 16 << random.Next(3);
			int left = random.Next(WaterSize);
			int top = random.Next(WaterSize);
			int kind = random.Next(3);
			int other = random.Next(WaterFrameCount);
			int shiftX = random.Next(8, 65);
			int shiftY = random.Next(WaterSize);
			float tiltX = (random.NextSingle() * 2f - 1f) * 0.8f;
			float tiltY = (random.NextSingle() * 2f - 1f) * 0.8f;
			for (int row = 0; row < size; row++)
			{
				int targetRow = (top + row) % WaterSize;
				for (int col = 0; col < size; col++)
				{
					int targetCol = (left + col) % WaterSize;
					int target = targetRow * WaterSize + targetCol;
					if (kind == 0)
					{
						int source = (targetRow + shiftY) % WaterSize * WaterSize + (targetCol + shiftX) % WaterSize;
						x[target] = xs[other][source];
						y[target] = ys[other][source];
					}
					else if (kind == 1)
					{
						x[target] = tiltX;
						y[target] = tiltY;
					}
					else
					{
						int source = top * WaterSize + (targetCol + shiftX) % WaterSize;
						x[target] = xs[other][source];
						y[target] = ys[other][source];
					}
				}
			}
		}
	}

	private static int EncodeLevel(byte[] dds, int offset, float[] x, float[] y, int size)
	{
		int blocks = Math.Max(1, (size + 3) / 4);
		Span<byte> alphaValues = stackalloc byte[16];
		Span<byte> greenValues = stackalloc byte[16];
		Span<byte> alphas = stackalloc byte[8];
		Span<byte> greens = stackalloc byte[4];
		for (int blockY = 0; blockY < blocks; blockY++)
		{
			for (int blockX = 0; blockX < blocks; blockX++)
			{
				byte alphaMax = 0;
				byte alphaMin = 255;
				byte greenMax = 0;
				byte greenMin = 255;
				for (int texel = 0; texel < 16; texel++)
				{
					int row = Math.Min(blockY * 4 + texel / 4, size - 1);
					int col = Math.Min(blockX * 4 + texel % 4, size - 1);
					byte alpha = ToByte(x[row * size + col]);
					byte green = ToByte(y[row * size + col]);
					alphaValues[texel] = alpha;
					greenValues[texel] = green;
					alphaMax = Math.Max(alphaMax, alpha);
					alphaMin = Math.Min(alphaMin, alpha);
					greenMax = Math.Max(greenMax, green);
					greenMin = Math.Min(greenMin, green);
				}
				AlphaPalette(alphaMax, alphaMin, alphas);
				ulong alphaBits = 0;
				if (alphaMax != alphaMin)
				{
					for (int texel = 0; texel < 16; texel++)
						alphaBits |= (ulong)Nearest(alphas, alphaValues[texel]) << (3 * texel);
				}
				int high = (greenMax * 63 + 127) / 255;
				int low = (greenMin * 63 + 127) / 255;
				ushort color0 = (ushort)((5 << 11) | (high << 5));
				ushort color1 = (ushort)((5 << 11) | (low << 5));
				uint colorBits = 0;
				if (color0 != color1)
				{
					GreenPalette(color0, color1, greens);
					for (int texel = 0; texel < 16; texel++)
						colorBits |= (uint)Nearest(greens, greenValues[texel]) << (2 * texel);
				}
				int block = offset + (blockY * blocks + blockX) * 16;
				dds[block] = alphaMax;
				dds[block + 1] = alphaMin;
				for (int part = 0; part < 6; part++)
					dds[block + 2 + part] = (byte)(alphaBits >> (8 * part));
				BinaryPrimitives.WriteUInt16LittleEndian(dds.AsSpan(block + 8), color0);
				BinaryPrimitives.WriteUInt16LittleEndian(dds.AsSpan(block + 10), color1);
				BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(block + 12), colorBits);
			}
		}
		return offset + blocks * blocks * 16;
	}

	private static void AlphaPalette(byte alpha0, byte alpha1, Span<byte> palette)
	{
		palette[0] = alpha0;
		palette[1] = alpha1;
		if (alpha0 > alpha1)
		{
			for (int index = 2; index < 8; index++)
				palette[index] = (byte)(((8 - index) * alpha0 + (index - 1) * alpha1) / 7);
			return;
		}
		for (int index = 2; index < 6; index++)
			palette[index] = (byte)(((6 - index) * alpha0 + (index - 1) * alpha1) / 5);
		palette[6] = 0;
		palette[7] = 255;
	}

	private static void GreenPalette(ushort color0, ushort color1, Span<byte> palette)
	{
		int green0 = Expand6((color0 >> 5) & 63);
		int green1 = Expand6((color1 >> 5) & 63);
		palette[0] = (byte)green0;
		palette[1] = (byte)green1;
		palette[2] = (byte)((2 * green0 + green1) / 3);
		palette[3] = (byte)((green0 + 2 * green1) / 3);
	}

	private static int Expand6(int value)
	{
		return (value << 2) | (value >> 4);
	}

	private static int Nearest(ReadOnlySpan<byte> palette, byte value)
	{
		int best = 0;
		int bestError = int.MaxValue;
		for (int index = 0; index < palette.Length; index++)
		{
			int error = Math.Abs(palette[index] - value);
			if (error < bestError)
			{
				best = index;
				bestError = error;
			}
		}
		return best;
	}
}
