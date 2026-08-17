using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed record SettingsDefaultDefinition(string Name, JsonNode DefaultValue);

public static class SettingsSchemaImporter
{
	private const long MaximumSchemaBytes = 4L * 1024 * 1024;
	private const int MaximumDefinitions = 10_000;
	private static readonly Regex PropertyExpression = new(
		"^\\s*public\\s+(?<type>[A-Za-z_][A-Za-z0-9_?.]*(?:<[^;{}]+>)?)\\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\\s*\\{\\s*get;\\s*set;\\s*\\}(?:\\s*=\\s*(?<initializer>[^;]+))?;",
		RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

	public static string DefaultSchemaPath => Path.Combine(AppContext.BaseDirectory, "Catalog", "AppSettings.cs");

	public static async Task<OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>>> LoadAsync(string? schemaPath = null, CancellationToken cancellationToken = default)
	{
		string path = string.IsNullOrWhiteSpace(schemaPath) ? DefaultSchemaPath : schemaPath;
		try
		{
			if (!File.Exists(path))
			{
				return OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>>.Fail("SettingsSchemaMissing", "The current settings schema is unavailable");
			}
			if (new FileInfo(path).Length > MaximumSchemaBytes)
			{
				return OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>>.Fail("SettingsSchemaTooLarge", "The current settings schema is too large");
			}

			string source = await ReadTextAsync(path, cancellationToken);
			return OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>>.Success(Parse(source));
		}
		catch (OperationCanceledException)
		{
			return OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>>.Fail("OperationCanceled", "Settings schema loading was canceled");
		}
		catch (IOException exception)
		{
			return OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>>.Fail("SettingsSchemaReadFailed", exception.Message);
		}
		catch (InvalidDataException exception)
		{
			return OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>>.Fail("SettingsSchemaReadFailed", exception.Message);
		}
		catch (UnauthorizedAccessException exception)
		{
			return OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>>.Fail("SettingsSchemaAccessDenied", exception.Message, CapabilityState.RequiresPermission);
		}
	}

	public static IReadOnlyCollection<SettingsDefaultDefinition> Parse(string source)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return Array.Empty<SettingsDefaultDefinition>();
		}

		return PropertyExpression.Matches(source)
			.Take(MaximumDefinitions)
			.Select(static match => TryCreateDefinition(match, out SettingsDefaultDefinition? definition) ? definition : null)
			.Where(static definition => definition is not null)
			.Cast<SettingsDefaultDefinition>()
			.GroupBy(static definition => definition.Name, StringComparer.Ordinal)
			.Select(static group => group.First())
			.ToArray();
	}

	private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaximumSchemaBytes)
			throw new InvalidDataException("Settings schema file size is invalid");
		byte[] data = new byte[checked((int)stream.Length)];
		int offset = 0;
		while (offset < data.Length)
		{
			int read = await stream.ReadAsync(data.AsMemory(offset), cancellationToken);
			if (read == 0)
				throw new EndOfStreamException();
			offset += read;
		}
		if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
			throw new InvalidDataException("Settings schema file changed while it was being read");
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, System.Text.Encoding.UTF8, true);
		return await reader.ReadToEndAsync(cancellationToken);
	}

	private static bool TryCreateDefinition(Match match, out SettingsDefaultDefinition? definition)
	{
		string type = match.Groups["type"].Value;
		string name = match.Groups["name"].Value;
		string initializer = match.Groups["initializer"].Success ? match.Groups["initializer"].Value.Trim() : string.Empty;
		if (!TryCreateDefaultValue(type, initializer, out JsonNode? value) || value is null)
		{
			definition = null;
			return false;
		}

		definition = new SettingsDefaultDefinition(name, value);
		return true;
	}

	private static bool TryCreateDefaultValue(string type, string initializer, out JsonNode? value)
	{
		string normalizedType = type.TrimEnd('?');
		switch (normalizedType)
		{
			case "bool":
				value = JsonValue.Create(string.Equals(initializer, "true", StringComparison.OrdinalIgnoreCase));
				return true;
			case "int":
				value = JsonValue.Create(ParseInt32(initializer));
				return true;
			case "long":
				value = JsonValue.Create(ParseInt64(initializer));
				return true;
			case "double":
				value = JsonValue.Create(ParseDouble(initializer));
				return true;
			case "decimal":
				value = JsonValue.Create(ParseDecimal(initializer));
				return true;
			case "string":
				if (string.Equals(initializer, "string.Empty", StringComparison.Ordinal))
				{
					value = JsonValue.Create(string.Empty);
					return true;
				}
				if (TryParseString(initializer, out string parsedString))
				{
					value = JsonValue.Create(parsedString);
					return true;
				}
				value = null;
				return false;
		}

		if (string.IsNullOrWhiteSpace(initializer) || !initializer.StartsWith(type + ".", StringComparison.Ordinal))
		{
			value = null;
			return false;
		}

		int separator = initializer.LastIndexOf(".", StringComparison.Ordinal);
		if (separator < 0 || separator == initializer.Length - 1)
		{
			value = null;
			return false;
		}

		value = JsonValue.Create(initializer[(separator + 1)..]);
		return true;
	}

	private static int ParseInt32(string value)
	{
		if (string.Equals(value, "Environment.ProcessorCount", StringComparison.Ordinal))
		{
			return Environment.ProcessorCount;
		}

		return int.TryParse(value, out int result) ? result : 0;
	}

	private static long ParseInt64(string value)
	{
		return long.TryParse(value, out long result) ? result : 0L;
	}

	private static double ParseDouble(string value)
	{
		return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : 0D;
	}

	private static decimal ParseDecimal(string value)
	{
		return decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal result) ? result : 0M;
	}

	private static bool TryParseString(string initializer, out string value)
	{
		if (initializer.Length < 2 || initializer[0] != '"' || initializer[^1] != '"')
		{
			value = "";
			return false;
		}

		try
		{
			value = Regex.Unescape(initializer[1..^1]);
		}
		catch (ArgumentException)
		{
			value = initializer[1..^1];
		}
		return true;
	}
}
