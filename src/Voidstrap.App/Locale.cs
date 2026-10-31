using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Resources;
using FontFamily = System.Windows.Media.FontFamily;

namespace Voidstrap;

internal static class Locale
{
    public const string DefaultLocale = "nil";

    private static readonly HashSet<string> _rtlLocales = new HashSet<string> { "ar", "he", "fa", "ur", "ps", "sd", "ug", "yi" };

    public static readonly Dictionary<string, string> SupportedLocales = new Dictionary<string, string>
    {
        {
            "nil",
            Strings.Common_SystemDefault
        },
        { "en-US", "English" },
        { "af", "Afrikaans" },
        { "ar", "العربية" },
        { "bg", "Български" },
        { "bn", "বাংলা" },
        { "ca", "Català" },
        { "cs", "Čeština" },
        { "da", "Dansk" },
        { "de", "Deutsch" },
        { "el", "Ελληνικά" },
        { "es-ES", "Español" },
        { "et", "Eesti" },
        { "fa", "فارسی" },
        { "fi", "Suomi" },
        { "fil", "Filipino" },
        { "fr", "Français" },
        { "he", "עברית" },
        { "hi", "हिन्दी" },
        { "hr", "Hrvatski" },
        { "hu", "Magyar" },
        { "id", "Bahasa Indonesia" },
        { "it", "Italiano" },
        { "ja", "日本語" },
        { "ko", "한국어" },
        { "lt", "Lietuvių" },
        { "lv", "Latviešu" },
        { "ms", "Malay" },
        { "nl", "Nederlands" },
        { "no", "Norsk" },
        { "pl", "Polski" },
        { "pt-BR", "Português (Brasil)" },
        { "ro", "Română" },
        { "ru", "Русский" },
        { "sk", "Slovenčina" },
        { "sl", "Slovenščina" },
        { "sr", "Српски" },
        { "sv-SE", "Svenska" },
        { "sw", "Kiswahili" },
        { "ta", "தமிழ்" },
        { "th", "ภาษาไทย" },
        { "tr", "Türkçe" },
        { "uk", "Українська" },
        { "vi", "Tiếng Việt" },
        { "zh-CN", "中文 (简体)" },
        { "zh-TW", "中文 (繁體)" },
        { "am", "አማርኛ" },
        { "az", "Azərbaycan" },
        { "be", "Беларуская" },
        { "bs", "Bosanski" },
        { "cy", "Cymraeg" },
        { "eo", "Esperanto" },
        { "eu", "Euskara" },
        { "ga", "Gaeilge" },
        { "gl", "Galego" },
        { "gu", "ગુજરાતી" },
        { "ha", "Hausa" },
        { "hy", "Հայերեն" },
        { "ig", "Igbo" },
        { "is", "Íslenska" },
        { "ka", "ქართული" },
        { "kk", "Қазақша" },
        { "km", "ខ្មែរ" },
        { "kn", "ಕನ್ನಡ" },
        { "ky", "Кыргызча" },
        { "lb", "Lëtzebuergesch" },
        { "lo", "ລາວ" },
        { "mg", "Malagasy" },
        { "mi", "Māori" },
        { "mk", "Македонски" },
        { "ml", "മലയാളം" },
        { "mn", "Монгол" },
        { "mr", "मराठी" },
        { "mt", "Malti" },
        { "my", "မြန်မာ" },
        { "ne", "नेपाली" },
        { "or", "ଓଡ଼ିଆ" },
        { "pa", "ਪੰਜਾਬੀ" },
        { "ps", "پښتو" },
        { "rw", "Kinyarwanda" },
        { "sd", "سنڌي" },
        { "si", "සිංහල" },
        { "so", "Soomaali" },
        { "sq", "Shqip" },
        { "st", "Sesotho" },
        { "te", "తెలుగు" },
        { "tg", "Тоҷикӣ" },
        { "tk", "Türkmençe" },
        { "ug", "ئۇيغۇرچە" },
        { "ur", "اردو" },
        { "uz", "Oʻzbek" },
        { "xh", "isiXhosa" },
        { "yi", "ייִדיש" },
        { "yo", "Yorùbá" },
        { "zu", "isiZulu" }
    };

