using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace Voidstrap.Utility;

internal static class ColorFont
{
	private const uint ChecksumMagic = 0xB1B0AFBA;

	private readonly record struct Pt(double X, double Y);

	private readonly record struct Seg(Pt A, Pt C, Pt B, bool Quad);

	private sealed class Outline
	{
		public short XMin;

		public short YMin;

		public short XMax;

		public short YMax;

		public List<List<Seg>> Contours = [];
	}

	public static byte[] Colorize(byte[] font, Color color)
	{
		return Colorize(font, [color], 1.0, 0.0);
	}

	public static byte[] Colorize(byte[] font, IReadOnlyList<Color> bands, double dx, double dy)
	{
		if (bands.Count == 0)
		{
			throw new ArgumentException("At least one colour is needed", nameof(bands));
		}
		(uint version, Dictionary<string, byte[]> tables) = ReadTables(font);
		if (!tables.TryGetValue("maxp", out byte[]? maxp) || maxp.Length < 6)
		{
			throw new InvalidDataException("The font has no glyph count");
		}
		int glyphCount = BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(4));
		List<ushort> glyphs = DrawnGlyphs(tables, glyphCount);
		if (glyphs.Count == 0)
		{
			throw new InvalidDataException("The font has no drawable glyphs");
		}
		List<List<(ushort Glyph, ushort Palette)>> layers = [.. glyphs.Select(glyph => new List<(ushort Glyph, ushort Palette)> { (glyph, 0) })];
		if (bands.Count > 1 && glyphCount + (long)glyphs.Count * (bands.Count - 1) <= ushort.MaxValue && tables.ContainsKey("glyf") && tables.ContainsKey("loca") && tables.ContainsKey("hmtx") && tables.ContainsKey("hhea"))
		{
			AddGradientBands(tables, glyphCount, glyphs, layers, bands.Count, dx, dy);
		}
		tables["COLR"] = BuildColr(glyphs, layers);
		tables["CPAL"] = BuildCpal(bands);
		return WriteTables(version, tables);
	}

	private static void AddGradientBands(Dictionary<string, byte[]> tables, int glyphCount, List<ushort> glyphs, List<List<(ushort Glyph, ushort Palette)>> layers, int bandCount, double dx, double dy)
	{
		byte[] head = tables["head"];
		byte[] loca = tables["loca"];
		byte[] glyf = tables["glyf"];
		bool longOffsets = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) == 1;
		uint[] offsets = new uint[glyphCount + 1];
		for (int glyph = 0; glyph <= glyphCount; glyph++)
		{
			offsets[glyph] = longOffsets ? BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph * 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(glyph * 2)) * 2u;
		}
		(ushort[] advances, short[] bearings) = ReadMetrics(tables, glyphCount);

		double lowest = Math.Min(0, dx) + Math.Min(0, dy);
		double range = Math.Max(0, dx) + Math.Max(0, dy) - lowest;
		if (range <= 0)
		{
			range = 1;
		}

		using MemoryStream extra = new();
		List<uint> extraOffsets = [];
		List<(ushort Advance, short Bearing)> extraMetrics = [];
		int maxPoints = 0;
		int maxContours = 0;
		uint baseLength = (uint)Pad(glyf.Length);
		for (int i = 0; i < glyphs.Count; i++)
		{
			ushort glyph = glyphs[i];
			Outline? outline = ReadOutline(glyf.AsSpan((int)offsets[glyph], (int)(offsets[glyph + 1] - offsets[glyph])));
			if (outline == null)
			{
				continue;
			}
			double width = Math.Max(1, outline.XMax - outline.XMin);
			double height = Math.Max(1, outline.YMax - outline.YMin);
			for (int band = 1; band < bandCount; band++)
			{
				double threshold = (double)band / bandCount;
				double Side(Pt point) => ((point.X - outline.XMin) / width * dx + (outline.YMax - point.Y) / height * dy - lowest) / range - threshold;
				List<List<Seg>> clipped = [.. outline.Contours.Select(contour => Clip(contour, Side)).OfType<List<Seg>>()];
				byte[]? encoded = EncodeGlyph(clipped, out int points, out int contours, out short xMin);
				if (encoded == null)
				{
					continue;
				}
				ushort id = (ushort)(glyphCount + extraOffsets.Count);
				extraOffsets.Add(baseLength + (uint)extra.Length);
				extra.Write(encoded);
				extraMetrics.Add((advances[glyph], xMin));
				maxPoints = Math.Max(maxPoints, points);
				maxContours = Math.Max(maxContours, contours);
				layers[i].Add((id, (ushort)band));
			}
		}
		if (extraOffsets.Count == 0)
		{
			return;
		}

		int total = glyphCount + extraOffsets.Count;
		byte[] newGlyf = new byte[baseLength + extra.Length];
		glyf.CopyTo(newGlyf, 0);
		extra.ToArray().CopyTo(newGlyf, baseLength);
		byte[] newLoca = new byte[(total + 1) * 4];
		for (int glyph = 0; glyph < glyphCount; glyph++)
		{
			BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(glyph * 4), offsets[glyph]);
		}
		for (int j = 0; j < extraOffsets.Count; j++)
		{
			BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan((glyphCount + j) * 4), extraOffsets[j]);
		}
		BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(total * 4), (uint)newGlyf.Length);
		tables["glyf"] = newGlyf;
		tables["loca"] = newLoca;

		head = (byte[])head.Clone();
		BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
		tables["head"] = head;

		byte[] maxp = (byte[])tables["maxp"].Clone();
		BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)total);
		if (maxp.Length >= 10 && BinaryPrimitives.ReadUInt32BigEndian(maxp) == 0x00010000)
		{
			BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(6), (ushort)Math.Max(BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(6)), maxPoints));
			BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(8), (ushort)Math.Max(BinaryPrimitives.ReadUInt16BigEndian(maxp.AsSpan(8)), maxContours));
		}
		tables["maxp"] = maxp;

		byte[] hmtx = new byte[total * 4];
		for (int glyph = 0; glyph < total; glyph++)
		{
			(ushort advance, short bearing) = glyph < glyphCount ? (advances[glyph], bearings[glyph]) : extraMetrics[glyph - glyphCount];
			BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(glyph * 4), advance);
			BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(glyph * 4 + 2), bearing);
		}
		tables["hmtx"] = hmtx;
		byte[] hhea = (byte[])tables["hhea"].Clone();
		BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)total);
		tables["hhea"] = hhea;

		if (tables.TryGetValue("post", out byte[]? post) && post.Length >= 34 && BinaryPrimitives.ReadUInt32BigEndian(post) == 0x00020000)
		{
			int oldCount = BinaryPrimitives.ReadUInt16BigEndian(post.AsSpan(32));
			int namesStart = 34 + oldCount * 2;
			if (oldCount == glyphCount && namesStart <= post.Length)
			{
				byte[] newPost = new byte[post.Length + extraOffsets.Count * 2];
				post.AsSpan(0, namesStart).CopyTo(newPost);
				BinaryPrimitives.WriteUInt16BigEndian(newPost.AsSpan(32), (ushort)total);
				post.AsSpan(namesStart).CopyTo(newPost.AsSpan(namesStart + extraOffsets.Count * 2));
				tables["post"] = newPost;
			}
		}
	}

	private static (ushort[] Advances, short[] Bearings) ReadMetrics(Dictionary<string, byte[]> tables, int glyphCount)
	{
		byte[] hhea = tables["hhea"];
		byte[] hmtx = tables["hmtx"];
		int longCount = Math.Clamp((int)BinaryPrimitives.ReadUInt16BigEndian(hhea.AsSpan(34)), 1, glyphCount);
		ushort[] advances = new ushort[glyphCount];
		short[] bearings = new short[glyphCount];
		for (int glyph = 0; glyph < glyphCount; glyph++)
		{
			if (glyph < longCount)
			{
				advances[glyph] = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(glyph * 4));
				bearings[glyph] = BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan(glyph * 4 + 2));
			}
			else
			{
				advances[glyph] = advances[longCount - 1];
				int at = longCount * 4 + (glyph - longCount) * 2;
				bearings[glyph] = at + 2 <= hmtx.Length ? BinaryPrimitives.ReadInt16BigEndian(hmtx.AsSpan(at)) : (short)0;
			}
		}
		return (advances, bearings);
	}

	private static Outline? ReadOutline(ReadOnlySpan<byte> data)
	{
		if (data.Length < 10)
		{
			return null;
		}
		int contourCount = BinaryPrimitives.ReadInt16BigEndian(data);
		if (contourCount <= 0)
		{
			return null;
		}
		Outline outline = new()
		{
			XMin = BinaryPrimitives.ReadInt16BigEndian(data[2..]),
			YMin = BinaryPrimitives.ReadInt16BigEndian(data[4..]),
			XMax = BinaryPrimitives.ReadInt16BigEndian(data[6..]),
			YMax = BinaryPrimitives.ReadInt16BigEndian(data[8..])
		};
		int[] ends = new int[contourCount];
		for (int i = 0; i < contourCount; i++)
		{
			ends[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(10 + i * 2)..]);
		}
		int pointCount = ends[^1] + 1;
		int at = 10 + contourCount * 2;
		at += 2 + BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
		byte[] flags = new byte[pointCount];
		for (int i = 0; i < pointCount;)
		{
			byte flag = data[at++];
			flags[i++] = flag;
			if ((flag & 8) != 0)
			{
				int repeat = data[at++];
				for (int r = 0; r < repeat && i < pointCount; r++)
				{
					flags[i++] = flag;
				}
			}
		}
		int[] xs = new int[pointCount];
		int[] ys = new int[pointCount];
		at = ReadCoordinates(data, at, flags, xs, 2, 16);
		ReadCoordinates(data, at, flags, ys, 4, 32);
		int start = 0;
		foreach (int end in ends)
		{
			List<(Pt Point, bool On)> points = [];
			for (int i = start; i <= end; i++)
			{
				points.Add((new Pt(xs[i], ys[i]), (flags[i] & 1) != 0));
			}
			start = end + 1;
			List<Seg>? contour = ToSegments(points);
			if (contour != null)
			{
				outline.Contours.Add(contour);
			}
		}
		return outline;
	}

	private static int ReadCoordinates(ReadOnlySpan<byte> data, int at, byte[] flags, int[] values, byte shortBit, byte sameBit)
	{
		int value = 0;
		for (int i = 0; i < flags.Length; i++)
		{
			byte flag = flags[i];
			if ((flag & shortBit) != 0)
			{
				int delta = data[at++];
				value += (flag & sameBit) != 0 ? delta : -delta;
			}
			else if ((flag & sameBit) == 0)
			{
				value += BinaryPrimitives.ReadInt16BigEndian(data[at..]);
				at += 2;
			}
			values[i] = value;
		}
		return at;
	}

	private static Pt Mid(Pt a, Pt b)
	{
		return new Pt((a.X + b.X) / 2, (a.Y + b.Y) / 2);
	}

	private static List<Seg>? ToSegments(List<(Pt Point, bool On)> points)
	{
		if (points.Count < 2)
		{
			return null;
		}
		int first = points.FindIndex(point => point.On);
		List<(Pt Point, bool On)> ring;
		if (first < 0)
		{
			ring = [(Mid(points[0].Point, points[1].Point), true), .. points.Skip(1), points[0]];
		}
		else
		{
			ring = [.. points.Skip(first), .. points.Take(first)];
		}
		List<Seg> segments = [];
		Pt current = ring[0].Point;
		Pt? control = null;
		for (int i = 1; i <= ring.Count; i++)
		{
			(Pt point, bool on) = i < ring.Count ? ring[i] : ring[0];
			if (on)
			{
				segments.Add(control is Pt c ? new Seg(current, c, point, true) : new Seg(current, point, point, false));
				current = point;
				control = null;
			}
			else if (control is Pt c)
			{
				Pt middle = Mid(c, point);
				segments.Add(new Seg(current, c, middle, true));
				current = middle;
				control = point;
			}
			else
			{
				control = point;
			}
		}
		return segments;
	}

	private static Pt Blossom(Seg seg, double u, double v)
	{
		if (!seg.Quad)
		{
			double t = (u + v) / 2;
			return new Pt(seg.A.X + (seg.B.X - seg.A.X) * t, seg.A.Y + (seg.B.Y - seg.A.Y) * t);
		}
		double wa = (1 - u) * (1 - v);
		double wc = (1 - u) * v + u * (1 - v);
		double wb = u * v;
		return new Pt(wa * seg.A.X + wc * seg.C.X + wb * seg.B.X, wa * seg.A.Y + wc * seg.C.Y + wb * seg.B.Y);
	}

	private static List<Seg>? Clip(List<Seg> contour, Func<Pt, double> side)
	{
		List<Seg> kept = [];
		bool whole = true;
		foreach (Seg seg in contour)
		{
			double sa = side(seg.A);
			double sb = side(seg.B);
			List<double> cuts = [0.0, 1.0];
			if (seg.Quad)
			{
				double sc = side(seg.C);
				double a = sa - 2 * sc + sb;
				double b = 2 * (sc - sa);
				if (Math.Abs(a) < 1e-12)
				{
					if (Math.Abs(b) > 1e-12)
					{
						cuts.Add(-sa / b);
					}
				}
				else
				{
					double discriminant = b * b - 4 * a * sa;
					if (discriminant >= 0)
					{
						double root = Math.Sqrt(discriminant);
						cuts.Add((-b - root) / (2 * a));
						cuts.Add((-b + root) / (2 * a));
					}
				}
			}
			else if ((sa < 0) != (sb < 0))
			{
				cuts.Add(sa / (sa - sb));
			}
			double[] ordered = [.. cuts.Where(t => t >= 0 && t <= 1).Distinct().Order()];
			for (int i = 0; i < ordered.Length - 1; i++)
			{
				double t0 = ordered[i];
				double t1 = ordered[i + 1];
				if (t1 - t0 < 1e-9)
				{
					continue;
				}
				if (side(Blossom(seg, (t0 + t1) / 2, (t0 + t1) / 2)) >= 0)
				{
					Pt end = Blossom(seg, t1, t1);
					kept.Add(new Seg(Blossom(seg, t0, t0), seg.Quad ? Blossom(seg, t0, t1) : end, end, seg.Quad));
				}
				else
				{
					whole = false;
				}
			}
		}
		if (whole)
		{
			return contour;
		}
		return kept.Count == 0 ? null : kept;
	}

	private static byte[]? EncodeGlyph(List<List<Seg>> contours, out int pointTotal, out int contourTotal, out short xMin)
	{
		pointTotal = 0;
		contourTotal = 0;
		xMin = 0;
		List<List<(int X, int Y, bool On)>> rings = [];
		foreach (List<Seg> contour in contours)
		{
			List<(int X, int Y, bool On)> ring = [];
			foreach (Seg seg in contour)
			{
				(int X, int Y, bool On) start = ((int)Math.Round(seg.A.X), (int)Math.Round(seg.A.Y), true);
				if (ring.Count == 0 || ring[^1] != start)
				{
					ring.Add(start);
				}
				if (seg.Quad)
				{
					ring.Add(((int)Math.Round(seg.C.X), (int)Math.Round(seg.C.Y), false));
				}
				ring.Add(((int)Math.Round(seg.B.X), (int)Math.Round(seg.B.Y), true));
			}
			if (ring.Count > 1 && ring[^1] == ring[0])
			{
				ring.RemoveAt(ring.Count - 1);
			}
			if (ring.Count >= 3)
			{
				rings.Add(ring);
			}
		}
		if (rings.Count == 0)
		{
			return null;
		}
		List<(int X, int Y, bool On)> all = [.. rings.SelectMany(ring => ring)];
		pointTotal = all.Count;
		contourTotal = rings.Count;
		int minX = all.Min(point => point.X);
		xMin = (short)minX;
		List<byte> output = [];
		void Write16(int value)
		{
			output.Add((byte)((value >> 8) & 0xFF));
			output.Add((byte)(value & 0xFF));
		}
		Write16(rings.Count);
		Write16(minX);
		Write16(all.Min(point => point.Y));
		Write16(all.Max(point => point.X));
		Write16(all.Max(point => point.Y));
		int last = -1;
		foreach (List<(int X, int Y, bool On)> ring in rings)
		{
			last += ring.Count;
			Write16(last);
		}
		Write16(0);
		List<byte> flags = [];
		List<byte> xBytes = [];
		List<byte> yBytes = [];
		int lastX = 0;
		int lastY = 0;
		foreach ((int x, int y, bool on) in all)
		{
			byte flag = on ? (byte)1 : (byte)0;
			flag |= EncodeDelta(x - lastX, xBytes, 2, 16);
			flag |= EncodeDelta(y - lastY, yBytes, 4, 32);
			lastX = x;
			lastY = y;
			flags.Add(flag);
		}
		output.AddRange(flags);
		output.AddRange(xBytes);
		output.AddRange(yBytes);
		while (output.Count % 4 != 0)
		{
			output.Add(0);
		}
		return [.. output];
	}

	private static byte EncodeDelta(int delta, List<byte> bytes, byte shortBit, byte sameBit)
	{
		if (delta == 0)
		{
			return sameBit;
		}
		if (Math.Abs(delta) <= 255)
		{
			bytes.Add((byte)Math.Abs(delta));
			return (byte)(shortBit | (delta > 0 ? sameBit : 0));
		}
		bytes.Add((byte)((delta >> 8) & 0xFF));
		bytes.Add((byte)(delta & 0xFF));
		return 0;
	}

	private static List<ushort> DrawnGlyphs(Dictionary<string, byte[]> tables, int glyphCount)
	{
		List<ushort> glyphs = [];
		if (tables.TryGetValue("loca", out byte[]? loca) && tables.TryGetValue("head", out byte[]? head) && head.Length >= 54)
		{
			bool longOffsets = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) == 1;
			int entrySize = longOffsets ? 4 : 2;
			if (loca.Length < (glyphCount + 1) * entrySize)
			{
				throw new InvalidDataException("The glyph location table is too short");
			}
			for (int glyph = 1; glyph < glyphCount && glyph <= ushort.MaxValue; glyph++)
			{
				uint start = longOffsets ? BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph * 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(glyph * 2)) * 2u;
				uint end = longOffsets ? BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(glyph * 4 + 4)) : BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(glyph * 2 + 2)) * 2u;
				if (end > start)
				{
					glyphs.Add((ushort)glyph);
				}
			}
			return glyphs;
		}
		for (int glyph = 1; glyph < glyphCount && glyph <= ushort.MaxValue; glyph++)
		{
			glyphs.Add((ushort)glyph);
		}
		return glyphs;
	}

	private static byte[] BuildColr(List<ushort> glyphs, List<List<(ushort Glyph, ushort Palette)>> layers)
	{
		int layerCount = layers.Sum(list => list.Count);
		int baseOffset = 14;
		int layerOffset = baseOffset + glyphs.Count * 6;
		byte[] table = new byte[layerOffset + layerCount * 4];
		Span<byte> span = table;
		BinaryPrimitives.WriteUInt16BigEndian(span, 0);
		BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)glyphs.Count);
		BinaryPrimitives.WriteUInt32BigEndian(span[4..], (uint)baseOffset);
		BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)layerOffset);
		BinaryPrimitives.WriteUInt16BigEndian(span[12..], (ushort)layerCount);
		int next = 0;
		for (int i = 0; i < glyphs.Count; i++)
		{
			Span<byte> record = span[(baseOffset + i * 6)..];
			BinaryPrimitives.WriteUInt16BigEndian(record, glyphs[i]);
			BinaryPrimitives.WriteUInt16BigEndian(record[2..], (ushort)next);
			BinaryPrimitives.WriteUInt16BigEndian(record[4..], (ushort)layers[i].Count);
			foreach ((ushort glyph, ushort palette) in layers[i])
			{
				Span<byte> layer = span[(layerOffset + next * 4)..];
				BinaryPrimitives.WriteUInt16BigEndian(layer, glyph);
				BinaryPrimitives.WriteUInt16BigEndian(layer[2..], palette);
				next++;
			}
		}
		return table;
	}

	private static byte[] BuildCpal(IReadOnlyList<Color> colors)
	{
		byte[] table = new byte[14 + colors.Count * 4];
		Span<byte> span = table;
		BinaryPrimitives.WriteUInt16BigEndian(span, 0);
		BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)colors.Count);
		BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1);
		BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)colors.Count);
		BinaryPrimitives.WriteUInt32BigEndian(span[8..], 14);
		BinaryPrimitives.WriteUInt16BigEndian(span[12..], 0);
		for (int i = 0; i < colors.Count; i++)
		{
			table[14 + i * 4] = colors[i].B;
			table[15 + i * 4] = colors[i].G;
			table[16 + i * 4] = colors[i].R;
			table[17 + i * 4] = 0xFF;
		}
		return table;
	}

	private static (uint Version, Dictionary<string, byte[]> Tables) ReadTables(byte[] font)
	{
		if (font.Length < 12)
		{
			throw new InvalidDataException("The font file is too short");
		}
		uint version = BinaryPrimitives.ReadUInt32BigEndian(font);
		if (version != 0x00010000 && version != 0x4F54544F && version != 0x74727565)
		{
			throw new InvalidDataException("Only single TrueType or OpenType fonts are supported");
		}
		int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
		if (12 + count * 16 > font.Length)
		{
			throw new InvalidDataException("The font table directory is truncated");
		}
		Dictionary<string, byte[]> tables = new(StringComparer.Ordinal);
		for (int i = 0; i < count; i++)
		{
			ReadOnlySpan<byte> record = font.AsSpan(12 + i * 16, 16);
			string tag = string.Concat(record[..4].ToArray().Select(value => (char)value));
			uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
			uint length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
			if (offset > font.Length || length > font.Length - offset)
			{
				throw new InvalidDataException("The font table " + tag + " points outside the file");
			}
			tables[tag] = font.AsSpan((int)offset, (int)length).ToArray();
		}
		return (version, tables);
	}

	private static byte[] WriteTables(uint version, Dictionary<string, byte[]> tables)
	{
		List<string> tags = [.. tables.Keys.OrderBy(tag => tag, StringComparer.Ordinal)];
		int count = tags.Count;
		int power = 1;
		int selector = 0;
		while (power * 2 <= count)
		{
			power *= 2;
			selector++;
		}
		int headerSize = 12 + count * 16;
		int total = headerSize + tags.Sum(tag => Pad(tables[tag].Length));
		byte[] output = new byte[total];
		Span<byte> span = output;
		BinaryPrimitives.WriteUInt32BigEndian(span, version);
		BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)count);
		BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)(power * 16));
		BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)selector);
		BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(count * 16 - power * 16));
		int offset = headerSize;
		int headOffset = -1;
		for (int i = 0; i < count; i++)
		{
			string tag = tags[i];
			byte[] data = tables[tag];
			if (tag == "head")
			{
				if (data.Length < 12)
				{
					throw new InvalidDataException("The font header table is too short");
				}
				data = (byte[])data.Clone();
				BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0);
				headOffset = offset;
			}
			data.CopyTo(span[offset..]);
			Span<byte> record = span[(12 + i * 16)..];
			for (int c = 0; c < 4; c++)
			{
				record[c] = (byte)tag[c];
			}
			BinaryPrimitives.WriteUInt32BigEndian(record[4..], Checksum(span.Slice(offset, Pad(data.Length))));
			BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)offset);
			BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)data.Length);
			offset += Pad(data.Length);
		}
		if (headOffset >= 0)
		{
			BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], unchecked(ChecksumMagic - Checksum(span)));
		}
		return output;
	}

	private static int Pad(int length)
	{
		return (length + 3) & ~3;
	}

	private static uint Checksum(ReadOnlySpan<byte> data)
	{
		uint sum = 0;
		int whole = data.Length & ~3;
		for (int i = 0; i < whole; i += 4)
		{
			sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
		}
		if (whole < data.Length)
		{
			Span<byte> tail = stackalloc byte[4];
			data[whole..].CopyTo(tail);
			sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(tail));
		}
		return sum;
	}
}
