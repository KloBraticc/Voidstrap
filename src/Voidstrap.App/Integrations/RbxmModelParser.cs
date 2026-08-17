using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Voidstrap.Integrations.Animation;
using ZstdSharp;

namespace Voidstrap.Integrations;

public static class RbxmModelParser
{
	private const int MaxChunkBytes = 64 * 1024 * 1024;

	private const int MaxTotalInstances = 50000;

	private static readonly byte[] Magic = new byte[8] { 60, 114, 111, 98, 108, 111, 120, 33 };

	public static List<ModelPart> Parse(byte[] data)
	{
		List<ModelPart> list = new List<ModelPart>();
		try
		{
			if (data == null || data.Length < 32)
			{
				return list;
			}
			for (int i = 0; i < Magic.Length; i++)
			{
				if (data[i] != Magic[i])
				{
					return list;
				}
			}
			int pos = 16;
			ReadU32(data, ref pos);
			ReadU32(data, ref pos);
			pos += 8;
			Dictionary<int, int[]> classRefs = new Dictionary<int, int[]>();
			Dictionary<int, ModelPart> builders = new Dictionary<int, ModelPart>();
			int totalInstances = 0;
			while (pos + 16 <= data.Length)
			{
				string text = Encoding.ASCII.GetString(data, pos, 4);
				pos += 4;
				uint num = ReadU32(data, ref pos);
				uint num2 = ReadU32(data, ref pos);
				pos += 4;
				uint num3 = (num != 0) ? num : num2;
				if (num3 > int.MaxValue || num3 > data.Length - pos)
				{
					break;
				}
				byte[] array2 = DecodeChunk(data, pos, num, num2);
				pos += (int)num3;
				if (array2.Length == 0)
				{
					if (text == "END\0")
					{
						break;
					}
					continue;
				}
				switch (text)
				{
				case "INST":
					HandleInst(array2, classRefs, ref totalInstances);
					continue;
				case "PROP":
					HandleProp(array2, classRefs, builders, Get);
					continue;
				default:
					continue;
				case "END\0":
					break;
				}
				break;
			}
			foreach (KeyValuePair<int, ModelPart> item in builders)
			{
				ModelPart value = item.Value;
				if (value.HasSize && value.HasCFrame)
				{
					list.Add(value);
				}
			}
			ModelPart Get(int referent)
			{
				if (!builders.TryGetValue(referent, out ModelPart value2))
				{
					value2 = new ModelPart();
					builders[referent] = value2;
				}
				return value2;
			}
		}
		catch
		{
		}
		return list;
	}

	public static void ExtractAssetIds(byte[] data, Regex idRegex, Action<string> onId)
	{
		try
		{
			if (data == null || data.Length < 16 || idRegex == null || onId == null)
			{
				return;
			}
			bool flag = data.Length >= Magic.Length;
			if (flag)
			{
				for (int i = 0; i < Magic.Length; i++)
				{
					if (data[i] != Magic[i])
					{
						flag = false;
						break;
					}
				}
			}
			if (!flag)
			{
				if (data.Length > MaxChunkBytes)
				{
					return;
				}
				string input = Encoding.Latin1.GetString(data);
				{
					foreach (Match item in idRegex.Matches(input))
					{
						onId(item.Groups[1].Value);
					}
					return;
				}
			}
			int pos = 16;
			ReadU32(data, ref pos);
			ReadU32(data, ref pos);
			pos += 8;
			while (pos + 16 <= data.Length)
			{
				string text = Encoding.ASCII.GetString(data, pos, 4);
				pos += 4;
				uint num = ReadU32(data, ref pos);
				uint num2 = ReadU32(data, ref pos);
				pos += 4;
				uint num3 = (num != 0) ? num : num2;
				if (num3 > int.MaxValue || num3 > data.Length - pos)
				{
					break;
				}
				byte[] array2 = DecodeChunk(data, pos, num, num2);
				pos += (int)num3;
				if (array2.Length != 0)
				{
					string input2 = Encoding.Latin1.GetString(array2);
					foreach (Match item2 in idRegex.Matches(input2))
					{
						onId(item2.Groups[1].Value);
					}
				}
				if (text == "END\0")
				{
					break;
				}
			}
		}
		catch
		{
		}
	}

	private static byte[] DecodeChunk(byte[] data, int pos, uint compressedLength, uint uncompressedLength)
	{
		if (uncompressedLength == 0 || uncompressedLength > MaxChunkBytes)
		{
			return Array.Empty<byte>();
		}
		if (compressedLength == 0)
		{
			byte[] result = new byte[(int)uncompressedLength];
			Array.Copy(data, pos, result, 0, result.Length);
			return result;
		}
		try
		{
			if (compressedLength >= 4 && data[pos] == 40 && data[pos + 1] == 181 && data[pos + 2] == 47 && data[pos + 3] == 253)
			{
				using Decompressor decompressor = new Decompressor();
				byte[] result = new byte[(int)uncompressedLength];
				int written = decompressor.Unwrap(new ReadOnlySpan<byte>(data, pos, (int)compressedLength), result);
				if (written <= 0 || written > result.Length)
				{
					return Array.Empty<byte>();
				}
				if (written == result.Length)
				{
					return result;
				}
				byte[] trimmed = new byte[written];
				Array.Copy(result, trimmed, written);
				return trimmed;
			}
			return Lz4Block.Decompress(data, pos, (int)compressedLength, (int)uncompressedLength);
		}
		catch
		{
			return Array.Empty<byte>();
		}
	}

