using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Voidstrap.Integrations.Animation;

public static class RobloxAnimationParser
{
	private const string LOG_IDENT = "RobloxAnimationParser";

	private static readonly byte[] Magic = "<roblox!"u8.ToArray();

	private static readonly string[] R15OnlyParts =
	[
		"LowerTorso", "UpperTorso", "LeftUpperArm", "LeftLowerArm", "LeftHand", "RightUpperArm", "RightLowerArm", "RightHand", "LeftUpperLeg", "LeftLowerLeg",
		"LeftFoot", "RightUpperLeg", "RightLowerLeg", "RightFoot"
	];

	private static readonly string[] R6OnlyParts = ["Torso", "Left Arm", "Right Arm", "Left Leg", "Right Leg"];

	public static AnimationData? Parse(byte[] data)
	{
		try
		{
			return ParseInternal(data);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RobloxAnimationParser", "Parse failed: " + ex.Message);
			return null;
		}
	}

	private static AnimationData? ParseInternal(byte[] data)
	{
		if (data == null || data.Length < 32)
		{
			return null;
		}
		for (int i = 0; i < Magic.Length; i++)
		{
			if (data[i] != Magic[i])
			{
				return null;
			}
		}
		int pos = 16;
		uint num = ReadU32(data, ref pos);
		uint num2 = ReadU32(data, ref pos);
		pos += 8;
		if (num > 100000 || num2 > 5000000)
		{
			return null;
		}
		Dictionary<int, string> classNameByIndex = [];
		Dictionary<int, int[]> referentsByClass = [];
		Dictionary<int, string> classNameByReferent = [];
		Dictionary<int, double> timeByRef = [];
		Dictionary<int, string> nameByRef = [];
		Dictionary<int, RobloxCFrame> cframeByRef = [];
		Dictionary<int, int> parentByRef = [];
		while (pos + 16 <= data.Length)
		{
			string text = Encoding.ASCII.GetString(data, pos, 4);
			pos += 4;
			uint num3 = ReadU32(data, ref pos);
			uint num4 = ReadU32(data, ref pos);
			pos += 4;
			int num5 = (int)((num3 != 0) ? num3 : num4);
			if (num5 < 0 || pos + num5 > data.Length)
			{
				break;
			}
			byte[] array;
			if (num3 != 0)
			{
				if (data[pos] == 40 && data[pos + 1] == 181 && data[pos + 2] == 47 && data[pos + 3] == 253)
				{
					return null;
				}
				array = Lz4Block.Decompress(data, pos, (int)num3, (int)num4);
			}
			else
			{
				array = new byte[num4];
				Array.Copy(data, pos, array, 0, (int)num4);
			}
			pos += num5;
			switch (text)
			{
			case "INST":
				HandleInst(array, classNameByIndex, referentsByClass, classNameByReferent);
				break;
			case "PROP":
				HandleProp(array, classNameByIndex, referentsByClass, timeByRef, nameByRef, cframeByRef);
				break;
			case "PRNT":
				HandlePrnt(array, parentByRef);
				break;
			case "END\0":
				return Build(classNameByReferent, timeByRef, nameByRef, cframeByRef, parentByRef);
			}
		}
		return Build(classNameByReferent, timeByRef, nameByRef, cframeByRef, parentByRef);
	}

	private static void HandleInst(byte[] p, Dictionary<int, string> classNameByIndex, Dictionary<int, int[]> referentsByClass, Dictionary<int, string> classNameByReferent)
	{
		int pos = 0;
		int key = (int)ReadU32(p, ref pos);
		string value = ReadString(p, ref pos);
		byte b = p[pos++];
		int num = (int)ReadU32(p, ref pos);
		if (num >= 0 && num <= 5000000)
		{
			int[] array = ReadReferentArray(p, ref pos, num);
			classNameByIndex[key] = value;
			referentsByClass[key] = array;
			int[] array2 = array;
			foreach (int key2 in array2)
			{
				classNameByReferent[key2] = value;
			}
			if (b == 1)
			{
				pos += num;
			}
		}
	}

