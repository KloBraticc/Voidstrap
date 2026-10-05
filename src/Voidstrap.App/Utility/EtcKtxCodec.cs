using System;
using System.Buffers.Binary;
using System.IO;

namespace Voidstrap.Utility;

internal static class EtcKtxCodec
{
	private const uint Etc1InternalFormat = 0x8D64;

	private const uint RgbBaseFormat = 0x1907;

	private const int HeaderLength = 64;

	private static ReadOnlySpan<byte> Identifier => [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

	private static readonly int[,] Modifiers =
	{
		{ 2, 8 }, { 5, 17 }, { 9, 29 }, { 13, 42 }, { 18, 60 }, { 24, 80 }, { 33, 106 }, { 47, 183 }
	};

	internal static byte[] ToWaterDds(byte[] ktx)
	{
		int pixels = DdsTextureCodec.WaterSize * DdsTextureCodec.WaterSize;
		float[] x = new float[pixels];
		float[] y = new float[pixels];
		DecodeWater(ktx, x, y);
		return DdsTextureCodec.EncodeWaterFrame(x, y);
	}

	internal static byte[] FromWaterDds(byte[] dds)
	{
		int pixels = DdsTextureCodec.WaterSize * DdsTextureCodec.WaterSize;
		float[] x = new float[pixels];
		float[] y = new float[pixels];
		DdsTextureCodec.DecodeWaterFrame(dds, x, y);
		return EncodeWater(x, y);
	}

	internal static void DecodeWater(byte[] ktx, float[] x, float[] y)
	{
		int size = DdsTextureCodec.WaterSize;
		byte[] rgb = DecodeTopLevel(ktx, out int width, out int height);
		if (width != size || height != size)
			throw new InvalidDataException("The water texture has an unexpected size.");
		for (int index = 0; index < size * size; index++)
		{
			x[index] = rgb[index * 3] / 127.5f - 1f;
			y[index] = rgb[index * 3 + 1] / 127.5f - 1f;
		}
	}

	internal static byte[] DecodeTopLevel(byte[] ktx, out int width, out int height)
	{
		if (ktx.Length < HeaderLength + 4 || !ktx.AsSpan(0, 12).SequenceEqual(Identifier))
			throw new InvalidDataException("The texture is not a KTX file.");
		if (BinaryPrimitives.ReadUInt32LittleEndian(ktx.AsSpan(12)) != 0x04030201
			|| BinaryPrimitives.ReadUInt32LittleEndian(ktx.AsSpan(28)) != Etc1InternalFormat)
			throw new InvalidDataException("The KTX texture is not ETC1.");
		width = (int)BinaryPrimitives.ReadUInt32LittleEndian(ktx.AsSpan(36));
		height = (int)BinaryPrimitives.ReadUInt32LittleEndian(ktx.AsSpan(40));
		int keyValueBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(ktx.AsSpan(60));
		int offset = HeaderLength + keyValueBytes;
		if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || offset + 4 > ktx.Length)
			throw new InvalidDataException("The KTX texture has an unexpected layout.");
		int imageSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(ktx.AsSpan(offset));
		int blocksWide = Math.Max(1, (width + 3) / 4);
		int blocksHigh = Math.Max(1, (height + 3) / 4);
		if (imageSize != blocksWide * blocksHigh * 8 || offset + 4 + imageSize > ktx.Length)
			throw new InvalidDataException("The KTX texture data is truncated.");

		byte[] rgb = new byte[width * height * 3];
		Span<byte> block = stackalloc byte[48];
		for (int blockY = 0; blockY < blocksHigh; blockY++)
		{
			for (int blockX = 0; blockX < blocksWide; blockX++)
			{
				DecodeBlock(ktx.AsSpan(offset + 4 + (blockY * blocksWide + blockX) * 8, 8), block);
				for (int row = 0; row < 4; row++)
				{
					int pixelY = blockY * 4 + row;
					if (pixelY >= height)
						continue;
					for (int column = 0; column < 4; column++)
					{
						int pixelX = blockX * 4 + column;
						if (pixelX >= width)
							continue;
						block.Slice((row * 4 + column) * 3, 3).CopyTo(rgb.AsSpan((pixelY * width + pixelX) * 3, 3));
					}
				}
			}
		}
		return rgb;
	}

