using System.Text.RegularExpressions;

namespace Voidstrap.Utility;

public static class ProfanityFilter
{
    private static readonly string[] Stems =
    {
        "f+[u\\*@v]+c+k+", "f+[u\\*]+k+", "f+[a@]+g+", "f+[a@]+g+[o0]+t+", "sh+[i1!\\*]+t+", "b+[i1!\\*]+t+c+h+", "c+[u\\*]+n+t+",
        "p+[u\\*]+s+s+y+", "a+s+s+h+[o0]+l+e+", "b+[a@]+s+t+[a@]+r+d+", "w+h+[o0]+r+e+",
        "s+l+[u\\*]+t+", "n+[i1!]+g+g+", "n+[i1!]+g+[a@]+", "r+[e3]+t+[a@]+r+d+", "m+[o0]+t+h+[e3]+r+f+", "j+[e3]+r+k+[o0]+f+f+",
        "w+[a@]+n+k+", "t+w+[a@]+t+", "p+[o0]+r+n+", "c+[u\\*]+m+s+h+[o0]+t+", "d+[i1!]+l+d+[o0]+", "t+r+[a@]+n+n+y+", "d+[a@]+m+n+", "p+[i1!]+s+s+",
    };

    private static readonly string[] ExactWords =
    {
        "a+s+s+", "a+s+s+e+s+", "t+[i1!]+t+s*", "c+[u\\*]+m+", "f+u+", "s+t+f+u+", "w+t+f+", "h+o+e+s*", "d+[i1!]+c+k+s*", "c+[o0]+c+k+s*", "k+y+s+", "k+[i1!]+k+[e3]+s*", "s+p+[i1!]+c+s*", "c+h+[i1!]+n+k+s*", "d+[i1!]+c+k+h+[e3]+[a@]+d+s*", "c+[o0]+c+k+s+[u\\*]+c+k+[e3]+r+s*",
    };

    private static readonly Regex Pattern = new(
        "(?<![\\p{L}\\p{N}])(?:" + string.Join("|", ExactWords) + ")(?![\\p{L}\\p{N}])|[\\p{L}\\p{N}\\*@!]*(?:" + string.Join("|", Stems) + ")[\\p{L}\\p{N}\\*@!]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250)
    );

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        try
        {
            return Pattern.Replace(text, static match => new string('#', match.Length));
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    public static bool HasProfanity(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            return Pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
