using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Voidstrap.Utility;

internal static partial class NativeStateLoader
{
	private const string Library = "voidstate";

	private const uint Magic = 0x31545356;

	private const int StatusOk = 0;

	private const uint TagNull = 0;
	private const uint TagFalse = 1;
	private const uint TagTrue = 2;
	private const uint TagNumber = 3;
	private const uint TagString = 4;
	private const uint TagObject = 5;
	private const uint TagArray = 6;

	private const int StateUnknown = 0;
	private const int StateReady = 1;
	private const int StateMissing = 2;

	private static int _state;

	private static readonly ConcurrentDictionary<Type, TypePlan?> Plans = new();

	[LibraryImport(Library, EntryPoint = "voidstate_version")]
	private static partial uint Version();

	[LibraryImport(Library, EntryPoint = "voidstate_load")]
	private static unsafe partial int LoadNative(byte* path, nuint pathLength, ulong maximum, out IntPtr output, out nuint outputLength);

	[LibraryImport(Library, EntryPoint = "voidstate_free")]
	private static partial void FreeNative(IntPtr buffer, nuint length);

	private static bool IsAvailable
	{
		get
		{
			int state = Volatile.Read(ref _state);
			if (state != StateUnknown)
				return state == StateReady;
			bool ok;
			try
			{
				ok = Version() == 1;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				App.Logger?.WriteLine(
					"NativeStateLoader",
					$"Native voidstate library is unavailable ({ex.GetType().Name}), settings load in managed code"
				);
				ok = false;
			}
			Interlocked.CompareExchange(ref _state, ok ? StateReady : StateMissing, StateUnknown);
			return Volatile.Read(ref _state) == StateReady;
		}
	}

	public static bool TryLoad<T>(string path, long maximumBytes, out T? value)
		where T : class
	{
		value = null;
		if (typeof(IDictionary).IsAssignableFrom(typeof(T)) || !IsAvailable)
			return false;
		byte[]? buffer = Read(path, maximumBytes);
		if (buffer == null)
			return false;
		try
		{
			Reader reader = new Reader(buffer);
			if (reader.PeekTag() != TagObject)
				return false;
			value = reader.ReadValue(typeof(T)) as T;
			return value != null;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(
				"NativeStateLoader",
				$"{Path.GetFileName(path)} needs the managed loader ({ex.GetType().Name}: {ex.Message})"
			);
			value = null;
			return false;
		}
	}

	private static unsafe byte[]? Read(string path, long maximumBytes)
	{
		byte[] pathBytes = Encoding.UTF8.GetBytes(path);
		IntPtr output;
		nuint length;
		int status;
		try
		{
			fixed (byte* pathPointer = pathBytes)
				status = LoadNative(pathPointer, (nuint)pathBytes.Length, (ulong)Math.Max(0, maximumBytes), out output, out length);
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			Volatile.Write(ref _state, StateMissing);
			return null;
		}
		if (status != StatusOk || output == IntPtr.Zero)
			return null;
		try
		{
			if (length < 12 || length > int.MaxValue)
				return null;
			byte[] buffer = new byte[(int)length];
			Marshal.Copy(output, buffer, 0, buffer.Length);
			return buffer;
		}
		finally
		{
			FreeNative(output, length);
		}
	}

	private sealed class TypePlan
	{
		public required ConstructorInfo Constructor { get; init; }

		public required Dictionary<string, PropertyPlan> Properties { get; init; }
	}

	private sealed class PropertyPlan
	{
		public required PropertyInfo Property { get; init; }

		public required bool UseSerializer { get; init; }
	}

	private static TypePlan? GetPlan(Type type) => Plans.GetOrAdd(type, BuildPlan);

	private static bool IsSerializerAttribute(Attribute attribute) =>
		attribute.GetType().Namespace == typeof(JsonIgnoreAttribute).Namespace;

