using System.Text;

namespace Voidstrap.Platform.Linux;

public sealed class SteamVdfNode
{
	public List<KeyValuePair<string, object>> Entries { get; } = [];

	public object? Get(string key)
	{
		foreach (KeyValuePair<string, object> entry in Entries)
		{
			if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
				return entry.Value;
		}
		return null;
	}

	public string? GetString(string key) => Get(key) as string;

	public int? GetInt(string key) => Get(key) is int value ? value : null;

	public SteamVdfNode? GetNode(string key) => Get(key) as SteamVdfNode;

	public void Set(string key, object value)
	{
		for (int index = 0; index < Entries.Count; index++)
		{
			if (string.Equals(Entries[index].Key, key, StringComparison.OrdinalIgnoreCase))
			{
				Entries[index] = new KeyValuePair<string, object>(Entries[index].Key, value);
				return;
			}
		}
		Entries.Add(new KeyValuePair<string, object>(key, value));
	}
}

public sealed record SteamVdfRaw(byte Type, byte[] Data);

public static class SteamBinaryVdf
{
	private const byte TypeNode = 0x00;
	private const byte TypeString = 0x01;
	private const byte TypeInt32 = 0x02;
	private const byte TypeFloat = 0x03;
	private const byte TypePointer = 0x04;
	private const byte TypeWideString = 0x05;
	private const byte TypeColor = 0x06;
	private const byte TypeUInt64 = 0x07;
	private const byte TypeEnd = 0x08;
	private const byte TypeInt64 = 0x0A;
	private const byte TypeAlternateEnd = 0x0B;

	public static SteamVdfNode Read(byte[] data)
	{
		int position = 0;
		SteamVdfNode root = ReadNode(data, ref position, true);
		return root;
	}

	public static byte[] Write(SteamVdfNode root)
	{
		using MemoryStream stream = new();
		WriteNode(stream, root);
		stream.WriteByte(TypeEnd);
		return stream.ToArray();
	}

	private static SteamVdfNode ReadNode(byte[] data, ref int position, bool root)
	{
		SteamVdfNode node = new();
		while (true)
		{
			if (position >= data.Length)
			{
				if (root)
					return node;
				throw new InvalidDataException("The Steam shortcut file ended in the middle of an entry");
			}

			byte type = data[position++];
			if (type == TypeEnd || type == TypeAlternateEnd)
				return node;

			string key = ReadCString(data, ref position);
			object value = type switch
			{
				TypeNode => ReadNode(data, ref position, false),
				TypeString => ReadCString(data, ref position),
				TypeInt32 => ReadInt32(data, ref position),
				TypeFloat or TypePointer or TypeColor => new SteamVdfRaw(type, ReadBytes(data, ref position, 4)),
				TypeUInt64 or TypeInt64 => new SteamVdfRaw(type, ReadBytes(data, ref position, 8)),
				TypeWideString => new SteamVdfRaw(type, ReadWideString(data, ref position)),
				_ => throw new InvalidDataException("The Steam shortcut file has an unknown value type " + type)
			};
			node.Entries.Add(new KeyValuePair<string, object>(key, value));
		}
	}

	private static void WriteNode(Stream stream, SteamVdfNode node)
	{
		foreach (KeyValuePair<string, object> entry in node.Entries)
		{
			switch (entry.Value)
			{
				case SteamVdfNode child:
					stream.WriteByte(TypeNode);
					WriteCString(stream, entry.Key);
					WriteNode(stream, child);
					stream.WriteByte(TypeEnd);
					break;
				case string text:
					stream.WriteByte(TypeString);
					WriteCString(stream, entry.Key);
					WriteCString(stream, text);
					break;
				case int number:
					stream.WriteByte(TypeInt32);
					WriteCString(stream, entry.Key);
					stream.Write(BitConverter.GetBytes(number));
					break;
				case SteamVdfRaw raw:
					stream.WriteByte(raw.Type);
					WriteCString(stream, entry.Key);
					stream.Write(raw.Data);
					break;
				default:
					throw new InvalidDataException("Unsupported Steam shortcut value for " + entry.Key);
			}
		}
	}

	private static string ReadCString(byte[] data, ref int position)
	{
		int start = position;
		while (position < data.Length && data[position] != 0)
			position++;
		if (position >= data.Length)
			throw new InvalidDataException("The Steam shortcut file has an unterminated string");
		string value = Encoding.UTF8.GetString(data, start, position - start);
		position++;
		return value;
	}

	private static int ReadInt32(byte[] data, ref int position)
	{
		byte[] bytes = ReadBytes(data, ref position, 4);
		return BitConverter.ToInt32(bytes, 0);
	}

	private static byte[] ReadBytes(byte[] data, ref int position, int count)
	{
		if (position + count > data.Length)
			throw new InvalidDataException("The Steam shortcut file ended inside a value");
		byte[] bytes = data.AsSpan(position, count).ToArray();
		position += count;
		return bytes;
	}

	private static byte[] ReadWideString(byte[] data, ref int position)
	{
		int start = position;
		while (position + 1 < data.Length && (data[position] != 0 || data[position + 1] != 0))
			position += 2;
		if (position + 1 >= data.Length)
			throw new InvalidDataException("The Steam shortcut file has an unterminated wide string");
		position += 2;
		return data.AsSpan(start, position - start).ToArray();
	}

	private static void WriteCString(Stream stream, string value)
	{
		stream.Write(Encoding.UTF8.GetBytes(value));
		stream.WriteByte(0);
	}
}