	private static void HandleInst(byte[] p, Dictionary<int, int[]> classRefs, ref int totalInstances)
	{
		int pos = 0;
		int key = (int)ReadU32(p, ref pos);
		ReadString(p, ref pos);
		byte b = p[pos++];
		int num = (int)ReadU32(p, ref pos);
		int previousCount = classRefs.TryGetValue(key, out int[]? previous) ? previous.Length : 0;
		long nextTotal = (long)totalInstances - previousCount + num;
		long required = (long)num * 4 + ((b == 1) ? num : 0);
		if (num >= 0 && nextTotal <= MaxTotalInstances && required <= p.Length - pos)
		{
			int[] value = ReadReferentArray(p, ref pos, num);
			classRefs[key] = value;
			totalInstances = (int)nextTotal;
			if (b == 1)
			{
				pos += num;
			}
		}
	}

	private static void HandleProp(byte[] p, Dictionary<int, int[]> classRefs, Dictionary<int, ModelPart> builders, Func<int, ModelPart> get)
	{
		int pos = 0;
		int key = (int)ReadU32(p, ref pos);
		string text = ReadString(p, ref pos);
		if (pos >= p.Length)
		{
			return;
		}
		byte b = p[pos++];
		if (!classRefs.TryGetValue(key, out int[] value))
		{
			return;
		}
		int num = value.Length;
		if ((text == "size" && b == 14) || (text == "Size" && b == 14))
		{
			float[] array = ReadInterleavedFloats(p, ref pos, num);
			float[] array2 = ReadInterleavedFloats(p, ref pos, num);
			float[] array3 = ReadInterleavedFloats(p, ref pos, num);
			for (int i = 0; i < num; i++)
			{
				ModelPart modelPart = get(value[i]);
				modelPart.SizeX = array[i];
				modelPart.SizeY = array2[i];
				modelPart.SizeZ = array3[i];
				modelPart.HasSize = true;
			}
		}
		else if (text == "CFrame" && b == 16)
		{
			RobloxCFrame[] array4 = ReadCFrames(p, ref pos, num);
			for (int j = 0; j < num; j++)
			{
				ModelPart modelPart2 = get(value[j]);
				modelPart2.CFrame = array4[j];
				modelPart2.HasCFrame = true;
			}
		}
		else if ((text == "MeshId" || text == "MeshID") && b == 1)
		{
			for (int k = 0; k < num; k++)
			{
				string text2 = ReadString(p, ref pos);
				if (text2.Length > 0)
				{
					get(value[k]).MeshId = text2;
				}
			}
		}
		else if (text == "Color3uint8" && b == 26 && pos + num * 3 <= p.Length)
		{
			for (int l = 0; l < num; l++)
			{
				get(value[l]).R = p[pos + l];
			}
			pos += num;
			for (int m = 0; m < num; m++)
			{
				get(value[m]).G = p[pos + m];
			}
			pos += num;
			for (int n = 0; n < num; n++)
			{
				get(value[n]).B = p[pos + n];
			}
			pos += num;
		}
	}

	private static uint ReadU32(byte[] p, ref int pos)
	{
		int result = p[pos] | (p[pos + 1] << 8) | (p[pos + 2] << 16) | (p[pos + 3] << 24);
		pos += 4;
		return (uint)result;
	}

	private static string ReadString(byte[] p, ref int pos)
	{
		int num = (int)ReadU32(p, ref pos);
		if (num < 0 || pos + num > p.Length)
		{
			pos = p.Length;
			return "";
		}
		string result = Encoding.UTF8.GetString(p, pos, num);
		pos += num;
		return result;
	}

	private static uint[] ReadInterleavedU32(byte[] p, ref int pos, int n)
	{
		uint[] array = new uint[n];
		for (int i = 0; i < 4; i++)
		{
			for (int j = 0; j < n; j++)
			{
				array[j] = (array[j] << 8) | p[pos + i * n + j];
			}
		}
		pos += 4 * n;
		return array;
	}

	private static float[] ReadInterleavedFloats(byte[] p, ref int pos, int n)
	{
		uint[] array = ReadInterleavedU32(p, ref pos, n);
		float[] array2 = new float[n];
		for (int i = 0; i < n; i++)
		{
			uint num = array[i];
			uint value = (num >> 1) | (num << 31);
			array2[i] = BitConverter.Int32BitsToSingle((int)value);
		}
		return array2;
	}

	private static int[] ReadReferentArray(byte[] p, ref int pos, int n)
	{
		uint[] array = ReadInterleavedU32(p, ref pos, n);
		int[] array2 = new int[n];
		int num = 0;
		for (int i = 0; i < n; i++)
		{
			int num2 = (int)((array[i] >> 1) ^ (0 - (array[i] & 1)));
			num = (array2[i] = num + num2);
		}
		return array2;
	}

	private static RobloxCFrame[] ReadCFrames(byte[] p, ref int pos, int n)
	{
		double[][] array = new double[n][];
		for (int i = 0; i < n; i++)
		{
			byte b = p[pos++];
			if (b == 0)
			{
				double[] array2 = new double[9];
				for (int j = 0; j < 9; j++)
				{
					array2[j] = BitConverter.ToSingle(p, pos);
					pos += 4;
				}
				array[i] = array2;
			}
			else
			{
				array[i] = RobloxCFrame.SpecialRotation(b);
			}
		}
		float[] array3 = ReadInterleavedFloats(p, ref pos, n);
		float[] array4 = ReadInterleavedFloats(p, ref pos, n);
		float[] array5 = ReadInterleavedFloats(p, ref pos, n);
		RobloxCFrame[] array6 = new RobloxCFrame[n];
		for (int k = 0; k < n; k++)
		{
			double[] array7 = array[k];
			array6[k] = new RobloxCFrame(array3[k], array4[k], array5[k], array7[0], array7[1], array7[2], array7[3], array7[4], array7[5], array7[6], array7[7], array7[8]);
		}
		return array6;
	}
}
