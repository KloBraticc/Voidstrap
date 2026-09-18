using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Voidstrap.Integrations;

public sealed record IconRect(string Name, int X, int Y, int Width, int Height);

public static class RobloxIconIndex
{
	private const string LogIdent = "RobloxIconIndex";

	private const int CacheFormat = 1;

	private static readonly string[] Bundles =
	[
		"ExtraContent/models/UniversalApp/UniversalApp.rbxm",
		"ExtraContent/models/InExperience/InExperience.rbxm"
	];

	private static readonly object CacheGate = new();

	private static string CacheRoot => Path.Combine(Paths.Base, "ModGenerator", "Index");

	private sealed class CacheFile
	{
		public int Format { get; set; }

		public Dictionary<string, List<int[]>> Sheets { get; set; } = [];

		public Dictionary<string, List<string>> Names { get; set; } = [];
	}

	public static IReadOnlyDictionary<string, List<IconRect>> Load(string versionDirectory, string versionGuid)
	{
		string cachePath = Path.Combine(CacheRoot, (string.IsNullOrWhiteSpace(versionGuid) ? "unknown" : versionGuid) + ".json");
		lock (CacheGate)
		{
			if (!string.IsNullOrWhiteSpace(versionGuid) && TryReadCache(cachePath, out Dictionary<string, List<IconRect>> cached))
			{
				return cached;
			}
			Dictionary<string, List<IconRect>> built = Build(versionDirectory);
			if (!string.IsNullOrWhiteSpace(versionGuid) && built.Count > 0)
			{
				WriteCache(cachePath, built);
			}
			return built;
		}
	}

