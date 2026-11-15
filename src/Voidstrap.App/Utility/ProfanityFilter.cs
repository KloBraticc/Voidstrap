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

    private static readonly string[] GlobalWords =
    {
        "mierda", "cabron", "cabrón", "cabrona", "pendejo", "pendeja", "joder", "jodete", "gilipollas", "verga", "chinga", "chingar", "chingada", "chingado", "chingue", "culero", "culera", "maricon", "maricón", "marica", "puta", "puto", "putas", "putos", "coño", "hijueputa", "malparido", "malparida", "pinche", "caralho", "porra", "merda", "foda", "fodase", "fodasse", "buceta", "viado", "arrombado", "arrombada", "desgraçado", "desgracado", "vadia", "fdp", "vsf", "pqp", "putain", "merde", "connard", "connasse", "salope", "enculé", "encule", "enculer", "batard", "bâtard", "pute", "niquer", "nique", "ntm", "pute", "gueule", "scheiße", "scheisse", "arschloch", "fotze", "hurensohn", "wichser", "fick", "ficken", "fickt", "schlampe", "missgeburt", "drecksau", "cazzo", "cazzi", "vaffanculo", "fanculo", "stronzo", "stronza", "puttana", "coglione", "coglioni", "minchia", "troia", "kut", "klootzak", "kanker", "godverdomme", "tering", "hoer", "kankerlijer", "kurwa", "kurwy", "chuj", "chuja", "pierdole", "pierdolę", "pierdolić", "jebać", "jebac", "jebany", "spierdalaj", "pizda", "skurwysyn", "блять", "бля", "блядь", "сука", "суки", "хуй", "хуя", "хуё", "пизда", "пиздец", "ебать", "ёбаный", "ебаный", "нахуй", "мудак", "долбоёб", "долбоеб", "шлюха", "уебок", "уёбок", "гандон", "курва", "amk", "siktir", "siktirgit", "orospu", "piç", "yarrak", "amına", "sikerim", "sikeyim", "oç", "göt", "bangsat", "kontol", "memek", "ngentot", "pukimak", "puki", "keparat", "putangina", "tangina", "gago", "pakyu", "ulol", "tarantado", "địt", "đéo", "lồn", "cặc", "đụ", "chutiya", "bhenchod", "behenchod", "madarchod", "gandu", "bhosdike", "bhosdi", "randi", "chutiye", "fitta", "kuk", "jävla", "javla", "vittu", "perkele", "saatana", "paska", "kokot", "hovno", "zmrd", "píča", "fasz", "geci", "picsa", "bazdmeg", "muie", "futu", "μαλάκα", "μαλακα", "μαλάκας", "γαμώ", "γαμω", "πουτάνα", "كس", "زب", "شرموطة", "منيوك", "चूतिया", "भेनचोद", "मादरचोद", "लौड़ा",
    };

    private static readonly string[] GlobalFragments =
    {
        "傻逼", "煞笔", "操你", "肏", "他妈的", "草泥马", "妈逼", "贱人", "婊子", "幹你娘", "干你娘", "靠北", "씨발", "시발", "병신", "개새끼", "좆", "ちんこ", "まんこ", "くそったれ", "死ね", "เหี้ย", "ควย", "สัส", "เย็ด",
    };

    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex WordPattern = new(
        "(?<![\\p{L}\\p{N}])(?:" + string.Join("|", ExactWords) + "|" + string.Join("|", GlobalWords) + ")(?![\\p{L}\\p{N}])|" + string.Join("|", GlobalFragments),
        MatchOptions | RegexOptions.Compiled,
        MatchTimeout
    );

    private static readonly Regex StemPattern = new(string.Join("|", Stems), MatchOptions | RegexOptions.NonBacktracking, MatchTimeout);

    private static readonly Regex WordRunPattern = new("[\\p{L}\\p{N}\\*@!]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, MatchTimeout);

    private static string Mask(Match match) => new('#', match.Length);

    private static string MaskStemRun(Match run) => StemPattern.IsMatch(run.Value) ? Mask(run) : run.Value;

    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        try
        {
            string cleaned = WordPattern.Replace(text, Mask);
            return StemPattern.IsMatch(cleaned) ? WordRunPattern.Replace(cleaned, MaskStemRun) : cleaned;
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
            return StemPattern.IsMatch(text) || WordPattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
