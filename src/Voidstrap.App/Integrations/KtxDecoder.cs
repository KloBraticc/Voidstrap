using System;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZstdSharp;

namespace Voidstrap.Integrations;

public static class KtxDecoder
{
	private const long MaxDecodedPixels = 16L * 1024L * 1024L;

	private const int MaxDecompressedBytes = 64 * 1024 * 1024;

	private sealed class BitReader
	{
		private readonly byte[] _d;

		private int _byte;

		private int _bit;

		public BitReader(byte[] d, int off)
		{
			_d = d;
			_byte = off;
			_bit = 0;
		}

		public int Read(int n)
		{
			int num = 0;
			for (int i = 0; i < n; i++)
			{
				if (_byte >= _d.Length)
				{
					break;
				}
				int num2 = (_d[_byte] >> _bit) & 1;
				num |= num2 << i;
				if (++_bit == 8)
				{
					_bit = 0;
					_byte++;
				}
			}
			return num;
		}
	}

	private static readonly byte[] KtxHead = new byte[5] { 171, 75, 84, 88, 32 };

	private static readonly byte[] KtxTail = new byte[5] { 187, 13, 10, 26, 10 };

	private static readonly int[] Bc7W2 = new int[4] { 0, 21, 43, 64 };

	private static readonly int[] Bc7W3 = new int[8] { 0, 9, 18, 27, 37, 46, 55, 64 };

	private static readonly int[] Bc7W4 = new int[16]
	{
		0, 4, 9, 13, 17, 21, 26, 30, 34, 38,
		43, 47, 51, 55, 60, 64
	};

	public static bool IsKtx(byte[] d, int off = 0)
	{
		return KtxVersion(d, off) != 0;
	}

	private static int KtxVersion(byte[] d, int off)
	{
		if (d == null || d.Length < off + 12)
		{
			return 0;
		}
		for (int i = 0; i < 5; i++)
		{
			if (d[off + i] != KtxHead[i])
			{
				return 0;
			}
		}
		for (int j = 0; j < 5; j++)
		{
			if (d[off + 7 + j] != KtxTail[j])
			{
				return 0;
			}
		}
		if (d[off + 5] == 49 && d[off + 6] == 49)
		{
			return 1;
		}
		if (d[off + 5] == 50 && d[off + 6] == 48)
		{
			return 2;
		}
		return 0;
	}

	public static BitmapSource? DecodeToBitmap(byte[] data, int off = 0)
	{
		DecodedImage decodedImage = Decode(data, off);
		if (decodedImage == null)
		{
			return null;
		}
		BitmapSource bitmapSource = BitmapSource.Create(decodedImage.Width, decodedImage.Height, 96.0, 96.0, PixelFormats.Bgra32, null, decodedImage.Bgra, decodedImage.Width * 4);
		if (((Freezable)bitmapSource).CanFreeze)
		{
			((Freezable)bitmapSource).Freeze();
		}
		return bitmapSource;
	}

	public static DecodedImage? Decode(byte[] data, int off = 0)
	{
		try
		{
			return KtxVersion(data, off) switch
			{
				1 => DecodeKtx1(data, off), 
				2 => DecodeKtx2(data, off), 
				_ => null, 
			};
		}
		catch
		{
			return null;
		}
	}

