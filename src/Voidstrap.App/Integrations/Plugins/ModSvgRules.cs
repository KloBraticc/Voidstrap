using System.Text.RegularExpressions;

namespace Voidstrap.Integrations.Plugins;

internal static partial class ModSvgRules
{
    public const int MaxSvgLine = 262144;

    [GeneratedRegex(@"<\s*(script|foreignObject|iframe|embed|object|image|link|meta|base)\b|<!(DOCTYPE|ENTITY)|<\?|\son[a-z]+\s*=|javascript:|data:|@import|url\(\s*['""]?(?!#)|\b(xlink:)?href\s*=\s*['""](?!#)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex UnsafePattern { get; }

    [GeneratedRegex(@"^\s*(?:export\s+default\s+|(?:export\s+)?(?:const|let|var)\s+[A-Za-z_$][\w$]*\s*=\s*|return\s+)?([""'`])((?:(?!\1)[^\\])*)\1\s*[;,]?\s*$", RegexOptions.None, 1000)]
    private static partial Regex StringLinePattern { get; }

    public static bool IsSafeSvg(string text)
    {
        try
        {
            return !UnsafePattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static bool IsSvgStringLine(string line)
    {
        if (line.Length > MaxSvgLine)
            return false;
        try
        {
            Match match = StringLinePattern.Match(line);
            if (!match.Success)
                return false;
            string body = match.Groups[2].Value.Trim();
            return body.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
                && body.EndsWith("</svg>", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("${", StringComparison.Ordinal)
                && IsSafeSvg(body);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
