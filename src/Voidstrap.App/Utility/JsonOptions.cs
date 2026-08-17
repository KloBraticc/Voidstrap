using System.Text.Json;

namespace Voidstrap.Utility;

internal static class JsonOptions
{
	public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	public static readonly JsonSerializerOptions CaseInsensitive = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	};

	public static readonly JsonSerializerOptions IndentedCaseInsensitive = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public static readonly JsonSerializerOptions Compact = new JsonSerializerOptions();

	public static readonly JsonSerializerOptions Tolerant = BuildTolerant();

	private static JsonSerializerOptions BuildTolerant()
	{
		JsonSerializerOptions options = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			AllowTrailingCommas = true,
			ReadCommentHandling = JsonCommentHandling.Skip,
			NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
		};
		options.Converters.Add(new TolerantEnumConverterFactory());
		return options;
	}
}