	private static DecodedImage? DecodeKtx1(byte[] data, int off)
	{
		int p = off + 12;
		bool swap = U32(data, ref p, swap: false) == 16909060;
		uint glType = U32(data, ref p, swap);
		U32(data, ref p, swap);
		uint glFormat = U32(data, ref p, swap);
		uint num = U32(data, ref p, swap);
		U32(data, ref p, swap);
		int num2 = (int)U32(data, ref p, swap);
		int num3 = (int)U32(data, ref p, swap);
		U32(data, ref p, swap);
		U32(data, ref p, swap);
		U32(data, ref p, swap);
		U32(data, ref p, swap);
		uint num4 = U32(data, ref p, swap);
		p += (int)num4;
		if (num2 <= 0 || num3 <= 0 || num2 > 16384 || num3 > 16384 || (long)num2 * num3 > MaxDecodedPixels)
		{
			return null;
		}
		if (p + 4 > data.Length)
		{
			return null;
		}
		U32(data, ref p, swap);
		byte[] array;
		switch (num)
		{
		case 33776u:
			array = DecodeBc1(data, p, num2, num3, dxt1Alpha: false);
			break;
		case 33777u:
			array = DecodeBc1(data, p, num2, num3, dxt1Alpha: true);
			break;
		case 33778u:
			array = DecodeBc2(data, p, num2, num3);
			break;
		case 33779u:
			array = DecodeBc3(data, p, num2, num3);
			break;
		case 36283u:
		case 36284u:
			array = DecodeBc4(data, p, num2, num3);
			break;
		case 36285u:
		case 36286u:
			array = DecodeBc5(data, p, num2, num3);
			break;
		case 36492u:
		case 36493u:
			array = DecodeBc7(data, p, num2, num3);
			break;
		default:
			array = DecodeUncompressed(data, p, num2, num3, glFormat, glType);
			break;
		}
		byte[] array2 = array;
		if (array2 != null)
		{
			return new DecodedImage
			{
				Width = num2,
				Height = num3,
				Bgra = array2
			};
		}
		return null;
	}

	private static DecodedImage? DecodeKtx2(byte[] data, int off)
	{
		int p = off + 12;
		uint num = U32(data, ref p, swap: false);
		U32(data, ref p, swap: false);
		int num2 = (int)U32(data, ref p, swap: false);
		int num3 = (int)U32(data, ref p, swap: false);
		U32(data, ref p, swap: false);
		U32(data, ref p, swap: false);
		U32(data, ref p, swap: false);
		U32(data, ref p, swap: false);
		uint scheme = U32(data, ref p, swap: false);
		p += 16;
		p += 16;
		if (num2 <= 0 || num3 <= 0 || num2 > 16384 || num3 > 16384 || (long)num2 * num3 > MaxDecodedPixels)
		{
			return null;
		}
		if (p + 24 > data.Length)
		{
			return null;
		}
		ulong num4 = U64(data, ref p);
		ulong num5 = U64(data, ref p);
		ulong uncompressedLength = U64(data, ref p);
		long num6 = (long)off + (long)num4;
		if (num5 == 0L || num6 < 0 || num6 + (long)num5 > data.Length)
		{
			return null;
		}
		byte[] array = Decompress(data, (int)num6, (int)num5, scheme, (long)uncompressedLength);
		if (array.Length == 0)
		{
			return null;
		}
		byte[] array2;
		switch (num)
		{
		case 131u:
		case 132u:
			array2 = DecodeBc1(array, 0, num2, num3, dxt1Alpha: false);
			break;
		case 133u:
		case 134u:
			array2 = DecodeBc1(array, 0, num2, num3, dxt1Alpha: true);
			break;
		case 135u:
		case 136u:
			array2 = DecodeBc2(array, 0, num2, num3);
			break;
		case 137u:
		case 138u:
			array2 = DecodeBc3(array, 0, num2, num3);
			break;
		case 139u:
		case 140u:
			array2 = DecodeBc4(array, 0, num2, num3);
			break;
		case 141u:
		case 142u:
			array2 = DecodeBc5(array, 0, num2, num3);
			break;
		case 145u:
		case 146u:
			array2 = DecodeBc7(array, 0, num2, num3);
			break;
		case 37u:
		case 43u:
			array2 = RawRgba(array, num2, num3);
			break;
		default:
			array2 = null;
			break;
		}
		byte[] array3 = array2;
		if (array3 != null)
		{
			return new DecodedImage
			{
				Width = num2,
				Height = num3,
				Bgra = array3
			};
		}
		return null;
	}

