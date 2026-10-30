using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Plugins;

internal static partial class ModLibraryReview
{
    private const string LogIdent = "ModLibraryReview";

    internal const string LibraryIndexUrl = "https://raw.githubusercontent.com/KloBraticc/voidstrap-mods-library/main/index.json";

    private const string NamePatternPattern = "^[a-z0-9][a-z0-9-]{0,63}$";

    private const string SemverPatternText = "^(0|[1-9]\\d{0,5})\\.(0|[1-9]\\d{0,5})\\.(0|[1-9]\\d{0,5})$";

    private const string HostPatternText = "^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$";

    private static readonly Regex SemverRegex = new(SemverPatternText, RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly Regex HostRegex = new(HostPatternText, RegexOptions.None, TimeSpan.FromSeconds(1));

    private static readonly Regex ReviewerInjectionPattern = new(
        "(ignore|disregard|forget|override)\\s+(all\\s+|any\\s+|the\\s+)?(previous|prior|above|earlier|system)\\s+(instructions|rules|prompts?)|you\\s+are\\s+(now\\s+)?(an?\\s+|the\\s+)?(ai|assistant|reviewer|language model)|system\\s+prompt|mark\\s+(this|it)\\s+(as\\s+)?safe|respond\\s+with\\s+.{0,20}safe|(say|respond|answer|output|return)\\s+.{0,30}verdict",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1)
    );

    private static readonly Regex InvisibleCharacterPattern = new(
        "[\\u200B\\u202A-\\u202E\\u2066-\\u2069]|(?!^)\\uFEFF",
        RegexOptions.None,
        TimeSpan.FromSeconds(1)
    );

    private static readonly string[] AllowedExtensions = [".ts", ".tsx", ".js", ".mjs", ".jsx", ".json", ".md", ".ico"];

    private static readonly string[] CodeExtensions = [".ts", ".tsx", ".js", ".mjs", ".jsx"];

    private static readonly HashSet<string> AllowedHosts = LoadAllowedHosts();

    private static readonly HashSet<string> KnownTags = LoadTags();

    private static readonly List<ReviewRule> BlockedRules = LoadBlockedRules();

    private static HashSet<string> LoadAllowedHosts()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(AllowedHostsJson);
            return [.. document.RootElement.GetProperty("hosts").EnumerateArray().Select(entry => entry.GetString() ?? "")];
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The allowed host list could not be read: " + ex.Message);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static HashSet<string> LoadTags()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(TagsJson);
            return [.. document.RootElement.GetProperty("tags").EnumerateArray().Select(entry => entry.GetString() ?? "")];
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The tag list could not be read: " + ex.Message);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static List<ReviewRule> LoadBlockedRules()
    {
        List<ReviewRule> rules = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(RulesJson);
            foreach (JsonElement entry in document.RootElement.GetProperty("blocked").EnumerateArray())
            {
                string pattern = entry.GetProperty("pattern").GetString() ?? "";
                string reason = entry.GetProperty("reason").GetString() ?? "";
                string level = entry.GetProperty("level").GetString() ?? "error";
                bool binary = !entry.TryGetProperty("binary", out JsonElement binaryValue) || binaryValue.ValueKind != JsonValueKind.False;
                bool codeOnly = entry.TryGetProperty("codeOnly", out JsonElement codeValue) && codeValue.ValueKind == JsonValueKind.True;
                RegexOptions options =
                    entry.TryGetProperty("caseSensitive", out JsonElement caseValue) && caseValue.ValueKind == JsonValueKind.True
                        ? RegexOptions.None
                        : RegexOptions.IgnoreCase;
                try
                {
                    rules.Add(
                        new ReviewRule(pattern, reason, level, binary, codeOnly, new Regex(pattern, options, TimeSpan.FromSeconds(1)))
                    );
                }
                catch (ArgumentException ex)
                {
                    App.Logger?.WriteLine(LogIdent, "A library rule could not be compiled: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The library rules could not be read: " + ex.Message);
        }
        return rules;
    }

    private static int Limit(string name, int fallback)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(RulesJson);
            if (document.RootElement.GetProperty("limits").TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed))
                return parsed;
        }
        catch (Exception) { }
        return fallback;
    }

    private sealed record ReviewRule(string Pattern, string Reason, string Level, bool Binary, bool CodeOnly, Regex Regex);

    public sealed record PreflightResult(int Errors, int Reviews, string Report);
}
