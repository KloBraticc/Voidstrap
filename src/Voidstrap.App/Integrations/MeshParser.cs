using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Media3D;
using Openize.Drako;

namespace Voidstrap.Integrations;

public static partial class MeshParser
{
	private const int MaxCount = 500000;

	private const int MaxInputBytes = 64 * 1024 * 1024;

	private static readonly byte[] DracoMagic = new byte[5] { 68, 82, 65, 67, 79 };

	public static MeshModel Parse(byte[] data)
	{
		if (data == null || data.Length < 13 || data.Length > MaxInputBytes)
		{
			throw new InvalidDataException("File is too small to be a mesh");
		}
		int lineLen;
		string text = ReadVersionLine(data, out lineLen);
		Match match = MeshVersionPattern.Match(text);
		if (!match.Success)
		{
			throw new InvalidDataException("Not a Roblox mesh file");
		}
		int num = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
		int num2 = IndexOf(data, DracoMagic, lineLen, 256);
		if (num2 >= 0)
		{
			return ParseDraco(data, num2);
		}
		if (num != 1)
		{
			return ParseBinary(data, lineLen, num);
		}
		return ParseText(data, lineLen, text.Contains("1.00"));
	}

	private static int IndexOf(byte[] data, byte[] pattern, int start, int window)
	{
		int num = Math.Min(data.Length - pattern.Length, start + window);
		for (int i = Math.Max(0, start); i <= num; i++)
		{
			bool flag = true;
			for (int j = 0; j < pattern.Length; j++)
			{
				if (data[i + j] != pattern[j])
				{
					flag = false;
					break;
				}
			}
			if (flag)
			{
				return i;
			}
		}
		return -1;
	}

	private static MeshModel ParseDraco(byte[] data, int dracoPos)
	{
		byte[] array;
		if (dracoPos == 0)
		{
			array = data;
		}
		else
		{
			array = new byte[data.Length - dracoPos];
			Array.Copy(data, dracoPos, array, 0, array.Length);
		}
		if (!(Draco.Decode(array) is DracoMesh dracoMesh))
		{
			throw new InvalidDataException("Draco data did not contain a mesh");
		}
		PointAttribute namedAttribute = dracoMesh.GetNamedAttribute(AttributeType.Position);
		if (namedAttribute == null)
		{
			throw new InvalidDataException("Draco mesh has no position data");
		}
		MeshModel meshModel = new MeshModel();
		int numPoints = dracoMesh.NumPoints;
		int numFaces = dracoMesh.NumFaces;
		if (numPoints <= 0 || numPoints > MaxCount || numFaces <= 0 || numFaces > MaxCount)
		{
			throw new InvalidDataException("Draco mesh is too large");
		}
		float[] array2 = new float[3];
		for (int i = 0; i < numPoints; i++)
		{
			namedAttribute.GetValue(namedAttribute.MappedIndex(i), array2);
			meshModel.Positions.Add(new Point3D(array2[0], array2[1], array2[2]));
		}
		int[] array3 = new int[3];
		for (int j = 0; j < numFaces; j++)
		{
			dracoMesh.ReadFace(j, array3);
			int num = array3[0];
			int num2 = array3[1];
			int num3 = array3[2];
			if ((uint)num < (uint)numPoints && (uint)num2 < (uint)numPoints && (uint)num3 < (uint)numPoints)
			{
				meshModel.Indices.Add(num);
				meshModel.Indices.Add(num2);
				meshModel.Indices.Add(num3);
			}
		}
		if (meshModel.Positions.Count == 0 || meshModel.Indices.Count < 3)
		{
			throw new InvalidDataException("Draco mesh produced no geometry");
		}
		return meshModel;
	}

	private static string ReadVersionLine(byte[] data, out int lineLen)
	{
		int i;
		for (i = 0; i < data.Length && data[i] != 10; i++)
		{
		}
		lineLen = Math.Min(i + 1, data.Length);
		int num = i;
		if (num > 0 && data[num - 1] == 13)
		{
			num--;
		}
		return Encoding.ASCII.GetString(data, 0, num);
	}

	private static MeshModel ParseText(byte[] data, int lineLen, bool scaleHalf)
	{
		string input = Encoding.ASCII.GetString(data, lineLen, data.Length - lineLen);
		List<Point3D> list = new List<Point3D>();
		foreach (Match item in BracketGroupPattern.Matches(input))
		{
			if (list.Count >= MaxCount * 3)
			{
				throw new InvalidDataException("Mesh contains too much geometry");
			}
			string[] array = item.Groups[1].Value.Split(',');
			if (array.Length < 3)
			{
				list.Add(default(Point3D));
			}
			else
			{
				list.Add(new Point3D(ParseD(array[0]), ParseD(array[1]), ParseD(array[2])));
			}
		}
		MeshModel meshModel = new MeshModel();
		double num = (scaleHalf ? 0.5 : 1.0);
		int num2 = 0;
		for (int i = 0; i + 2 < list.Count; i += 3)
		{
			Point3D point3D = list[i];
			meshModel.Positions.Add(new Point3D(point3D.X * num, point3D.Y * num, point3D.Z * num));
			meshModel.Indices.Add(num2++);
		}
		return meshModel;
	}