	private static byte[] Decompress(byte[] src, int offset, int length, uint scheme, long uncompressedLength)
	{
		if (scheme == 0)
		{
			if (length <= 0 || length > MaxDecompressedBytes)
			{
				return Array.Empty<byte>();
			}
			byte[] array = new byte[length];
			Array.Copy(src, offset, array, 0, length);
			return array;
		}
		if (uncompressedLength <= 0 || uncompressedLength > MaxDecompressedBytes)
		{
			return Array.Empty<byte>();
		}
		try
		{
			ReadOnlySpan<byte> src2 = new ReadOnlySpan<byte>(src, offset, length);
			switch (scheme)
			{
			case 2u:
			{
				using Decompressor decompressor = new Decompressor();
				byte[] array2 = new byte[uncompressedLength];
				int num = decompressor.Unwrap(src2, array2);
				if (num < 0 || num > array2.Length)
				{
					return Array.Empty<byte>();
				}
				if (num == array2.Length)
				{
					return array2;
				}
				byte[] array3 = new byte[num];
				Array.Copy(array2, array3, num);
				return array3;
			}
			case 3u:
			{
				using MemoryStream stream = new MemoryStream(src, offset, length, writable: false);
				using ZLibStream zLibStream = new ZLibStream(stream, CompressionMode.Decompress);
				byte[] array4 = new byte[uncompressedLength];
				int num2 = 0;
				while (num2 < array4.Length)
				{
					int num3 = zLibStream.Read(array4, num2, array4.Length - num2);
					if (num3 == 0)
					{
						break;
					}
					num2 += num3;
				}
				if (num2 == array4.Length && zLibStream.ReadByte() != -1)
				{
					return Array.Empty<byte>();
				}
				if (num2 == array4.Length)
				{
					return array4;
				}
				byte[] array5 = new byte[num2];
				Array.Copy(array4, array5, num2);
				return array5;
			}
			}
		}
		catch
		{
			return Array.Empty<byte>();
		}
		return Array.Empty<byte>();
	}

	private static byte[] RawRgba(byte[] d, int w, int h)
	{
		byte[] array = new byte[w * h * 4];
		int num = Math.Min(d.Length, w * h * 4);
		for (int i = 0; i + 3 < num; i += 4)
		{
			byte b = d[i];
			byte b2 = d[i + 1];
			byte b3 = d[i + 2];
			byte b4 = d[i + 3];
			array[i] = b3;
			array[i + 1] = b2;
			array[i + 2] = b;
			array[i + 3] = b4;
		}
		return array;
	}

	private static ulong U64(byte[] d, ref int p)
	{
		ulong num = 0uL;
		for (int i = 0; i < 8; i++)
		{
			num |= (ulong)d[p + i] << 8 * i;
		}
		p += 8;
		return num;
	}

	private static uint U32(byte[] d, ref int p, bool swap)
	{
		uint num = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
		p += 4;
		if (swap)
		{
			num = ((num & 0xFF) << 24) | ((num & 0xFF00) << 8) | ((num >> 8) & 0xFF00) | ((num >> 24) & 0xFF);
		}
		return num;
	}

	private static void From565(ushort c, out byte r, out byte g, out byte b)
	{
		int num = (c >> 11) & 0x1F;
		int num2 = (c >> 5) & 0x3F;
		int num3 = c & 0x1F;
		r = (byte)((num * 255 + 15) / 31);
		g = (byte)((num2 * 255 + 31) / 63);
		b = (byte)((num3 * 255 + 15) / 31);
	}

	private static void Put(byte[] o, int w, int h, int x, int y, byte r, byte g, byte b, byte a)
	{
		if ((uint)x < (uint)w && (uint)y < (uint)h)
		{
			int num = (y * w + x) * 4;
			o[num] = b;
			o[num + 1] = g;
			o[num + 2] = r;
			o[num + 3] = a;
		}
	}

	private static void ColorPalette(ushort c0, ushort c1, bool dxt1Alpha, byte[] pr, byte[] pg, byte[] pb, byte[] pa)
	{
		From565(c0, out var r, out var g, out var b);
		From565(c1, out var r2, out var g2, out var b2);
		pr[0] = r;
		pg[0] = g;
		pb[0] = b;
		pa[0] = byte.MaxValue;
		pr[1] = r2;
		pg[1] = g2;
		pb[1] = b2;
		pa[1] = byte.MaxValue;
		if (!dxt1Alpha || c0 > c1)
		{
			pr[2] = (byte)((2 * r + r2) / 3);
			pg[2] = (byte)((2 * g + g2) / 3);
			pb[2] = (byte)((2 * b + b2) / 3);
			pa[2] = byte.MaxValue;
			pr[3] = (byte)((r + 2 * r2) / 3);
			pg[3] = (byte)((g + 2 * g2) / 3);
			pb[3] = (byte)((b + 2 * b2) / 3);
			pa[3] = byte.MaxValue;
		}
		else
		{
			pr[2] = (byte)((r + r2) / 2);
			pg[2] = (byte)((g + g2) / 2);
			pb[2] = (byte)((b + b2) / 2);
			pa[2] = byte.MaxValue;
			pr[3] = 0;
			pg[3] = 0;
			pb[3] = 0;
			pa[3] = 0;
		}
	}