	private static Dictionary<string, List<IconRect>> Build(string versionDirectory)
	{
		Dictionary<string, Dictionary<string, IconRect>> sheets = new(StringComparer.OrdinalIgnoreCase);
		foreach (string bundle in Bundles)
		{
			string path = Path.Combine(versionDirectory, bundle.Replace('/', Path.DirectorySeparatorChar));
			if (!File.Exists(path))
			{
				continue;
			}
			try
			{
				byte[] data = File.ReadAllBytes(path);
				foreach ((string modulePath, byte[] bytecode) in RbxmModelParser.ReadScripts(data, IsIndexModuleName))
				{
					if (modulePath.Contains("Deprecated", StringComparison.OrdinalIgnoreCase) || modulePath.Contains("__tests__", StringComparison.Ordinal))
					{
						continue;
					}
					List<(string Name, string Set, int X, int Y, int Width, int Height)>? icons = LuauAssetTables.Read(bytecode);
					if (icons == null)
					{
						App.Logger?.WriteLine(LogIdent, "Could not read the icon table in " + modulePath);
						continue;
					}
					foreach ((string name, string set, int x, int y, int width, int height) in icons)
					{
						string? sheet = ResolveSheet(versionDirectory, modulePath, set);
						if (sheet == null)
						{
							continue;
						}
						if (!sheets.TryGetValue(sheet, out Dictionary<string, IconRect>? byName))
						{
							byName = new Dictionary<string, IconRect>(StringComparer.Ordinal);
							sheets[sheet] = byName;
						}
						byName[name] = new IconRect(name, x, y, width, height);
					}
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Could not read " + bundle + ": " + ex.Message);
			}
		}
		Dictionary<string, List<IconRect>> result = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string sheet, Dictionary<string, IconRect> byName) in sheets)
		{
			result[sheet] = [.. byName.Values.OrderBy(icon => icon.Y).ThenBy(icon => icon.X)];
		}
		App.Logger?.WriteLine(LogIdent, "Read " + result.Values.Sum(list => list.Count) + " named icons across " + result.Count + " sprite sheets");
		return result;
	}

	private static bool IsIndexModuleName(string name)
	{
		return name.EndsWith("ImageSetData", StringComparison.Ordinal)
			&& !name.Contains("Deprecated", StringComparison.Ordinal)
			&& !name.StartsWith("validate", StringComparison.OrdinalIgnoreCase);
	}

	private static string? ResolveSheet(string versionDirectory, string modulePath, string set)
	{
		string file = set.Replace('\\', '/').Trim('/') + ".png";
		List<string> candidates = [];
		int core = modulePath.IndexOf("CorePackages/", StringComparison.Ordinal);
		if (core >= 0)
		{
			string[] parts = modulePath[(core + "CorePackages/".Length)..].Split('/');
			for (int keep = parts.Length - 1; keep > 0; keep--)
			{
				candidates.Add("ExtraContent/LuaPackages/" + string.Join("/", parts[..keep]) + "/SpriteSheets/" + file);
			}
		}
		candidates.Add("ExtraContent/textures/ui/ImageSet/" + file);
		candidates.Add("content/textures/ui/ImageSet/" + file);
		foreach (string candidate in candidates)
		{
			if (File.Exists(Path.Combine(versionDirectory, candidate.Replace('/', Path.DirectorySeparatorChar))))
			{
				return candidate;
			}
		}
		return null;
	}

	private static bool TryReadCache(string path, out Dictionary<string, List<IconRect>> sheets)
	{
		sheets = new Dictionary<string, List<IconRect>>(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(path))
		{
			return false;
		}
		try
		{
			CacheFile? file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
			if (file == null || file.Format != CacheFormat)
			{
				return false;
			}
			foreach ((string sheet, List<int[]> rects) in file.Sheets)
			{
				if (!file.Names.TryGetValue(sheet, out List<string>? names) || names.Count != rects.Count)
				{
					return false;
				}
				List<IconRect> icons = new(rects.Count);
				for (int i = 0; i < rects.Count; i++)
				{
					int[] rect = rects[i];
					if (rect.Length == 4)
					{
						icons.Add(new IconRect(names[i], rect[0], rect[1], rect[2], rect[3]));
					}
				}
				sheets[sheet] = icons;
			}
			return sheets.Count > 0;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The cached icon index could not be read: " + ex.Message);
			return false;
		}
	}

	private static void WriteCache(string path, Dictionary<string, List<IconRect>> sheets)
	{
		try
		{
			CacheFile file = new() { Format = CacheFormat };
			foreach ((string sheet, List<IconRect> icons) in sheets)
			{
				file.Sheets[sheet] = [.. icons.Select(icon => new[] { icon.X, icon.Y, icon.Width, icon.Height })];
				file.Names[sheet] = [.. icons.Select(icon => icon.Name)];
			}
			Directory.CreateDirectory(CacheRoot);
			foreach (string stale in Directory.EnumerateFiles(CacheRoot, "*.json"))
			{
				if (!string.Equals(stale, path, StringComparison.OrdinalIgnoreCase))
				{
					File.Delete(stale);
				}
			}
			File.WriteAllText(path, JsonSerializer.Serialize(file));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The icon index could not be cached: " + ex.Message);
		}
	}
}

internal static class LuauAssetTables
{
	private const int OpNop = 0;
	private const int OpLoadNil = 2;
	private const int OpLoadB = 3;
	private const int OpLoadN = 4;
	private const int OpLoadK = 5;
	private const int OpMove = 6;
	private const int OpSetUpval = 10;
	private const int OpGetImport = 12;
	private const int OpSetTable = 14;
	private const int OpSetTableKs = 16;
	private const int OpSetTableN = 18;
	private const int OpCall = 21;
	private const int OpReturn = 22;
	private const int OpNewTable = 53;
	private const int OpDupTable = 54;
	private const int OpPrepVarargs = 65;
	private const int OpLoadKx = 66;

	private readonly record struct Constant(int Kind, object? Value);

	private sealed record Vector(double X, double Y);

	private sealed record Import(string Path);

	private sealed class Proto
	{
		public uint[] Code = [];

		public Constant[] Constants = [];