	private static TypePlan? BuildPlan(Type type)
	{
		if (type.IsAbstract || type.IsInterface || type.IsValueType || type.IsGenericTypeDefinition)
			return null;
		foreach (Attribute attribute in type.GetCustomAttributes(true))
		{
			if (IsSerializerAttribute(attribute))
				return null;
		}
		ConstructorInfo? constructor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
		if (constructor == null)
			return null;
		foreach (ConstructorInfo candidate in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
		{
			if (candidate.IsDefined(typeof(JsonConstructorAttribute), false))
				return null;
		}
		Dictionary<string, PropertyPlan> properties = new Dictionary<string, PropertyPlan>(StringComparer.OrdinalIgnoreCase);
		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
		{
			if (property.GetIndexParameters().Length != 0)
				continue;
			bool useSerializer = false;
			bool ignored = false;
			string name = property.Name;
			foreach (Attribute attribute in property.GetCustomAttributes(true))
			{
				switch (attribute)
				{
					case JsonIgnoreAttribute ignore:
						if (ignore.Condition == JsonIgnoreCondition.Always)
							ignored = true;
						break;
					case JsonPropertyNameAttribute rename:
						name = rename.Name;
						break;
					case JsonIncludeAttribute or JsonExtensionDataAttribute or JsonRequiredAttribute:
						return null;
					default:
						if (IsSerializerAttribute(attribute))
							useSerializer = true;
						break;
				}
			}
			if (property.IsDefined(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false))
				return null;
			MethodInfo? getter = property.GetMethod;
			MethodInfo? setter = property.SetMethod;
			bool publicGetter = getter is { IsPublic: true };
			bool publicSetter = setter is { IsPublic: true };
			if (!publicGetter && !publicSetter)
				continue;
			if (ignored)
				continue;
			if (publicSetter && !publicGetter)
				return null;
			if (!publicSetter)
			{
				if (properties.ContainsKey(name))
					return null;
				continue;
			}
			if (properties.ContainsKey(name))
				return null;
			properties[name] = new PropertyPlan { Property = property, UseSerializer = useSerializer };
		}
		return new TypePlan { Constructor = constructor, Properties = properties };
	}

	private ref struct Reader
	{
		private readonly byte[] _buffer;
		private readonly int _dataStart;
		private readonly int _dataLength;
		private readonly int _tokenStart;
		private readonly int _tokenCount;
		private int _index;

		public Reader(byte[] buffer)
		{
			_buffer = buffer;
			if (BinaryPrimitives.ReadUInt32LittleEndian(buffer) != Magic)
				throw new InvalidDataException("Unexpected native state header");
			_dataStart = 12;
			_dataLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)));
			_tokenStart = checked(_dataStart + _dataLength);
			_tokenCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8)));
			if (checked(_tokenStart + _tokenCount * 4L) != buffer.Length)
				throw new InvalidDataException("Native state buffer is truncated");
			_index = 0;
		}

		private readonly uint Token(int index)
		{
			if ((uint)index >= (uint)_tokenCount)
				throw new InvalidDataException("Native state token is out of range");
			return BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_tokenStart + index * 4));
		}

		private uint Next() => Token(_index++);

		public readonly uint PeekTag() => Token(_index);

		private ReadOnlySpan<byte> Data(uint offset, uint length)
		{
			if ((ulong)offset + length > (ulong)_dataLength)
				throw new InvalidDataException("Native state slice is out of range");
			return _buffer.AsSpan(_dataStart + (int)offset, (int)length);
		}

		private void Skip()
		{
			uint tag = Next();
			switch (tag)
			{
				case TagNull:
				case TagFalse:
				case TagTrue:
					return;
				case TagNumber:
				case TagString:
					_index += 2;
					return;
				case TagObject:
				case TagArray:
					uint count = Next();
					_index += 2;
					for (uint i = 0; i < count; i++)
					{
						if (tag == TagObject)
							_index += 3;
						Skip();
					}
					return;
				default:
					throw new InvalidDataException("Unknown native state token");
			}
		}

		private byte[] RawValue()
		{
			int start = _index;
			uint tag = Next();
			switch (tag)
			{
				case TagNull:
					return "null"u8.ToArray();
				case TagFalse:
					return "false"u8.ToArray();
				case TagTrue:
					return "true"u8.ToArray();
				case TagNumber:
					return Data(Next(), Next()).ToArray();
				case TagString:
				{
					string text = Encoding.UTF8.GetString(Data(Next(), Next()));
					return JsonSerializer.SerializeToUtf8Bytes(text, JsonOptions.Compact);
				}
				case TagObject:
				case TagArray:
				{
					Next();
					ReadOnlySpan<byte> raw = Data(Next(), Next());
					_index = start;
					Skip();
					return raw.ToArray();
				}
				default:
					throw new InvalidDataException("Unknown native state token");
			}
		}

		private object? Serializer(Type type) => JsonSerializer.Deserialize(RawValue(), type, JsonOptions.Tolerant);

		private string ReadString()
		{
			if (Next() != TagString)
				throw new NotSupportedException("Expected a string");
			return Encoding.UTF8.GetString(Data(Next(), Next()));
		}

		public object? ReadValue(Type type)
		{
			uint tag = PeekTag();
			Type? underlying = Nullable.GetUnderlyingType(type);
			if (underlying != null)
			{
				if (tag == TagNull)
				{
					_index++;
					return null;
				}
				return ReadValue(underlying);
			}
			if (type.IsEnum)
				return ReadEnum(type);
			if (tag == TagNull)
			{
				if (type.IsValueType)
					throw new NotSupportedException("Null for a value type " + type.Name);
				_index++;
				return null;
			}
			if (type == typeof(string))
				return ReadString();
			if (type == typeof(bool))
			{
				uint value = Next();
				return value switch
				{
					TagTrue => true,
					TagFalse => false,
					_ => throw new NotSupportedException("Expected a boolean"),
				};
			}
			if (type.IsPrimitive || type == typeof(decimal))
			{
				if (tag != TagNumber)
					return Serializer(type);
				_index++;
				return ReadNumber(type, Data(Next(), Next()));
			}
			if (type.IsArray && type.GetArrayRank() == 1)
			{
				if (tag != TagArray)
					throw new NotSupportedException("Expected an array");
				Type element = type.GetElementType()!;
				List<object?> items = ReadItems(element);
				Array array = Array.CreateInstance(element, items.Count);
				for (int i = 0; i < items.Count; i++)
					array.SetValue(items[i], i);
				return array;
			}
			if (type.IsGenericType)
			{
				Type definition = type.GetGenericTypeDefinition();
				if (definition == typeof(List<>) || definition == typeof(ObservableCollection<>))
				{
					if (tag != TagArray)
						throw new NotSupportedException("Expected an array");
					IList list = (IList)Activator.CreateInstance(type)!;
					foreach (object? item in ReadItems(type.GetGenericArguments()[0]))
						list.Add(item);
					return list;
				}
				if (definition == typeof(Dictionary<,>))
				{
					Type key = type.GetGenericArguments()[0];
					if (key == typeof(string) || key == typeof(long) || key == typeof(int))
					{
						if (tag != TagObject)
							throw new NotSupportedException("Expected an object");
						return ReadDictionary(type, key, type.GetGenericArguments()[1]);
					}
				}
				return Serializer(type);
			}
			if (tag == TagObject)
			{
				TypePlan? plan = GetPlan(type);
				if (plan != null)
					return ReadObject(plan);
			}
			return Serializer(type);
		}

		private List<object?> ReadItems(Type element)
		{
			Next();
			uint count = Next();
			_index += 2;
			List<object?> items = new List<object?>((int)Math.Min(count, 4096));
			for (uint i = 0; i < count; i++)
				items.Add(ReadValue(element));
			return items;
		}

		private object ReadDictionary(Type type, Type key, Type value)
		{
			Next();
			uint count = Next();
			_index += 2;
			IDictionary dictionary = (IDictionary)Activator.CreateInstance(type)!;
			for (uint i = 0; i < count; i++)
			{
				string name = ReadString();
				object keyValue;
				if (key == typeof(string))
					keyValue = name;
				else if (key == typeof(long))
					keyValue = long.TryParse(
						name,
						System.Globalization.NumberStyles.AllowLeadingSign,
						System.Globalization.CultureInfo.InvariantCulture,
						out long l
					)
						? l
						: throw new NotSupportedException("Dictionary key is not a number");
				else
					keyValue = int.TryParse(
						name,
						System.Globalization.NumberStyles.AllowLeadingSign,
						System.Globalization.CultureInfo.InvariantCulture,
						out int n
					)
						? n
						: throw new NotSupportedException("Dictionary key is not a number");
				if (dictionary.Contains(keyValue))
					throw new NotSupportedException("Duplicate dictionary key");
				dictionary.Add(keyValue, ReadValue(value));
			}
			return dictionary;
		}

		private object ReadObject(TypePlan plan)
		{
			Next();
			uint count = Next();
			_index += 2;
			object instance = plan.Constructor.Invoke(null);
			for (uint i = 0; i < count; i++)
			{
				string name = ReadString();
				if (!plan.Properties.TryGetValue(name, out PropertyPlan? property))
				{
					Skip();
					continue;
				}
				Type propertyType = property.Property.PropertyType;
				object? value = property.UseSerializer ? Serializer(propertyType) : ReadValue(propertyType);
				property.Property.SetValue(instance, value);
			}
			return instance;
		}

		private object ReadEnum(Type type)
		{
			uint tag = Next();
			switch (tag)
			{
				case TagString:
				{
					string text = Encoding.UTF8.GetString(Data(Next(), Next()));
					if (!string.IsNullOrEmpty(text) && Enum.TryParse(type, text, true, out object? parsed))
						return parsed!;
					return Activator.CreateInstance(type)!;
				}
				case TagNumber:
				{
					ReadOnlySpan<byte> number = Data(Next(), Next());
					if (Utf8Parser.TryParse(number, out long value, out int consumed) && consumed == number.Length)
						return Enum.ToObject(type, value);
					return Activator.CreateInstance(type)!;
				}
				default:
					_index--;
					Skip();
					return Activator.CreateInstance(type)!;
			}
		}

		private static object ReadNumber(Type type, ReadOnlySpan<byte> number)
		{
			int consumed;
			bool ok;
			object result;
			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Byte:
					ok = Utf8Parser.TryParse(number, out byte b, out consumed);
					result = b;
					break;
				case TypeCode.SByte:
					ok = Utf8Parser.TryParse(number, out sbyte sb, out consumed);
					result = sb;
					break;
				case TypeCode.Int16:
					ok = Utf8Parser.TryParse(number, out short s, out consumed);
					result = s;
					break;
				case TypeCode.UInt16:
					ok = Utf8Parser.TryParse(number, out ushort us, out consumed);
					result = us;
					break;
				case TypeCode.Int32:
					ok = Utf8Parser.TryParse(number, out int i, out consumed);
					result = i;
					break;
				case TypeCode.UInt32:
					ok = Utf8Parser.TryParse(number, out uint ui, out consumed);
					result = ui;
					break;
				case TypeCode.Int64:
					ok = Utf8Parser.TryParse(number, out long l, out consumed);
					result = l;
					break;
				case TypeCode.UInt64:
					ok = Utf8Parser.TryParse(number, out ulong ul, out consumed);
					result = ul;
					break;
				case TypeCode.Single:
					ok = Utf8Parser.TryParse(number, out float f, out consumed) && float.IsFinite(f);
					result = f;
					break;
				case TypeCode.Double:
					ok = Utf8Parser.TryParse(number, out double d, out consumed) && double.IsFinite(d);
					result = d;
					break;
				case TypeCode.Decimal:
					ok = Utf8Parser.TryParse(number, out decimal m, out consumed);
					result = m;
					break;
				default:
					throw new NotSupportedException("Unsupported number type " + type.Name);
			}
			if (!ok || consumed != number.Length)
				throw new NotSupportedException("Number does not fit " + type.Name);
			return result;
		}
	}
}