	private static void AlphaPalette(byte a0, byte a1, byte[] av)
	{
		av[0] = a0;
		av[1] = a1;
		if (a0 > a1)
		{
			for (int i = 2; i < 8; i++)
			{
				av[i] = (byte)(((8 - i) * a0 + (i - 1) * a1) / 7);
			}
			return;
		}
		for (int j = 2; j < 6; j++)
		{
			av[j] = (byte)(((6 - j) * a0 + (j - 1) * a1) / 5);
		}
		av[6] = 0;
		av[7] = byte.MaxValue;
	}

	private static byte[] DecodeBc1(byte[] d, int pos, int w, int h, bool dxt1Alpha)
	{
		byte[] array = new byte[w * h * 4];
		byte[] array2 = new byte[4];
		byte[] array3 = new byte[4];
		byte[] array4 = new byte[4];
		byte[] array5 = new byte[4];
		for (int i = 0; i < h; i += 4)
		{
			for (int j = 0; j < w; j += 4)
			{
				if (pos + 8 > d.Length)
				{
					return array;
				}
				ushort c = (ushort)(d[pos] | (d[pos + 1] << 8));
				ushort c2 = (ushort)(d[pos + 2] | (d[pos + 3] << 8));
				uint num = (uint)(d[pos + 4] | (d[pos + 5] << 8) | (d[pos + 6] << 16) | (d[pos + 7] << 24));
				pos += 8;
				ColorPalette(c, c2, dxt1Alpha, array2, array3, array4, array5);
				for (int k = 0; k < 4; k++)
				{
					for (int l = 0; l < 4; l++)
					{
						int num2 = (int)((num >> 2 * (k * 4 + l)) & 3);
						Put(array, w, h, j + l, i + k, array2[num2], array3[num2], array4[num2], array5[num2]);
					}
				}
			}
		}
		return array;
	}

	private static byte[] DecodeBc2(byte[] d, int pos, int w, int h)
	{
		byte[] array = new byte[w * h * 4];
		byte[] array2 = new byte[4];
		byte[] array3 = new byte[4];
		byte[] array4 = new byte[4];
		byte[] pa = new byte[4];
		for (int i = 0; i < h; i += 4)
		{
			for (int j = 0; j < w; j += 4)
			{
				if (pos + 16 > d.Length)
				{
					return array;
				}
				ushort c = (ushort)(d[pos + 8] | (d[pos + 9] << 8));
				ushort c2 = (ushort)(d[pos + 10] | (d[pos + 11] << 8));
				uint num = (uint)(d[pos + 12] | (d[pos + 13] << 8) | (d[pos + 14] << 16) | (d[pos + 15] << 24));
				ColorPalette(c, c2, dxt1Alpha: false, array2, array3, array4, pa);
				for (int k = 0; k < 4; k++)
				{
					for (int l = 0; l < 4; l++)
					{
						int num2 = k * 4 + l;
						int num3 = (int)((num >> 2 * num2) & 3);
						byte a = (byte)(((d[pos + (num2 >> 1)] >> (num2 & 1) * 4) & 0xF) * 17);
						Put(array, w, h, j + l, i + k, array2[num3], array3[num3], array4[num3], a);
					}
				}
				pos += 16;
			}
		}
		return array;
	}