	private static MeshModel ParseBinary(byte[] data, int headerStart, int major)
	{
		if (headerStart + 16 > data.Length)
		{
			throw new InvalidDataException("Mesh header is truncated");
		}
		ushort num = BitConverter.ToUInt16(data, headerStart);
		int num2 = headerStart + ((num >= 12 && num < 256) ? num : 12);
		int num3 = data[headerStart + 2];
		List<(int nv, int nf)> counts = new List<(int, int)>();
		switch (major)
		{
		case 2:
			TryRead(4, 8);
			break;
		case 3:
			TryRead(8, 12);
			TryRead(4, 8);
			break;
		default:
			TryRead(4, 8);
			TryRead(8, 12);
			break;
		}
		foreach (var (num4, num5) in counts)
		{
			if (num4 <= 0 || num4 > MaxCount || num5 <= 0 || num5 > MaxCount)
			{
				continue;
			}
			int[] obj = new int[7] { 0, 40, 36, 32, 44, 48, 56 };
			obj[0] = num3;
			int[] array = obj;
			foreach (int num6 in array)
			{
				if (num6 >= 12)
				{
					long num7 = (long)num4 * (long)num6;
					long num8 = (long)num5 * 12L;
					if (num2 >= 0 && num2 + num7 + num8 <= data.Length)
					{
						return ReadMeshBuffers(data, num2, num4, num5, num6);
					}
				}
			}
		}
		throw new InvalidDataException("Mesh header values look invalid unsupported version?");
		void TryRead(int nvOff, int nfOff)
		{
			if (headerStart + nvOff + 4 <= data.Length && headerStart + nfOff + 4 <= data.Length)
			{
				int item = (int)BitConverter.ToUInt32(data, headerStart + nvOff);
				int item2 = (int)BitConverter.ToUInt32(data, headerStart + nfOff);
				counts.Add((item, item2));
			}
		}
	}

	private static MeshModel ReadMeshBuffers(byte[] data, int vertStart, int numVerts, int numFaces, int vertSize)
	{
		MeshModel meshModel = new MeshModel();
		for (int i = 0; i < numVerts; i++)
		{
			int num = vertStart + i * vertSize;
			meshModel.Positions.Add(new Point3D(BitConverter.ToSingle(data, num), BitConverter.ToSingle(data, num + 4), BitConverter.ToSingle(data, num + 8)));
		}
		int num2 = vertStart + numVerts * vertSize;
		for (int j = 0; j < numFaces; j++)
		{
			int num3 = num2 + j * 12;
			int num4 = (int)BitConverter.ToUInt32(data, num3);
			int num5 = (int)BitConverter.ToUInt32(data, num3 + 4);
			int num6 = (int)BitConverter.ToUInt32(data, num3 + 8);
			if (num4 >= 0 && num5 >= 0 && num6 >= 0 && num4 < numVerts && num5 < numVerts && num6 < numVerts)
			{
				meshModel.Indices.Add(num4);
				meshModel.Indices.Add(num5);
				meshModel.Indices.Add(num6);
			}
		}
		return meshModel;
	}

	private static double ParseD(string s)
	{
		if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return 0.0;
		}
		return result;
	}

	public static string ToObj(MeshModel model)
	{
		if (model.Positions.Count > MaxCount || model.Indices.Count > MaxCount * 3)
		{
			throw new InvalidDataException("Mesh contains too much geometry");
		}
		StringBuilder stringBuilder = new StringBuilder(model.Positions.Count * 24 + model.Indices.Count * 10);
		stringBuilder.Append("o Mesh\n");
		foreach (Point3D position in model.Positions)
		{
			stringBuilder.Append("v ");
			stringBuilder.Append(position.X.ToString("0.######", CultureInfo.InvariantCulture));
			stringBuilder.Append(' ');
			stringBuilder.Append(position.Y.ToString("0.######", CultureInfo.InvariantCulture));
			stringBuilder.Append(' ');
			stringBuilder.Append(position.Z.ToString("0.######", CultureInfo.InvariantCulture));
			stringBuilder.Append('\n');
		}
		for (int i = 0; i + 2 < model.Indices.Count; i += 3)
		{
			stringBuilder.Append("f ");
			stringBuilder.Append(model.Indices[i] + 1);
			stringBuilder.Append(' ');
			stringBuilder.Append(model.Indices[i + 1] + 1);
			stringBuilder.Append(' ');
			stringBuilder.Append(model.Indices[i + 2] + 1);
			stringBuilder.Append('\n');
		}
		return stringBuilder.ToString();
	}

	private static double[] ObjVec(string[] parts, int count)
	{
		double[] array = new double[count];
		for (int i = 0; i < count; i++)
		{
			array[i] = ((parts.Length > i + 1) ? ParseD(parts[i + 1]) : 0.0);
		}
		return array;
	}

	private static int[] ParseFaceVert(string token, int vCount, int vtCount, int vnCount)
	{
		string[] array = token.Split('/');
		return new int[3]
		{
			ParseIdx((array.Length != 0) ? array[0] : "", vCount),
			ParseIdx((array.Length > 1) ? array[1] : "", vtCount),
			ParseIdx((array.Length > 2) ? array[2] : "", vnCount)
		};
	}

	private static int ParseIdx(string s, int count)
	{
		if (string.IsNullOrEmpty(s) || !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return -1;
		}
		if (result > 0)
		{
			return result - 1;
		}
		if (result < 0)
		{
			return count + result;
		}
		return -1;
	}

	private static void AppendBracket(StringBuilder sb, double x, double y, double z)
	{
		sb.Append('[');
		sb.Append(x.ToString("0.######", CultureInfo.InvariantCulture));
		sb.Append(',');
		sb.Append(y.ToString("0.######", CultureInfo.InvariantCulture));
		sb.Append(',');
		sb.Append(z.ToString("0.######", CultureInfo.InvariantCulture));
		sb.Append(']');
	}

    [GeneratedRegex("version\\s+(\\d+)\\.(\\d+)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex MeshVersionPattern { get; }
    [GeneratedRegex("\\[([^\\]]*)\\]")]
    private static partial Regex BracketGroupPattern { get; }
}
