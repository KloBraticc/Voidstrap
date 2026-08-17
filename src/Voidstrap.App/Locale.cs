using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Voidstrap.Resources;
using FontFamily = System.Windows.Media.FontFamily;

namespace Voidstrap;

internal static class Locale
{
	public const string DefaultLocale = "nil";

	private static readonly HashSet<string> _rtlLocales = new HashSet<string> { "ar", "he", "fa" };

	public static readonly Dictionary<string, string> SupportedLocales = new Dictionary<string, string>
	{
		{
			"nil",
			Strings.Common_SystemDefault
		},
		{ "en-US", "English (Recommended)" },
		{ "ar", "العربية" },
		{ "bg", "Български" },
		{ "cs", "Čeština" },
		{ "de", "Deutsch" },
		{ "es-ES", "Español" },
		{ "fa", "فارسی" },
		{ "fi", "Suomi" },
		{ "fil", "Filipino" },
		{ "fr", "Français" },
		{ "hr", "Hrvatski" },
		{ "hu", "Magyar" },
		{ "id", "Bahasa Indonesia" },
		{ "it", "Italiano" },
		{ "ja", "日本語" },
		{ "ko", "한국어" },
		{ "lt", "Lietuvių" },
		{ "ms", "Malay" },
		{ "nl", "Nederlands" },
		{ "pl", "Polski" },
		{ "pt-BR", "Português (Brasil)" },
		{ "ro", "Română" },
		{ "ru", "Русский" },
		{ "sv-SE", "Svenska" },
		{ "th", "ภาษาไทย" },
		{ "tr", "Türkçe" },
		{ "uk", "Українська" },
		{ "vi", "Tiếng Việt" },
		{ "zh-CN", "中文 (简体)" },
		{ "zh-TW", "中文 (繁體)" }
	};

	public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.InvariantCulture;

	public static bool RightToLeft { get; private set; } = false;

	public static string GetIdentifierFromName(string language)
	{
		return SupportedLocales.FirstOrDefault<KeyValuePair<string, string>>((KeyValuePair<string, string> x) => x.Value == language).Key ?? "nil";
	}

	public static List<string> GetLanguages()
	{
		List<string> languages = SupportedLocales.Values.Take(3).ToList();
		languages.AddRange(from x in SupportedLocales.Values
			where !languages.Contains(x)
			orderby x
			select x);
		languages[0] = Strings.Common_SystemDefault;
		return languages;
	}

	public static void Set(string identifier)
	{
		if (!SupportedLocales.ContainsKey(identifier))
		{
			identifier = "nil";
		}
		if (identifier == "nil")
		{
			CurrentCulture = Thread.CurrentThread.CurrentUICulture;
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
			CultureInfo.DefaultThreadCurrentUICulture = CurrentCulture;
			Thread.CurrentThread.CurrentUICulture = CurrentCulture;
		}
		RightToLeft = IsRightToLeft(CurrentCulture.Name);
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

	private static bool IsRightToLeft(string cultureName)
	{
		if (string.IsNullOrEmpty(cultureName) || cultureName.Length < 2)
		{
			return false;
		}
		string item = cultureName.Substring(0, 2);
		return _rtlLocales.Contains(item);
	}

	public static void Initialize()
	{
		Set("nil");
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, (RoutedEventHandler)delegate(object sender, RoutedEventArgs _)
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
			else if (CurrentCulture.Name.StartsWith("th"))
			{
				window.FontFamily = new FontFamily(new Uri("pack://application:,,,/Resources/Fonts/"), "./#Noto Sans Thai");
			}
		});
	}
}