	private static byte[] DecodeBc3(byte[] d, int pos, int w, int h)
	{
		byte[] array = new byte[w * h * 4];
		byte[] array2 = new byte[4];
		byte[] array3 = new byte[4];
		byte[] array4 = new byte[4];
		byte[] pa = new byte[4];
		byte[] array5 = new byte[8];
		for (int i = 0; i < h; i += 4)
		{
			for (int j = 0; j < w; j += 4)
			{
				if (pos + 16 > d.Length)
				{
					return array;
				}
				AlphaPalette(d[pos], d[pos + 1], array5);
				ulong num = 0uL;
				for (int k = 0; k < 6; k++)
				{
					num |= (ulong)d[pos + 2 + k] << 8 * k;
				}
				ushort c = (ushort)(d[pos + 8] | (d[pos + 9] << 8));
				ushort c2 = (ushort)(d[pos + 10] | (d[pos + 11] << 8));
				uint num2 = (uint)(d[pos + 12] | (d[pos + 13] << 8) | (d[pos + 14] << 16) | (d[pos + 15] << 24));
				ColorPalette(c, c2, dxt1Alpha: false, array2, array3, array4, pa);
				for (int l = 0; l < 4; l++)
				{
					for (int m = 0; m < 4; m++)
					{
						int num3 = l * 4 + m;
						int num4 = (int)((num2 >> 2 * num3) & 3);
						int num5 = (int)((num >> 3 * num3) & 7);
						Put(array, w, h, j + m, i + l, array2[num4], array3[num4], array4[num4], array5[num5]);
					}
				}
				pos += 16;
			}
		}
		return array;
	}

	private static byte ChannelBlock(byte[] d, int pos, int px, int py, byte[] cv)
	{
		int num = py * 4 + px;
		ulong num2 = 0uL;
		for (int i = 0; i < 6; i++)
		{
			num2 |= (ulong)d[pos + 2 + i] << 8 * i;
		}
		int num3 = (int)((num2 >> 3 * num) & 7);
		return cv[num3];
	}

	private static byte[] DecodeBc4(byte[] d, int pos, int w, int h)
	{
		byte[] array = new byte[w * h * 4];
		byte[] array2 = new byte[8];
		for (int i = 0; i < h; i += 4)
		{
			for (int j = 0; j < w; j += 4)
			{
				if (pos + 8 > d.Length)
				{
					return array;
				}
				AlphaPalette(d[pos], d[pos + 1], array2);
				for (int k = 0; k < 4; k++)
				{
					for (int l = 0; l < 4; l++)
					{
						byte b = ChannelBlock(d, pos, l, k, array2);
						Put(array, w, h, j + l, i + k, b, b, b, byte.MaxValue);
					}
				}
				pos += 8;
			}
		}
		return array;
	}

	private static byte[] DecodeBc5(byte[] d, int pos, int w, int h)
	{
		byte[] array = new byte[w * h * 4];
		byte[] array2 = new byte[8];
		byte[] array3 = new byte[8];
		for (int i = 0; i < h; i += 4)
		{
			for (int j = 0; j < w; j += 4)
			{
				if (pos + 16 > d.Length)
				{
					return array;
				}
				AlphaPalette(d[pos], d[pos + 1], array2);
				AlphaPalette(d[pos + 8], d[pos + 9], array3);
				for (int k = 0; k < 4; k++)
				{
					for (int l = 0; l < 4; l++)
					{
						byte b = ChannelBlock(d, pos, l, k, array2);
						byte b2 = ChannelBlock(d, pos + 8, l, k, array3);
						double num = (double)(int)b / 255.0 * 2.0 - 1.0;
						double num2 = (double)(int)b2 / 255.0 * 2.0 - 1.0;
						byte b3 = (byte)((Math.Sqrt(Math.Max(0.0, 1.0 - num * num - num2 * num2)) * 0.5 + 0.5) * 255.0);
						Put(array, w, h, j + l, i + k, b, b2, b3, byte.MaxValue);
					}
				}
				pos += 16;
			}
		}
		return array;
	}

	private static int Interp(int a, int b, int wgt)
	{
		return a * (64 - wgt) + b * wgt + 32 >> 6;
	}

	private static int Exp5(int v)
	{
		return (v << 3) | (v >> 2);
	}

	private static int Exp6(int v)
	{
		return (v << 2) | (v >> 4);
	}

	private static int Exp7(int v)
	{
		return (v << 1) | (v >> 6);
	}

	private static void ApplyRot(int rot, ref int r, ref int g, ref int b, ref int a)
	{
		switch (rot)
		{
		case 1:
		{
			int num = r;
			int num2 = a;
			a = num;
			r = num2;
			break;
		}
		case 2:
		{
			int num2 = g;
			int num = a;
			a = num2;
			g = num;
			break;
		}
		case 3:
		{
			int num = b;
			int num2 = a;
			a = num;
			b = num2;
			break;
		}
		}
	}

