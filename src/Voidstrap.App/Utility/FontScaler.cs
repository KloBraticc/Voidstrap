using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Voidstrap.Utility;

public static class FontScaler
{
	private const string LogIdent = "FontScaler";

	private const int MinUnitsPerEm = 16;

	private const int MaxUnitsPerEm = 16384;

	private sealed class Table
	{
		public string Tag = "";

		public byte[] Data = Array.Empty<byte>();
	}

	public static bool IsSupported(string path)
	{
		try
		{
			using FileStream stream = File.OpenRead(path);
			Span<byte> tag = stackalloc byte[4];
			return stream.Read(tag) == 4 && !(tag[0] == (byte)'t' && tag[1] == (byte)'t' && tag[2] == (byte)'c' && tag[3] == (byte)'f');
		}
		catch
		{
			return false;
		}
	}

	public static bool TryScale(string sourcePath, string targetPath, double scale)
	{
		try
		{
			byte[] data = File.ReadAllBytes(sourcePath);

			if (Math.Abs(scale - 1.0) < 0.0001)
			{
				File.WriteAllBytes(targetPath, data);
				return true;
			}

			List<Table>? tables = ReadTables(data);

			if (tables != null && TryScaleOutlines(tables, scale, out byte[] rebuilt))
			{
				File.WriteAllBytes(targetPath, rebuilt);
				App.Logger?.WriteLine(LogIdent, "Scaled the outlines of " + Path.GetFileName(sourcePath) + " to " + (int)Math.Round(scale * 100.0) + " percent.");
				return true;
			}

			if (ScaleEmSquare(data, scale))
			{
				File.WriteAllBytes(targetPath, data);
				App.Logger?.WriteLine(LogIdent, "Scaled the em square of " + Path.GetFileName(sourcePath) + " to " + (int)Math.Round(scale * 100.0) + " percent.");
				return true;
			}

			File.WriteAllBytes(targetPath, File.ReadAllBytes(sourcePath));
			App.Logger?.WriteLine(LogIdent, "Could not resize " + Path.GetFileName(sourcePath) + ", it was applied at its normal size.");
			return false;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Could not scale the font: " + ex.Message);

			try
			{
				File.Copy(sourcePath, targetPath, overwrite: true);
			}
			catch
			{
			}

			return false;
		}
	}