		public string? Name;
	}

	public static List<(string Name, string Set, int X, int Y, int Width, int Height)>? Read(byte[] bytecode)
	{
		for (int prefix = 0; prefix <= 16; prefix++)
		{
			List<Proto>? protos = TryParse(bytecode, prefix);
			if (protos == null)
			{
				continue;
			}
			List<Proto> builders = [.. protos.Where(proto => proto.Name != null && proto.Name.StartsWith("make_assets", StringComparison.Ordinal))];
			if (builders.Count == 0)
			{
				builders = protos;
			}
			foreach (int multiplier in new[] { 203, 1 })
			{
				List<(string, string, int, int, int, int)> icons = [];
				bool failed = false;
				foreach (Proto proto in builders)
				{
					Dictionary<object, object?>? table = Run(proto, multiplier);
					if (table == null)
					{
						failed = true;
						break;
					}
					Collect(table, icons);
				}
				if (!failed && icons.Count > 0)
				{
					return icons;
				}
			}
		}
		return null;
	}

	private static void Collect(Dictionary<object, object?> table, List<(string, string, int, int, int, int)> icons)
	{
		foreach ((object key, object? value) in table)
		{
			if (key is not string name || value is not Dictionary<object, object?> entry)
			{
				continue;
			}
			if (entry.GetValueOrDefault("ImageSet") is string set
				&& entry.GetValueOrDefault("ImageRectOffset") is Vector offset
				&& entry.GetValueOrDefault("ImageRectSize") is Vector size
				&& size.X > 0 && size.Y > 0 && offset.X >= 0 && offset.Y >= 0)
			{
				icons.Add((name, set, (int)offset.X, (int)offset.Y, (int)size.X, (int)size.Y));
			}
		}
	}

	private static Dictionary<object, object?>? Run(Proto proto, int multiplier)
	{
		object?[] registers = new object?[256];
		Dictionary<object, object?>? result = null;
		uint[] code = proto.Code;
		Constant[] k = proto.Constants;
		int pc = 0;
		while (pc < code.Length)
		{
			uint insn = code[pc];
			int op = (int)((insn & 0xFF) * multiplier & 0xFF);
			int a = (int)(insn >> 8 & 0xFF);
			int b = (int)(insn >> 16 & 0xFF);
			int c = (int)(insn >> 24 & 0xFF);
			int d = (short)(insn >> 16);
			uint aux = pc + 1 < code.Length ? code[pc + 1] : 0;
			int step = 1;
			switch (op)
			{
				case OpNop:
				case OpPrepVarargs:
					break;
				case OpLoadNil:
					registers[a] = null;
					break;
				case OpLoadB:
					registers[a] = b != 0;
					break;
				case OpLoadN:
					registers[a] = (double)d;
					break;
				case OpLoadK:
					registers[a] = ConstantValue(k, d);
					break;
				case OpLoadKx:
					registers[a] = ConstantValue(k, (int)aux);
					step = 2;
					break;
				case OpMove:
					registers[a] = registers[b];
					break;
				case OpGetImport:
					registers[a] = ConstantValue(k, d) is Import import ? import : null;
					step = 2;
					break;
				case OpNewTable:
					registers[a] = new Dictionary<object, object?>();
					step = 2;
					break;
				case OpDupTable:
					registers[a] = Template(k, d);
					break;
				case OpCall:
					registers[a] = registers[a] is Import { Path: "Vector2.new" } && b == 3 && registers[a + 1] is double x && registers[a + 2] is double y
						? new Vector(x, y)
						: null;
					break;
				case OpSetTableKs:
					if (registers[b] is Dictionary<object, object?> keyed && ConstantValue(k, (int)aux) is string field)
					{
						keyed[field] = registers[a];
					}
					step = 2;
					break;
				case OpSetTable:
					if (registers[b] is Dictionary<object, object?> target && registers[c] is { } dynamicKey)
					{
						target[dynamicKey] = registers[a];
					}
					break;
				case OpSetTableN:
					if (registers[b] is Dictionary<object, object?> list)
					{
						list[(double)(c + 1)] = registers[a];
					}
					break;
				case OpSetUpval:
					if (registers[a] is Dictionary<object, object?> stored && (result == null || stored.Count > result.Count))
					{
						result = stored;
					}
					break;
				case OpReturn:
					if (b >= 2 && registers[a] is Dictionary<object, object?> returned && (result == null || returned.Count > result.Count))
					{
						result = returned;
					}
					return result;
				default:
					return null;
			}
			pc += step;
		}
		return result;
	}