	internal static byte[] EncodeWater(float[] x, float[] y)
	{
		int size = DdsTextureCodec.WaterSize;
		int levels = 1;
		for (int dimension = size; dimension > 1; dimension /= 2)
			levels++;

		using MemoryStream output = new();
		Span<byte> header = stackalloc byte[HeaderLength];
		header.Clear();
		Identifier.CopyTo(header);
		BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0x04030201);
		BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header[28..], Etc1InternalFormat);
		BinaryPrimitives.WriteUInt32LittleEndian(header[32..], RgbBaseFormat);
		BinaryPrimitives.WriteUInt32LittleEndian(header[36..], (uint)size);
		BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)size);
		BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 1);
		BinaryPrimitives.WriteUInt32LittleEndian(header[56..], (uint)levels);
		output.Write(header);

		float[] levelX = x;
		float[] levelY = y;
		int levelSize = size;
		Span<byte> sizeBytes = stackalloc byte[4];
		for (int level = 0; level < levels; level++)
		{
			byte[] rgb = new byte[levelSize * levelSize * 3];
			for (int index = 0; index < levelSize * levelSize; index++)
			{
				float nx = Math.Clamp(levelX[index], -1f, 1f);
				float ny = Math.Clamp(levelY[index], -1f, 1f);
				float nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));
				rgb[index * 3] = ToByte(nx);
				rgb[index * 3 + 1] = ToByte(ny);
				rgb[index * 3 + 2] = ToByte(nz);
			}

			byte[] blocks = EncodeLevel(rgb, levelSize, levelSize);
			BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)blocks.Length);
			output.Write(sizeBytes);
			output.Write(blocks);

			if (level == levels - 1)
				break;
			int next = Math.Max(1, levelSize / 2);
			float[] nextX = new float[next * next];
			float[] nextY = new float[next * next];
			for (int row = 0; row < next; row++)
			{
				for (int column = 0; column < next; column++)
				{
					float sumX = 0f;
					float sumY = 0f;
					float sumZ = 0f;
					for (int dy = 0; dy < 2; dy++)
					{
						for (int dx = 0; dx < 2; dx++)
						{
							int source = Math.Min(row * 2 + dy, levelSize - 1) * levelSize + Math.Min(column * 2 + dx, levelSize - 1);
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
						nextX[row * next + column] = sumX / length;
						nextY[row * next + column] = sumY / length;
					}
				}
			}
			levelX = nextX;
			levelY = nextY;
			levelSize = next;
		}
		return output.ToArray();
	}

	private static byte ToByte(float value)
	{
		return (byte)Math.Clamp((int)MathF.Round((value + 1f) * 127.5f), 0, 255);
	}

	internal static byte[] EncodeLevel(byte[] rgb, int width, int height)
	{
		int blocksWide = Math.Max(1, (width + 3) / 4);
		int blocksHigh = Math.Max(1, (height + 3) / 4);
		byte[] output = new byte[blocksWide * blocksHigh * 8];
		int[] pixels = new int[48];
		for (int blockY = 0; blockY < blocksHigh; blockY++)
		{
			for (int blockX = 0; blockX < blocksWide; blockX++)
			{
				for (int row = 0; row < 4; row++)
				{
					int pixelY = Math.Min(blockY * 4 + row, height - 1);
					for (int column = 0; column < 4; column++)
					{
						int pixelX = Math.Min(blockX * 4 + column, width - 1);
						int source = (pixelY * width + pixelX) * 3;
						int target = (row * 4 + column) * 3;
						pixels[target] = rgb[source];
						pixels[target + 1] = rgb[source + 1];
						pixels[target + 2] = rgb[source + 2];
					}
				}
				EncodeBlock(pixels, output.AsSpan((blockY * blocksWide + blockX) * 8, 8));
			}
		}
		return output;
	}

	private static void EncodeBlock(int[] pixels, Span<byte> output)
	{
		long bestError = long.MaxValue;
		ulong bestBits = 0;
		for (int flip = 0; flip < 2; flip++)
		{
			int[] firstAverage = Average(pixels, flip, 0);
			int[] secondAverage = Average(pixels, flip, 1);

			int[] first5 = [Quantize5(firstAverage[0]), Quantize5(firstAverage[1]), Quantize5(firstAverage[2])];
			int[] second5 = [Quantize5(secondAverage[0]), Quantize5(secondAverage[1]), Quantize5(secondAverage[2])];
			bool differential = true;
			for (int channel = 0; channel < 3; channel++)
			{
				int delta = second5[channel] - first5[channel];
				if (delta < -4 || delta > 3)
					differential = false;
			}

			if (differential)
			{
				int[] firstColor = [Expand5(first5[0]), Expand5(first5[1]), Expand5(first5[2])];
				int[] secondColor = [Expand5(second5[0]), Expand5(second5[1]), Expand5(second5[2])];
				long error = ChooseTables(pixels, flip, firstColor, secondColor, out int firstTable, out int secondTable, out uint indexBits);
				if (error < bestError)
				{
					bestError = error;
					uint high = (uint)(first5[0] << 27) | (uint)((second5[0] - first5[0]) & 7) << 24
						| (uint)(first5[1] << 19) | (uint)((second5[1] - first5[1]) & 7) << 16
						| (uint)(first5[2] << 11) | (uint)((second5[2] - first5[2]) & 7) << 8
						| (uint)(firstTable << 5) | (uint)(secondTable << 2) | 2u | (uint)flip;
					bestBits = (ulong)high << 32 | indexBits;
				}
			}

			int[] first4 = [Quantize4(firstAverage[0]), Quantize4(firstAverage[1]), Quantize4(firstAverage[2])];
			int[] second4 = [Quantize4(secondAverage[0]), Quantize4(secondAverage[1]), Quantize4(secondAverage[2])];
			int[] firstIndividual = [first4[0] * 17, first4[1] * 17, first4[2] * 17];
			int[] secondIndividual = [second4[0] * 17, second4[1] * 17, second4[2] * 17];
			long individualError = ChooseTables(pixels, flip, firstIndividual, secondIndividual, out int firstIndividualTable, out int secondIndividualTable, out uint individualBits);
			if (individualError < bestError)
			{
				bestError = individualError;
				uint high = (uint)(first4[0] << 28) | (uint)(second4[0] << 24)
					| (uint)(first4[1] << 20) | (uint)(second4[1] << 16)
					| (uint)(first4[2] << 12) | (uint)(second4[2] << 8)
					| (uint)(firstIndividualTable << 5) | (uint)(secondIndividualTable << 2) | (uint)flip;
				bestBits = (ulong)high << 32 | individualBits;
			}
		}
		BinaryPrimitives.WriteUInt64BigEndian(output, bestBits);
	}

	private static long ChooseTables(int[] pixels, int flip, int[] firstColor, int[] secondColor, out int firstTable, out int secondTable, out uint indexBits)
	{
		indexBits = 0;
		long total = 0;
		firstTable = 0;
		secondTable = 0;
		for (int subBlock = 0; subBlock < 2; subBlock++)
		{
			int[] baseColor = subBlock == 0 ? firstColor : secondColor;
			long bestError = long.MaxValue;
			int bestTable = 0;
			uint bestIndices = 0;
			for (int table = 0; table < 8; table++)
			{
				long error = 0;
				uint indices = 0;
				for (int row = 0; row < 4; row++)
				{
					for (int column = 0; column < 4; column++)
					{
						if (SubBlockOf(flip, column, row) != subBlock)
							continue;
						int pixel = (row * 4 + column) * 3;
						long bestPixelError = long.MaxValue;
						int bestSelector = 0;
						for (int selector = 0; selector < 4; selector++)
						{
							int modifier = selector switch
							{
								0 => Modifiers[table, 0],
								1 => Modifiers[table, 1],
								2 => -Modifiers[table, 0],
								_ => -Modifiers[table, 1]
							};
							long pixelError = 0;
							for (int channel = 0; channel < 3; channel++)
							{
								int difference = Math.Clamp(baseColor[channel] + modifier, 0, 255) - pixels[pixel + channel];
								pixelError += difference * difference;
							}
							if (pixelError < bestPixelError)
							{
								bestPixelError = pixelError;
								bestSelector = selector;
							}
						}
						error += bestPixelError;
						int bit = column * 4 + row;
						indices |= (uint)(bestSelector & 1) << bit;
						indices |= (uint)(bestSelector >> 1) << (bit + 16);
					}
				}
				if (error < bestError)
				{
					bestError = error;
					bestTable = table;
					bestIndices = indices;
				}
			}
			total += bestError;
			indexBits |= bestIndices;
			if (subBlock == 0)
				firstTable = bestTable;
			else
				secondTable = bestTable;
		}
		return total;
	}

	private static int SubBlockOf(int flip, int column, int row)
	{
		return flip == 1 ? (row >= 2 ? 1 : 0) : (column >= 2 ? 1 : 0);
	}

	private static int[] Average(int[] pixels, int flip, int subBlock)
	{
		int[] sum = new int[3];
		int count = 0;
		for (int row = 0; row < 4; row++)
		{
			for (int column = 0; column < 4; column++)
			{
				if (SubBlockOf(flip, column, row) != subBlock)
					continue;
				int pixel = (row * 4 + column) * 3;
				sum[0] += pixels[pixel];
				sum[1] += pixels[pixel + 1];
				sum[2] += pixels[pixel + 2];
				count++;
			}
		}
		return [(sum[0] + count / 2) / count, (sum[1] + count / 2) / count, (sum[2] + count / 2) / count];
	}

	private static int Quantize5(int value)
	{
		return Math.Clamp((value * 31 + 127) / 255, 0, 31);
	}

	private static int Quantize4(int value)
	{
		return Math.Clamp((value * 15 + 127) / 255, 0, 15);
	}

	private static int Expand5(int value)
	{
		return (value << 3) | (value >> 2);
	}

	private static void DecodeBlock(ReadOnlySpan<byte> data, Span<byte> rgb)
	{
		uint high = BinaryPrimitives.ReadUInt32BigEndian(data);
		uint low = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
		bool differential = (high & 2) != 0;
		int flip = (int)(high & 1);
		int firstTable = (int)(high >> 5) & 7;
		int secondTable = (int)(high >> 2) & 7;
		Span<int> first = stackalloc int[3];
		Span<int> second = stackalloc int[3];
		if (differential)
		{
			for (int channel = 0; channel < 3; channel++)
			{
				int shift = 27 - channel * 8;
				int base5 = (int)(high >> shift) & 31;
				int delta = (int)(high >> (shift - 3)) & 7;
				if (delta >= 4)
					delta -= 8;
				first[channel] = Expand5(base5);
				second[channel] = Expand5((base5 + delta) & 31);
			}
		}
		else
		{
			for (int channel = 0; channel < 3; channel++)
			{
				int shift = 28 - channel * 8;
				first[channel] = (int)((high >> shift) & 15) * 17;
				second[channel] = (int)((high >> (shift - 4)) & 15) * 17;
			}
		}

		for (int row = 0; row < 4; row++)
		{
			for (int column = 0; column < 4; column++)
			{
				int bit = column * 4 + row;
				int selector = (int)((low >> bit) & 1) | (int)((low >> (bit + 16)) & 1) << 1;
				int subBlock = SubBlockOf(flip, column, row);
				int table = subBlock == 0 ? firstTable : secondTable;
				int modifier = selector switch
				{
					0 => Modifiers[table, 0],
					1 => Modifiers[table, 1],
					2 => -Modifiers[table, 0],
					_ => -Modifiers[table, 1]
				};
				Span<int> baseColor = subBlock == 0 ? first : second;
				for (int channel = 0; channel < 3; channel++)
					rgb[(row * 4 + column) * 3 + channel] = (byte)Math.Clamp(baseColor[channel] + modifier, 0, 255);
			}
		}
	}
}
