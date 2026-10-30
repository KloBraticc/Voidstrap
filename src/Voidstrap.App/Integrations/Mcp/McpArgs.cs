using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.Mcp;

public static class McpArgs
{
    public static string? String(JsonObject? args, string name)
    {
        return Text(args?[name]);
    }

    public static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? text))
            return text;
        return value.GetValueKind() switch
        {
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null
        };
    }

    public static string Trimmed(JsonObject? args, string name, string fallback = "")
    {
        string? text = String(args, name)?.Trim();
        return string.IsNullOrEmpty(text) ? fallback : text;
    }

    public static bool? Bool(JsonObject? args, string name)
    {
        if (args?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out bool flag))
            return flag;
        string? text = Text(value)?.Trim();
        if (bool.TryParse(text, out bool parsed))
            return parsed;
        return text switch
        {
            "1" or "yes" or "on" => true,
            "0" or "no" or "off" => false,
            _ => null
        };
    }

    public static bool Flag(JsonObject? args, string name, bool fallback) => Bool(args, name) ?? fallback;

    public static long? Long(JsonObject? args, string name)
    {
        if (args?[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out long whole))
            return whole;
        if (value.TryGetValue(out double real) && double.IsFinite(real))
            return (long)Math.Round(real);
        return long.TryParse(Text(value)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
    }

    public static int Int(JsonObject? args, string name, int fallback, int min, int max)
    {
        long? value = Long(args, name);
        return value == null ? fallback : (int)Math.Clamp(value.Value, min, max);
    }

    public static bool IsSafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            return false;
        string trimmed = name.Trim();
        if (trimmed is "." or ".." || trimmed.EndsWith('.') || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        return !trimmed.Contains('/') && !trimmed.Contains('\\') && !trimmed.Contains(':');
    }

    public static string? ContainedPath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return null;
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string combined = Path.GetFullPath(Path.Combine(rootFull, relative));
        string prefix = rootFull + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }
}