	private static void HandleProp(byte[] p, Dictionary<int, string> classNameByIndex, Dictionary<int, int[]> referentsByClass, Dictionary<int, double> timeByRef, Dictionary<int, string> nameByRef, Dictionary<int, RobloxCFrame> cframeByRef)
	{
		int pos = 0;
		int key = (int)ReadU32(p, ref pos);
		string text = ReadString(p, ref pos);
		if (pos >= p.Length)
		{
			return;
		}
		byte b = p[pos++];
		if (!classNameByIndex.TryGetValue(key, out string value) || !referentsByClass.TryGetValue(key, out int[] value2))
		{
			return;
		}
		int num = value2.Length;
		if (value == "Keyframe" && text == "Time" && b == 4)
		{
			float[] array = ReadInterleavedFloats(p, ref pos, num);
			for (int i = 0; i < num; i++)
			{
				timeByRef[value2[i]] = array[i];
			}
		}
		else if (value == "Pose" && text == "Name" && b == 1)
		{
			for (int j = 0; j < num; j++)
			{
				nameByRef[value2[j]] = ReadString(p, ref pos);
			}
		}
		else if (value == "Pose" && text == "CFrame" && b == 16)
		{
			RobloxCFrame[] array2 = ReadCFrames(p, ref pos, num);
			for (int k = 0; k < num; k++)
			{
				cframeByRef[value2[k]] = array2[k];
			}
		}
	}

	private static void HandlePrnt(byte[] p, Dictionary<int, int> parentByRef)
	{
		int num = 0;
		num++;
		int num2 = (int)ReadU32(p, ref num);
		if (num2 >= 0 && num2 <= 5000000)
		{
			int[] array = ReadReferentArray(p, ref num, num2);
			int[] array2 = ReadReferentArray(p, ref num, num2);
			for (int i = 0; i < num2; i++)
			{
				parentByRef[array[i]] = array2[i];
			}
		}
	}

	private static AnimationData? Build(Dictionary<int, string> classNameByReferent, Dictionary<int, double> timeByRef, Dictionary<int, string> nameByRef, Dictionary<int, RobloxCFrame> cframeByRef, Dictionary<int, int> parentByRef)
	{
		Dictionary<int, AnimKeyframe> dictionary = [];
		foreach (KeyValuePair<int, double> item in timeByRef)
		{
			dictionary[item.Key] = new AnimKeyframe
			{
				Time = item.Value
			};
		}
		HashSet<string> hashSet = new(StringComparer.Ordinal);
		foreach (KeyValuePair<int, RobloxCFrame> item2 in cframeByRef)
		{
			int key = item2.Key;
			if (nameByRef.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value))
			{
				int num = FindKeyframe(key);
				if (num >= 0 && dictionary.TryGetValue(num, out var value2))
				{
					value2.Poses[value] = item2.Value;
					hashSet.Add(value);
				}
			}
		}
		List<AnimKeyframe> list = [.. from k in dictionary.Values
			where k.Poses.Count > 0
			orderby k.Time
			select k];
		if (list.Count == 0)
		{
			return null;
		}
		AnimationData obj = new()
		{
			IsR15 = DetectR15(hashSet),
			Length = list[^1].Time
		};
		obj.Keyframes.AddRange(list);
		return obj;
		int FindKeyframe(int poseRef)
		{
			int key2 = poseRef;
			for (int i = 0; i < 64; i++)
			{
				if (!parentByRef.TryGetValue(key2, out var value3))
				{
					return -1;
				}
				if (classNameByReferent.TryGetValue(value3, out string value4) && value4 == "Keyframe")
				{
					return value3;
				}
				key2 = value3;
			}
			return -1;
		}
	}

	private static bool DetectR15(HashSet<string> partNames)
	{
		int num = R15OnlyParts.Count(partNames.Contains);
		int num2 = R6OnlyParts.Count(partNames.Contains);
		if (num == 0 && num2 == 0)
		{
			return false;
		}
		return num >= num2;
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

	private static float ReadFloatLE(byte[] p, ref int pos)
	{
		float result = BitConverter.ToSingle(p, pos);
		pos += 4;
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
					array2[j] = ReadFloatLE(p, ref pos);
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
