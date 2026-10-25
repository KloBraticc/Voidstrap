using System.Buffers.Binary;
using System.Text;

namespace Voidstrap.Platform.MacOS;

public static class MacOSBinaryCookies
{
	public static string RobloxPlayerStore => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "HTTPStorages", "com.roblox.RobloxPlayer.binarycookies");

	public static string? Find(string path, string name, string domainSuffix)
	{
		if (!File.Exists(path))
			return null;
		byte[] data;
		using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			if (stream.Length is < 8 or > 32 * 1024 * 1024)
				return null;
			data = new byte[stream.Length];
			stream.ReadExactly(data);
		}
		return Find(data, name, domainSuffix);
	}

	public static string? Find(ReadOnlySpan<byte> data, string name, string domainSuffix)
	{
		if (data.Length < 8 || !data[..4].SequenceEqual("cook"u8))
			return null;
		int pages = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
		if (pages < 0 || 8 + (long)pages * 4 > data.Length)
			return null;
		int pageStart = 8 + pages * 4;
		string? best = null;
		double bestExpiry = double.MinValue;
		for (int page = 0; page < pages; page++)
		{
			int size = BinaryPrimitives.ReadInt32BigEndian(data[(8 + page * 4)..]);
			if (size < 8 || pageStart + (long)size > data.Length)
				return best;
			ReadOnlySpan<byte> pageData = data.Slice(pageStart, size);
			pageStart += size;
			int count = BinaryPrimitives.ReadInt32LittleEndian(pageData[4..]);
			if (count < 0 || 8 + (long)count * 4 > pageData.Length)
				continue;
			for (int index = 0; index < count; index++)
			{
				int offset = BinaryPrimitives.ReadInt32LittleEndian(pageData[(8 + index * 4)..]);
				if (offset < 0 || (long)offset + 56 > pageData.Length)
					continue;
				ReadOnlySpan<byte> cookie = pageData[offset..];
				int length = BinaryPrimitives.ReadInt32LittleEndian(cookie);
				if (length < 56 || length > cookie.Length)
					continue;
				cookie = cookie[..length];
				string? domain = ReadString(cookie, BinaryPrimitives.ReadInt32LittleEndian(cookie[16..]));
				string? cookieName = ReadString(cookie, BinaryPrimitives.ReadInt32LittleEndian(cookie[20..]));
				string? host = domain?.TrimStart('.');
				if (!string.Equals(cookieName, name, StringComparison.Ordinal) || host is null
					|| !(host.Equals(domainSuffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domainSuffix, StringComparison.OrdinalIgnoreCase)))
					continue;
				string? value = ReadString(cookie, BinaryPrimitives.ReadInt32LittleEndian(cookie[28..]));
				double expiry = BinaryPrimitives.ReadDoubleLittleEndian(cookie[40..]);
				if (!string.IsNullOrEmpty(value) && double.IsFinite(expiry)
					&& expiry > (DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds && expiry > bestExpiry)
				{
					best = value;
					bestExpiry = expiry;
				}
			}
		}
		return best;
	}

	public static bool WriteWithCookie(string? templatePath, string outputPath, string name, string domain, string value)
	{
		List<byte[]> records = [];
		byte[] trailer = [0x07, 0x17, 0x20, 0x05, 0x00, 0x00, 0x00, 0x4B];
		if (!string.IsNullOrEmpty(templatePath) && File.Exists(templatePath))
		{
			byte[] template = File.ReadAllBytes(templatePath);
			if (!ReadRecords(template, name, domain, records, out byte[]? rest))
				records.Clear();
			else if (rest is { Length: >= 8 })
				trailer = rest;
		}
		double now = (DateTime.UtcNow - new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
		records.Add(BuildRecord("." + domain.TrimStart('.'), name, "/", value, now + 365 * 86400.0, now));

		using MemoryStream page = new();
		page.Write([0x00, 0x00, 0x01, 0x00]);
		WriteInt32Le(page, records.Count);
		int offset = 4 + 4 + records.Count * 4 + 4;
		foreach (byte[] record in records)
		{
			WriteInt32Le(page, offset);
			offset += record.Length;
		}
		page.Write([0x00, 0x00, 0x00, 0x00]);
		foreach (byte[] record in records)
			page.Write(record);
		byte[] pageBytes = page.ToArray();

		int checksum = 0;
		for (int i = 0; i < pageBytes.Length; i += 4)
			checksum += pageBytes[i];

		using MemoryStream file = new();
		file.Write("cook"u8);
		WriteInt32Be(file, 1);
		WriteInt32Be(file, pageBytes.Length);
		file.Write(pageBytes);
		WriteInt32Be(file, checksum);
		file.Write(trailer);
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
		File.WriteAllBytes(outputPath, file.ToArray());
		return string.Equals(Find(outputPath, name, domain.TrimStart('.')), value, StringComparison.Ordinal);
	}

	private static bool ReadRecords(byte[] data, string dropName, string dropDomain, List<byte[]> records, out byte[]? rest)
	{
		rest = null;
		if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual("cook"u8))
			return false;
		int pages = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
		if (pages < 0 || 8 + (long)pages * 4 > data.Length)
			return false;
		int pageStart = 8 + pages * 4;
		for (int page = 0; page < pages; page++)
		{
			int size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(8 + page * 4));
			if (size < 8 || pageStart + (long)size > data.Length)
				return false;
			ReadOnlySpan<byte> pageData = data.AsSpan(pageStart, size);
			pageStart += size;
			int count = BinaryPrimitives.ReadInt32LittleEndian(pageData[4..]);
			if (count < 0 || 8 + (long)count * 4 > pageData.Length)
				return false;
			for (int index = 0; index < count; index++)
			{
				int offset = BinaryPrimitives.ReadInt32LittleEndian(pageData[(8 + index * 4)..]);
				if (offset < 0 || (long)offset + 56 > pageData.Length)
					continue;
				int length = BinaryPrimitives.ReadInt32LittleEndian(pageData[offset..]);
				if (length < 56 || (long)offset + length > pageData.Length)
					continue;
				ReadOnlySpan<byte> cookie = pageData.Slice(offset, length);
				string? cookieName = ReadString(cookie, BinaryPrimitives.ReadInt32LittleEndian(cookie[20..]));
				string? host = ReadString(cookie, BinaryPrimitives.ReadInt32LittleEndian(cookie[16..]))?.TrimStart('.');
				string suffix = dropDomain.TrimStart('.');
				bool matches = string.Equals(cookieName, dropName, StringComparison.Ordinal) && host != null
					&& (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
				if (!matches)
					records.Add(cookie.ToArray());
			}
		}
		if (pageStart + 4 < data.Length)
			rest = data[(pageStart + 4)..];
		return true;
	}

	private static byte[] BuildRecord(string domain, string name, string path, string value, double expiry, double creation)
	{
		byte[][] strings = [Encoding.UTF8.GetBytes(domain), Encoding.UTF8.GetBytes(name), Encoding.UTF8.GetBytes(path), Encoding.UTF8.GetBytes(value)];
		int length = 56 + strings.Sum(s => s.Length + 1);
		byte[] record = new byte[length];
		Span<byte> span = record;
		BinaryPrimitives.WriteInt32LittleEndian(span, length);
		BinaryPrimitives.WriteInt32LittleEndian(span[8..], 5);
		int position = 56;
		for (int i = 0; i < strings.Length; i++)
		{
			BinaryPrimitives.WriteInt32LittleEndian(span[(16 + i * 4)..], position);
			strings[i].CopyTo(span[position..]);
			position += strings[i].Length + 1;
		}
		BinaryPrimitives.WriteDoubleLittleEndian(span[40..], expiry);
		BinaryPrimitives.WriteDoubleLittleEndian(span[48..], creation);
		return record;
	}

	private static void WriteInt32Le(Stream stream, int value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteInt32Be(Stream stream, int value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static string? ReadString(ReadOnlySpan<byte> cookie, int offset)
	{
		if (offset <= 0 || offset >= cookie.Length)
			return null;
		ReadOnlySpan<byte> tail = cookie[offset..];
		int end = tail.IndexOf((byte)0);
		return Encoding.UTF8.GetString(end < 0 ? tail : tail[..end]);
	}
}
