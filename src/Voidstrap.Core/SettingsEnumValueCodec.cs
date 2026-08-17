using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Voidstrap.Core;

public static class SettingsEnumValueCodec
{
	public static string Get(SettingsDocument document, string key, IReadOnlyList<string> values, string fallback)
	{
		if (document is null)
		{
			throw new ArgumentNullException(nameof(document));
		}

		if (string.IsNullOrWhiteSpace(key) || values.Count == 0 || document.Root[key] is not JsonValue value)
		{
			return fallback;
		}

		if (value.TryGetValue<string>(out string? text))
		{
			foreach (string option in values)
			{
				if (string.Equals(option, text, StringComparison.OrdinalIgnoreCase))
				{
					return option;
				}
			}
		}

		if (value.TryGetValue<int>(out int number) && number >= 0 && number < values.Count)
		{
			return values[number];
		}

		if (value.TryGetValue<long>(out long longNumber) && longNumber >= 0 && longNumber < values.Count)
		{
			return values[(int)longNumber];
		}

		return fallback;
	}

	public static void Set(SettingsDocument document, string key, IReadOnlyList<string> values, string value)
	{
		if (document is null)
		{
			throw new ArgumentNullException(nameof(document));
		}

		int index = FindIndex(values, value);
		if (index < 0)
		{
			throw new ArgumentException("The enum value is not supported", nameof(value));
		}

		if (document.Root[key] is JsonValue existing && (existing.TryGetValue<int>(out _) || existing.TryGetValue<long>(out _)))
		{
			document.Set(key, index);
			return;
		}

		document.Set(key, values[index]);
	}

	private static int FindIndex(IReadOnlyList<string> values, string value)
	{
		for (int index = 0; index < values.Count; index++)
		{
			if (string.Equals(values[index], value, StringComparison.OrdinalIgnoreCase))
			{
				return index;
			}
		}

		return -1;
	}
}