	private static List<Table>? ReadTables(byte[] data)
	{
		if (data.Length < 12)
			return null;

		if (data[0] == (byte)'t' && data[1] == (byte)'t' && data[2] == (byte)'c' && data[3] == (byte)'f')
			return null;

		int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));

		if (count <= 0 || count > 512 || 12 + (count * 16) > data.Length)
			return null;

		List<Table> tables = new List<Table>(count);

		for (int i = 0; i < count; i++)
		{
			int entry = 12 + (i * 16);
			string tag = System.Text.Encoding.ASCII.GetString(data, entry, 4);
			int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 8, 4));
			int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 12, 4));

			if (offset < 0 || length < 0 || offset + length > data.Length)
				return null;

			tables.Add(new Table { Tag = tag, Data = data.AsSpan(offset, length).ToArray() });
		}

		return tables;
	}

	private static Table? Find(List<Table> tables, string tag)
	{
		foreach (Table table in tables)
		{
			if (table.Tag == tag)
				return table;
		}

		return null;
	}

	private static bool TryScaleOutlines(List<Table> tables, double scale, out byte[] result)
	{
		result = Array.Empty<byte>();

		Table? head = Find(tables, "head");
		Table? maxp = Find(tables, "maxp");
		Table? loca = Find(tables, "loca");
		Table? glyf = Find(tables, "glyf");
		Table? hhea = Find(tables, "hhea");
		Table? hmtx = Find(tables, "hmtx");

		if (head == null || maxp == null || loca == null || glyf == null || head.Data.Length < 54 || maxp.Data.Length < 6)
			return false;

		int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(maxp.Data.AsSpan(4, 2));
		int locFormat = BinaryPrimitives.ReadInt16BigEndian(head.Data.AsSpan(50, 2));

		if (numGlyphs <= 0)
			return false;

		int[] offsets = new int[numGlyphs + 1];

		if (locFormat == 0)
		{
			if (loca.Data.Length < (numGlyphs + 1) * 2)
				return false;

			for (int i = 0; i <= numGlyphs; i++)
				offsets[i] = BinaryPrimitives.ReadUInt16BigEndian(loca.Data.AsSpan(i * 2, 2)) * 2;
		}
		else
		{
			if (loca.Data.Length < (numGlyphs + 1) * 4)
				return false;

			for (int i = 0; i <= numGlyphs; i++)
				offsets[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(loca.Data.AsSpan(i * 4, 4));
		}

		using MemoryStream glyphStream = new MemoryStream();
		int[] newOffsets = new int[numGlyphs + 1];

		int minX = int.MaxValue;
		int minY = int.MaxValue;
		int maxX = int.MinValue;
		int maxY = int.MinValue;

		for (int i = 0; i < numGlyphs; i++)
		{
			newOffsets[i] = (int)glyphStream.Length;

			int start = offsets[i];
			int length = offsets[i + 1] - start;

			if (length <= 0 || start < 0 || start + length > glyf.Data.Length)
				continue;

			byte[]? scaled = ScaleGlyph(glyf.Data.AsSpan(start, length), scale, ref minX, ref minY, ref maxX, ref maxY);

			if (scaled == null)
				return false;

			glyphStream.Write(scaled, 0, scaled.Length);

			while (glyphStream.Length % 4 != 0)
				glyphStream.WriteByte(0);
		}

		newOffsets[numGlyphs] = (int)glyphStream.Length;

		glyf.Data = glyphStream.ToArray();

		byte[] newLoca = new byte[(numGlyphs + 1) * 4];

		for (int i = 0; i <= numGlyphs; i++)
			BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(i * 4, 4), (uint)newOffsets[i]);

		loca.Data = newLoca;
		BinaryPrimitives.WriteInt16BigEndian(head.Data.AsSpan(50, 2), 1);

		if (minX <= maxX)
		{
			BinaryPrimitives.WriteInt16BigEndian(head.Data.AsSpan(36, 2), Saturate(minX));
			BinaryPrimitives.WriteInt16BigEndian(head.Data.AsSpan(38, 2), Saturate(minY));
			BinaryPrimitives.WriteInt16BigEndian(head.Data.AsSpan(40, 2), Saturate(maxX));
			BinaryPrimitives.WriteInt16BigEndian(head.Data.AsSpan(42, 2), Saturate(maxY));
		}

		if (hhea != null && hmtx != null && hhea.Data.Length >= 36)
		{
			int metricCount = BinaryPrimitives.ReadUInt16BigEndian(hhea.Data.AsSpan(34, 2));

			for (int i = 0; i < metricCount; i++)
			{
				int at = i * 4;

				if (at + 4 > hmtx.Data.Length)
					break;

				ushort advance = BinaryPrimitives.ReadUInt16BigEndian(hmtx.Data.AsSpan(at, 2));
				short bearing = BinaryPrimitives.ReadInt16BigEndian(hmtx.Data.AsSpan(at + 2, 2));

				BinaryPrimitives.WriteUInt16BigEndian(hmtx.Data.AsSpan(at, 2), (ushort)Math.Clamp((int)Math.Round(advance * scale), 0, 65535));
				BinaryPrimitives.WriteInt16BigEndian(hmtx.Data.AsSpan(at + 2, 2), Saturate((int)Math.Round(bearing * scale)));
			}
		}

		result = Rebuild(tables);
		return true;
	}

	private static short Saturate(int value)
	{
		return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
	}

	private static byte[]? ScaleGlyph(ReadOnlySpan<byte> glyph, double scale, ref int minX, ref int minY, ref int maxX, ref int maxY)
	{
		if (glyph.Length < 10)
			return null;

		int contours = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(0, 2));

		using MemoryStream output = new MemoryStream();

		int gxMin = Saturate((int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(2, 2)) * scale));
		int gyMin = Saturate((int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(4, 2)) * scale));
		int gxMax = Saturate((int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(6, 2)) * scale));
		int gyMax = Saturate((int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(8, 2)) * scale));

		minX = Math.Min(minX, gxMin);
		minY = Math.Min(minY, gyMin);
		maxX = Math.Max(maxX, gxMax);
		maxY = Math.Max(maxY, gyMax);

		WriteShort(output, (short)contours);
		WriteShort(output, (short)gxMin);
		WriteShort(output, (short)gyMin);
		WriteShort(output, (short)gxMax);
		WriteShort(output, (short)gyMax);

		if (contours >= 0)
			return ScaleSimpleGlyph(glyph, contours, scale, output);

		return ScaleCompositeGlyph(glyph, scale, output);
	}

	private static byte[]? ScaleSimpleGlyph(ReadOnlySpan<byte> glyph, int contours, double scale, MemoryStream output)
	{
		int position = 10;

		if (position + (contours * 2) + 2 > glyph.Length)
			return null;

		int points = 0;

		for (int i = 0; i < contours; i++)
		{
			int end = BinaryPrimitives.ReadUInt16BigEndian(glyph.Slice(position, 2));
			output.Write(glyph.Slice(position, 2));
			position += 2;
			points = end + 1;
		}

		int instructionLength = BinaryPrimitives.ReadUInt16BigEndian(glyph.Slice(position, 2));
		position += 2;

		if (position + instructionLength > glyph.Length)
			return null;

		position += instructionLength;
		WriteShort(output, 0);

		if (points <= 0)
			return output.ToArray();

		byte[] flags = new byte[points];
		int index = 0;

		while (index < points)
		{
			if (position >= glyph.Length)
				return null;

			byte flag = glyph[position++];
			flags[index++] = flag;

			if ((flag & 0x08) != 0)
			{
				if (position >= glyph.Length)
					return null;

				int repeat = glyph[position++];

				for (int r = 0; r < repeat && index < points; r++)
					flags[index++] = flag;
			}
		}

		int[] deltaX = new int[points];
		int[] deltaY = new int[points];

		if (!ReadDeltas(glyph, ref position, flags, deltaX, 0x02, 0x10))
			return null;

		if (!ReadDeltas(glyph, ref position, flags, deltaY, 0x04, 0x20))
			return null;

		int[] absoluteX = new int[points];
		int[] absoluteY = new int[points];
		int runningX = 0;
		int runningY = 0;

		for (int i = 0; i < points; i++)
		{
			runningX += deltaX[i];
			runningY += deltaY[i];
			absoluteX[i] = (int)Math.Round(runningX * scale);
			absoluteY[i] = (int)Math.Round(runningY * scale);
		}

		for (int i = 0; i < points; i++)
			output.WriteByte((byte)(flags[i] & 0x01));

		int previous = 0;

		for (int i = 0; i < points; i++)
		{
			WriteShort(output, Saturate(absoluteX[i] - previous));
			previous = absoluteX[i];
		}

		previous = 0;

		for (int i = 0; i < points; i++)
		{
			WriteShort(output, Saturate(absoluteY[i] - previous));
			previous = absoluteY[i];
		}

		return output.ToArray();
	}

	private static bool ReadDeltas(ReadOnlySpan<byte> glyph, ref int position, byte[] flags, int[] deltas, int shortBit, int sameBit)
	{
		for (int i = 0; i < flags.Length; i++)
		{
			byte flag = flags[i];

			if ((flag & shortBit) != 0)
			{
				if (position >= glyph.Length)
					return false;

				int value = glyph[position++];
				deltas[i] = (flag & sameBit) != 0 ? value : -value;
			}
			else if ((flag & sameBit) != 0)
			{
				deltas[i] = 0;
			}
			else
			{
				if (position + 2 > glyph.Length)
					return false;

				deltas[i] = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(position, 2));
				position += 2;
			}
		}

		return true;
	}

	private static byte[]? ScaleCompositeGlyph(ReadOnlySpan<byte> glyph, double scale, MemoryStream output)
	{
		int position = 10;

		while (true)
		{
			if (position + 4 > glyph.Length)
				return null;

			int flags = BinaryPrimitives.ReadUInt16BigEndian(glyph.Slice(position, 2));
			int glyphIndex = BinaryPrimitives.ReadUInt16BigEndian(glyph.Slice(position + 2, 2));
			position += 4;

			bool wordArgs = (flags & 0x0001) != 0;
			bool xyValues = (flags & 0x0002) != 0;

			int argument1;
			int argument2;

			if (wordArgs)
			{
				if (position + 4 > glyph.Length)
					return null;

				argument1 = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(position, 2));
				argument2 = BinaryPrimitives.ReadInt16BigEndian(glyph.Slice(position + 2, 2));
				position += 4;
			}
			else
			{
				if (position + 2 > glyph.Length)
					return null;

				argument1 = xyValues ? (sbyte)glyph[position] : glyph[position];
				argument2 = xyValues ? (sbyte)glyph[position + 1] : glyph[position + 1];
				position += 2;
			}

			if (xyValues)
			{
				argument1 = (int)Math.Round(argument1 * scale);
				argument2 = (int)Math.Round(argument2 * scale);
			}

			int outFlags = (flags | 0x0001) & ~0x0100;

			WriteShort(output, (short)outFlags);
			WriteShort(output, (short)glyphIndex);
			WriteShort(output, Saturate(argument1));
			WriteShort(output, Saturate(argument2));

			int transformBytes = 0;

			if ((flags & 0x0008) != 0)
				transformBytes = 2;
			else if ((flags & 0x0040) != 0)
				transformBytes = 4;
			else if ((flags & 0x0080) != 0)
				transformBytes = 8;

			if (transformBytes > 0)
			{
				if (position + transformBytes > glyph.Length)
					return null;

				output.Write(glyph.Slice(position, transformBytes));
				position += transformBytes;
			}

			if ((flags & 0x0020) == 0)
				break;
		}

		return output.ToArray();
	}

	private static void WriteShort(MemoryStream stream, short value)
	{
		Span<byte> buffer = stackalloc byte[2];
		BinaryPrimitives.WriteInt16BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static byte[] Rebuild(List<Table> tables)
	{
		tables.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

		int count = tables.Count;
		int directorySize = 12 + (count * 16);
		int total = directorySize;

		foreach (Table table in tables)
			total += (table.Data.Length + 3) & ~3;

		byte[] output = new byte[total];

		BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), 0x00010000u);
		BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4, 2), (ushort)count);

		int power = 1;
		int exponent = 0;

		while (power * 2 <= count)
		{
			power *= 2;
			exponent++;
		}

		BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(6, 2), (ushort)(power * 16));
		BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(8, 2), (ushort)exponent);
		BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(10, 2), (ushort)((count * 16) - (power * 16)));

		int offset = directorySize;
		int headEntry = -1;

		for (int i = 0; i < count; i++)
		{
			Table table = tables[i];
			int entry = 12 + (i * 16);

			System.Text.Encoding.ASCII.GetBytes(table.Tag).CopyTo(output.AsSpan(entry, 4));

			if (table.Tag == "head")
			{
				headEntry = i;
				BinaryPrimitives.WriteUInt32BigEndian(table.Data.AsSpan(8, 4), 0u);
			}

			table.Data.CopyTo(output.AsSpan(offset));

			BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entry + 4, 4), Checksum(output.AsSpan(offset, (table.Data.Length + 3) & ~3)));
			BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entry + 8, 4), (uint)offset);
			BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(entry + 12, 4), (uint)table.Data.Length);

			offset += (table.Data.Length + 3) & ~3;
		}

		if (headEntry >= 0)
		{
			int headOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(12 + (headEntry * 16) + 8, 4));
			uint adjustment = unchecked(0xB1B0AFBAu - Checksum(output));
			BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(headOffset + 8, 4), adjustment);
		}

		return output;
	}

	private static uint Checksum(ReadOnlySpan<byte> data)
	{
		uint sum = 0;
		int i = 0;

		for (; i + 4 <= data.Length; i += 4)
			sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i, 4)));

		if (i < data.Length)
		{
			uint tail = 0;

			for (int b = 0; b < 4; b++)
				tail = (tail << 8) | (i + b < data.Length ? data[i + b] : 0u);

			sum = unchecked(sum + tail);
		}

		return sum;
	}

	private static bool ScaleEmSquare(byte[] data, double scale)
	{
		List<Table>? tables = ReadTables(data);
		Table? head = tables == null ? null : Find(tables, "head");

		if (head == null)
			return false;

		int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2));

		for (int i = 0; i < count; i++)
		{
			int entry = 12 + (i * 16);

			if (System.Text.Encoding.ASCII.GetString(data, entry, 4) != "head")
				continue;

			int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 8, 4));

			if (offset + 20 > data.Length)
				return false;

			ushort current = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 18, 2));

			if (current == 0)
				return false;

			int scaled = Math.Clamp((int)Math.Round(current / scale), MinUnitsPerEm, MaxUnitsPerEm);
			BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 18, 2), (ushort)scaled);
			BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 8, 4), 0u);
			return true;
		}

		return false;
	}
}