    private static readonly Dictionary<string, string> LatinLocaleNames = new Dictionary<string, string>
    {
        { "ar", "Arabic" },
        { "bn", "Bengali" },
        { "fa", "Persian" },
        { "he", "Hebrew" },
        { "hi", "Hindi" },
        { "ja", "Japanese" },
        { "ko", "Korean" },
        { "ta", "Tamil" },
        { "th", "Thai" },
        { "zh-CN", "Chinese (Simplified)" },
        { "zh-TW", "Chinese (Traditional)" },
        { "am", "Amharic" },
        { "gu", "Gujarati" },
        { "hy", "Armenian" },
        { "ka", "Georgian" },
        { "km", "Khmer" },
        { "kn", "Kannada" },
        { "lo", "Lao" },
        { "ml", "Malayalam" },
        { "mr", "Marathi" },
        { "my", "Burmese" },
        { "ne", "Nepali" },
        { "or", "Odia" },
        { "pa", "Punjabi" },
        { "ps", "Pashto" },
        { "sd", "Sindhi" },
        { "si", "Sinhala" },
        { "te", "Telugu" },
        { "ug", "Uyghur" },
        { "ur", "Urdu" },
        { "yi", "Yiddish" }
    };

    static Locale()
    {
        if (!Voidstrap.Utility.Platform.IsLinux)
            return;

        foreach (KeyValuePair<string, string> replacement in LatinLocaleNames)
        {
            if (SupportedLocales.ContainsKey(replacement.Key))
                SupportedLocales[replacement.Key] = replacement.Value;
        }
    }

    private static readonly CultureInfo SystemUICulture = CultureInfo.CurrentUICulture;

    public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.InvariantCulture;

    public static bool RightToLeft { get; private set; } = false;

    public static string GetDisplayName(string identifier)
    {
        return identifier != DefaultLocale && SupportedLocales.TryGetValue(identifier, out string? name) ? name : Strings.Common_SystemDefault;
    }

    public static string GetIdentifierFromName(string language)
    {
        if (language == Strings.Common_SystemDefault)
        {
            return DefaultLocale;
        }
        return SupportedLocales.FirstOrDefault<KeyValuePair<string, string>>((KeyValuePair<string, string> x) => x.Value == language).Key ?? "nil";
    }

    public static void Set(string identifier)
    {
        if (!SupportedLocales.ContainsKey(identifier))
        {
            identifier = "nil";
        }
        if (identifier == "nil")
        {
            CurrentCulture = SystemUICulture;
        }
        else
        {
            try
            {
                CurrentCulture = new CultureInfo(identifier);
            }
            catch (CultureNotFoundException)
            {
                CurrentCulture = CultureInfo.InvariantCulture;
            }
        }
        CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;
        Thread.CurrentThread.CurrentUICulture = CurrentCulture;
        RightToLeft = MirrorsLayout && IsRightToLeft(CurrentCulture.Name);
        try
        {
            if (App.Settings.Prop.AutoTranslate)
            {
                Voidstrap.UI.LiveLanguageRefresher.RefreshAllOpenWindows();
            }
            else
            {
                Voidstrap.UI.LiveLanguageRefresher.RestoreAllOpenWindows();
            }
        }
        catch
        {
        }
    }

    public static bool IsRightToLeftLanguage(string language)
    {
        return IsRightToLeft(language);
    }

    private static bool MirrorsLayout => Voidstrap.Utility.Platform.IsWindows;

    private static bool IsRightToLeft(string cultureName)
    {
        if (!MirrorsLayout || string.IsNullOrEmpty(cultureName) || cultureName.Length < 2)
        {
            return false;
        }
        string item = cultureName.Substring(0, 2);
        return _rtlLocales.Contains(item);
    }

    public static void Initialize()
    {
        Set("nil");
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, (RoutedEventHandler)delegate (object sender, RoutedEventArgs _)
        {
            Window window = (Window)sender;
            if (RightToLeft)
            {
                window.FlowDirection = FlowDirection.RightToLeft;
                if (window.ContextMenu != null)
                {
                    window.ContextMenu.FlowDirection = FlowDirection.RightToLeft;
                }
            }
            ApplyScriptFont(window);
        });
    }

    private static FontFamily? _thaiFont;

    public static void ApplyScriptFont(Window window)
    {
        bool thai = !RightToLeft && CurrentCulture.Name.StartsWith("th", StringComparison.Ordinal);
        if (thai)
        {
            _thaiFont ??= new FontFamily(new Uri("pack://application:,,,/Resources/Fonts/"), "./#Noto Sans Thai");
            window.FontFamily = _thaiFont;
        }
        else if (_thaiFont != null && ReferenceEquals(window.ReadLocalValue(Control.FontFamilyProperty), _thaiFont))
        {
            window.ClearValue(Control.FontFamilyProperty);
        }
    }
}
