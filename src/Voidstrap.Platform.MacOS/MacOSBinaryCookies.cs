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
				if (offset < 0 || offset + 56 > pageData.Length)
					continue;
				ReadOnlySpan<byte> cookie = pageData[offset..];
				int length = BinaryPrimitives.ReadInt32LittleEndian(cookie);
				if (length < 56 || length > cookie.Length)
					continue;
				cookie = cookie[..length];
				string? domain = ReadString(cookie, BinaryPrimitives.ReadInt32LittleEndian(cookie[16..]));
				string? cookieName = ReadString(cookie, BinaryPrimitives.ReadInt32LittleEndian(cookie[20..]));
				if (!string.Equals(cookieName, name, StringComparison.Ordinal) || domain is null || !domain.TrimStart('.').EndsWith(domainSuffix, StringComparison.OrdinalIgnoreCase))
					continue;
				string? value = ReadString(cookie, BinaryPrimitives.ReadInt32LittleEndian(cookie[28..]));
				double expiry = BinaryPrimitives.ReadDoubleLittleEndian(cookie[40..]);
				if (!string.IsNullOrEmpty(value) && expiry > bestExpiry)
				{
					best = value;
					bestExpiry = expiry;
				}
			}
		}
		return best;
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
