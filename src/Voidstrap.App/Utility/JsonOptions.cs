using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Voidstrap.Utility;

internal static class JsonOptions
{
    private static readonly IJsonTypeInfoResolver Resolver = JsonTypeInfoResolver.Combine(PersistedJsonContext.Default, new DefaultJsonTypeInfoResolver());

    public static readonly JsonSerializerOptions Indented = new JsonSerializerOptions
    {
        WriteIndented = true,
        TypeInfoResolver = Resolver
    };

    public static readonly JsonSerializerOptions CaseInsensitive = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = Resolver
    };

    public static readonly JsonSerializerOptions IndentedCaseInsensitive = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = Resolver
    };

    public static readonly JsonSerializerOptions Compact = new JsonSerializerOptions
    {
        TypeInfoResolver = Resolver
    };

    public static readonly JsonSerializerOptions Tolerant = BuildTolerant();

    private static JsonSerializerOptions BuildTolerant()
    {
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            TypeInfoResolver = Resolver
        };
        options.Converters.Add(new TolerantEnumConverterFactory());
        return options;
    }
}