	private static byte[] DecodeBc7(byte[] d, int pos, int w, int h)
	{
		byte[] array = new byte[w * h * 4];
		for (int i = 0; i < h; i += 4)
		{
			for (int j = 0; j < w; j += 4)
			{
				if (pos + 16 > d.Length)
				{
					return array;
				}
				BitReader bitReader = new BitReader(d, pos);
				int k;
				for (k = 0; k < 8; k++)
				{
					if (bitReader.Read(1) != 0)
					{
						break;
					}
				}
				switch (k)
				{
				case 4:
					Bc7Mode4(bitReader, array, w, h, j, i);
					break;
				case 5:
					Bc7Mode5(bitReader, array, w, h, j, i);
					break;
				case 6:
					Bc7Mode6(bitReader, array, w, h, j, i);
					break;
				default:
				{
					for (int l = 0; l < 4; l++)
					{
						for (int m = 0; m < 4; m++)
						{
							Put(array, w, h, j + m, i + l, 128, 128, 128, byte.MaxValue);
						}
					}
					break;
				}
				}
				pos += 16;
			}
		}
		return array;
	}

	private static void Bc7Mode6(BitReader br, byte[] o, int w, int h, int bx, int by)
	{
		int[] array = new int[2];
		int[] array2 = new int[2];
		int[] array3 = new int[2];
		int[] array4 = new int[2];
		array[0] = br.Read(7);
		array[1] = br.Read(7);
		array2[0] = br.Read(7);
		array2[1] = br.Read(7);
		array3[0] = br.Read(7);
		array3[1] = br.Read(7);
		array4[0] = br.Read(7);
		array4[1] = br.Read(7);
		int num = br.Read(1);
		int num2 = br.Read(1);
		array[0] = (array[0] << 1) | num;
		array[1] = (array[1] << 1) | num2;
		array2[0] = (array2[0] << 1) | num;
		array2[1] = (array2[1] << 1) | num2;
		array3[0] = (array3[0] << 1) | num;
		array3[1] = (array3[1] << 1) | num2;
		array4[0] = (array4[0] << 1) | num;
		array4[1] = (array4[1] << 1) | num2;
		for (int i = 0; i < 16; i++)
		{
			int num3 = br.Read((i == 0) ? 3 : 4);
			int wgt = Bc7W4[num3];
			Put(o, w, h, bx + (i & 3), by + (i >> 2), (byte)Interp(array[0], array[1], wgt), (byte)Interp(array2[0], array2[1], wgt), (byte)Interp(array3[0], array3[1], wgt), (byte)Interp(array4[0], array4[1], wgt));
		}
	}

	private static void Bc7Mode5(BitReader br, byte[] o, int w, int h, int bx, int by)
	{
		int rot = br.Read(2);
		int[] array = new int[2];
		int[] array2 = new int[2];
		int[] array3 = new int[2];
		int[] array4 = new int[2];
		array[0] = br.Read(7);
		array[1] = br.Read(7);
		array2[0] = br.Read(7);
		array2[1] = br.Read(7);
		array3[0] = br.Read(7);
		array3[1] = br.Read(7);
		array4[0] = br.Read(8);
		array4[1] = br.Read(8);
		array[0] = Exp7(array[0]);
		array[1] = Exp7(array[1]);
		array2[0] = Exp7(array2[0]);
		array2[1] = Exp7(array2[1]);
		array3[0] = Exp7(array3[0]);
		array3[1] = Exp7(array3[1]);
		int[] array5 = new int[16];
		int[] array6 = new int[16];
		for (int i = 0; i < 16; i++)
		{
			array5[i] = br.Read((i == 0) ? 1 : 2);
		}
		for (int j = 0; j < 16; j++)
		{
			array6[j] = br.Read((j == 0) ? 1 : 2);
		}
		for (int k = 0; k < 16; k++)
		{
			int wgt = Bc7W2[array5[k]];
			int wgt2 = Bc7W2[array6[k]];
			int r = Interp(array[0], array[1], wgt);
			int g = Interp(array2[0], array2[1], wgt);
			int b = Interp(array3[0], array3[1], wgt);
			int a = Interp(array4[0], array4[1], wgt2);
			ApplyRot(rot, ref r, ref g, ref b, ref a);
			Put(o, w, h, bx + (k & 3), by + (k >> 2), (byte)r, (byte)g, (byte)b, (byte)a);
		}
	}