	private static object? ConstantValue(Constant[] k, int index)
	{
		return index >= 0 && index < k.Length ? k[index].Value : null;
	}

	private static Dictionary<object, object?>? Template(Constant[] k, int index)
	{
		if (index < 0 || index >= k.Length)
		{
			return null;
		}
		Dictionary<object, object?> table = [];
		switch (k[index].Value)
		{
			case (int Key, int Value)[] pairs:
				foreach ((int key, int value) in pairs)
				{
					if (ConstantValue(k, key) is { } name)
					{
						table[name] = value >= 0 ? ConstantValue(k, value) : null;
					}
				}
				break;
			case int[] keys:
				foreach (int key in keys)
				{
					if (ConstantValue(k, key) is { } name)
					{
						table[name] = null;
					}
				}
				break;
		}
		return table;
	}

	private static List<Proto>? TryParse(byte[] data, int start)
	{
		try
		{
			int pos = start;
			int version = data[pos++];
			if (version < 3 || version > 15)
			{
				return null;
			}
			int typesVersion = version >= 4 ? data[pos++] : 0;
			int stringCount = ReadVarInt(data, ref pos);
			if (stringCount < 0 || stringCount > data.Length)
			{
				return null;
			}
			string?[] strings = new string?[stringCount + 1];
			for (int i = 1; i <= stringCount; i++)
			{
				int length = ReadVarInt(data, ref pos);
				if (length < 0 || length > data.Length - pos)
				{
					return null;
				}
				strings[i] = Encoding.UTF8.GetString(data, pos, length);
				pos += length;
			}
			if (typesVersion == 3)
			{
				while (data[pos++] != 0)
				{
					ReadVarInt(data, ref pos);
				}
			}
			int protoCount = ReadVarInt(data, ref pos);
			if (protoCount <= 0 || protoCount > 100000)
			{
				return null;
			}
			List<Proto> protos = new(protoCount);
			for (int p = 0; p < protoCount; p++)
			{
				Proto proto = new();
				pos += 4;
				if (version >= 4)
				{
					pos++;
					int typeSize = ReadVarInt(data, ref pos);
					if (typeSize < 0 || typeSize > data.Length - pos)
					{
						return null;
					}
					pos += typeSize;
				}
				int codeSize = ReadVarInt(data, ref pos);
				if (codeSize < 0 || (long)codeSize * 4 > data.Length - pos)
				{
					return null;
				}
				proto.Code = new uint[codeSize];
				for (int i = 0; i < codeSize; i++)
				{
					proto.Code[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
					pos += 4;
				}
				int constantCount = ReadVarInt(data, ref pos);
				if (constantCount < 0 || constantCount > data.Length)
				{
					return null;
				}
				proto.Constants = new Constant[constantCount];
				for (int i = 0; i < constantCount; i++)
				{
					int kind = data[pos++];
					switch (kind)
					{
						case 0:
							proto.Constants[i] = new Constant(kind, null);
							break;
						case 1:
							proto.Constants[i] = new Constant(kind, data[pos++] != 0);
							break;
						case 2:
							proto.Constants[i] = new Constant(kind, BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(pos, 8)));
							pos += 8;
							break;
						case 3:
						{
							int id = ReadVarInt(data, ref pos);
							proto.Constants[i] = new Constant(kind, id >= 1 && id <= stringCount ? strings[id] : null);
							break;
						}
						case 4:
							proto.Constants[i] = new Constant(kind, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)));
							pos += 4;
							break;
						case 5:
						{
							int keyCount = ReadVarInt(data, ref pos);
							if (keyCount < 0 || keyCount > data.Length - pos)
							{
								return null;
							}
							int[] keys = new int[keyCount];
							for (int j = 0; j < keyCount; j++)
							{
								keys[j] = ReadVarInt(data, ref pos);
							}
							proto.Constants[i] = new Constant(kind, keys);
							break;
						}
						case 6:
							proto.Constants[i] = new Constant(kind, ReadVarInt(data, ref pos));
							break;
						case 7:
							proto.Constants[i] = new Constant(kind, null);
							pos += 16;
							break;
						case 8:
						{
							int pairCount = ReadVarInt(data, ref pos);
							if (pairCount < 0 || pairCount > data.Length - pos)
							{
								return null;
							}
							(int Key, int Value)[] pairs = new (int, int)[pairCount];
							for (int j = 0; j < pairCount; j++)
							{
								int key = ReadVarInt(data, ref pos);
								int value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));
								pos += 4;
								pairs[j] = (key, value);
							}
							proto.Constants[i] = new Constant(kind, pairs);
							break;
						}
						default:
							return null;
					}
				}
				for (int i = 0; i < proto.Constants.Length; i++)
				{
					if (proto.Constants[i].Kind == 4 && proto.Constants[i].Value is uint importId)
					{
						proto.Constants[i] = new Constant(4, ResolveImport(proto.Constants, importId));
					}
				}
				int childCount = ReadVarInt(data, ref pos);
				for (int i = 0; i < childCount; i++)
				{
					ReadVarInt(data, ref pos);
				}
				ReadVarInt(data, ref pos);
				int debugName = ReadVarInt(data, ref pos);
				proto.Name = debugName >= 1 && debugName <= stringCount ? strings[debugName] : null;
				if (data[pos++] != 0)
				{
					int gap = data[pos++];
					int intervals = ((codeSize - 1) >> gap) + 1;
					pos += codeSize + intervals * 4;
				}
				if (data[pos++] != 0)
				{
					int locals = ReadVarInt(data, ref pos);
					for (int i = 0; i < locals; i++)
					{
						ReadVarInt(data, ref pos);
						ReadVarInt(data, ref pos);
						ReadVarInt(data, ref pos);
						pos++;
					}
					int upvalues = ReadVarInt(data, ref pos);
					for (int i = 0; i < upvalues; i++)
					{
						ReadVarInt(data, ref pos);
					}
				}
				if (pos > data.Length)
				{
					return null;
				}
				protos.Add(proto);
			}
			ReadVarInt(data, ref pos);
			return pos <= data.Length ? protos : null;
		}
		catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
		{
			return null;
		}
	}

	private static Import? ResolveImport(Constant[] constants, uint id)
	{
		int count = (int)(id >> 30);
		int[] indices = [(int)(id >> 20 & 1023), (int)(id >> 10 & 1023), (int)(id & 1023)];
		List<string> parts = [];
		for (int i = 0; i < count; i++)
		{
			if (indices[i] >= constants.Length || constants[indices[i]].Value is not string part)
			{
				return null;
			}
			parts.Add(part);
		}
		return parts.Count == 0 ? null : new Import(string.Join(".", parts));
	}

	private static int ReadVarInt(byte[] data, ref int pos)
	{
		int result = 0;
		int shift = 0;
		while (true)
		{
			byte value = data[pos++];
			result |= (value & 0x7F) << shift;
			if ((value & 0x80) == 0)
			{
				return result;
			}
			shift += 7;
			if (shift > 28)
			{
				throw new IndexOutOfRangeException();
			}
		}
	}
}