	private static void Bc7Mode4(BitReader br, byte[] o, int w, int h, int bx, int by)
	{
		int rot = br.Read(2);
		int num = br.Read(1);
		int[] array = new int[2];
		int[] array2 = new int[2];
		int[] array3 = new int[2];
		int[] array4 = new int[2];
		array[0] = br.Read(5);
		array[1] = br.Read(5);
		array2[0] = br.Read(5);
		array2[1] = br.Read(5);
		array3[0] = br.Read(5);
		array3[1] = br.Read(5);
		array4[0] = br.Read(6);
		array4[1] = br.Read(6);
		array[0] = Exp5(array[0]);
		array[1] = Exp5(array[1]);
		array2[0] = Exp5(array2[0]);
		array2[1] = Exp5(array2[1]);
		array3[0] = Exp5(array3[0]);
		array3[1] = Exp5(array3[1]);
		array4[0] = Exp6(array4[0]);
		array4[1] = Exp6(array4[1]);
		int[] array5 = new int[16];
		int[] array6 = new int[16];
		for (int i = 0; i < 16; i++)
		{
			array5[i] = br.Read((i == 0) ? 1 : 2);
		}
		for (int j = 0; j < 16; j++)
		{
			array6[j] = br.Read((j == 0) ? 2 : 3);
		}
		for (int k = 0; k < 16; k++)
		{
			int wgt;
			int wgt2;
			if (num == 0)
			{
				wgt = Bc7W2[array5[k]];
				wgt2 = Bc7W3[array6[k]];
			}
			else
			{
				wgt = Bc7W3[array6[k]];
				wgt2 = Bc7W2[array5[k]];
			}
			int r = Interp(array[0], array[1], wgt);
			int g = Interp(array2[0], array2[1], wgt);
			int b = Interp(array3[0], array3[1], wgt);
			int a = Interp(array4[0], array4[1], wgt2);
			ApplyRot(rot, ref r, ref g, ref b, ref a);
			Put(o, w, h, bx + (k & 3), by + (k >> 2), (byte)r, (byte)g, (byte)b, (byte)a);
		}
	}

	private static byte[]? DecodeUncompressed(byte[] d, int pos, int w, int h, uint glFormat, uint glType)
	{
		if (glType != 5121)
		{
			return null;
		}
		int num;
		switch (glFormat)
		{
		case 6408u:
		case 32993u:
			num = 4;
			break;
		case 6407u:
			num = 3;
			break;
		case 6403u:
			num = 1;
			break;
		case 33319u:
			num = 2;
			break;
		default:
			num = 0;
			break;
		}
		int num2 = num;
		if (num2 == 0)
		{
			return null;
		}
		bool flag = glFormat == 32993;
		long num3 = (long)w * (long)h * num2;
		if (pos + num3 > d.Length)
		{
			return null;
		}
		byte[] array = new byte[w * h * 4];
		int num4 = pos;
		for (int i = 0; i < h; i++)
		{
			for (int j = 0; j < w; j++)
			{
				byte a = byte.MaxValue;
				byte b4;
				byte g;
				byte r;
				if (num2 >= 3)
				{
					byte b = d[num4];
					byte b2 = d[num4 + 1];
					byte b3 = d[num4 + 2];
					if (flag)
					{
						b4 = b;
						g = b2;
						r = b3;
					}
					else
					{
						r = b;
						g = b2;
						b4 = b3;
					}
					if (num2 == 4)
					{
						a = d[num4 + 3];
					}
				}
				else if (num2 == 2)
				{
					r = d[num4];
					g = d[num4 + 1];
					b4 = 0;
				}
				else
				{
					r = (g = (b4 = d[num4]));
				}
				num4 += num2;
				Put(array, w, h, j, i, r, g, b4, a);
			}
		}
		return array;
	}
}
