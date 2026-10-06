using System;
using System.Text.RegularExpressions;

namespace Voidstrap.Utility;

internal static partial class RpcText
{
	private const string Open = "⟪";

	private const string Close = "⟫";

	private static readonly string[] KnownPhrases =
	[
		"Inside", "Browsing Roblox", "Loading game", "by", "By", "in", "Playing in", "FFlag", "FFlags",
		"Private server", "Reserved server", "Public server", "Private experience", "Join server", "View game",
		"Editing", "Playtesting", "Testing", "in Team Create", "On the start page", "Testing the experience",
		"Building the experience", "Editing UI", "In Roblox Studio", "Get", "Viewing", "Looking at a Roblox game",
		"Idle", "Browsing the app", "Exploring the app", "Nothing playing", "Music Player", "Playing", "Paused",
		"Paused at", "of", "On repeat"
	];

	[GeneratedRegex("⟪([^⟫]*)⟫")]
	private static partial Regex MarkerRegex { get; }

	public static string Language
	{
		get
		{
			try
			{
				return App.Settings?.Prop?.DiscordRpcLanguage ?? "";
			}
			catch (Exception)
			{
				return "";
			}
		}
	}

	public static string Mark(string phrase)
	{
		return string.IsNullOrWhiteSpace(phrase) ? phrase : Open + phrase + Close;
	}

	public static string Render(string? text)
	{
		if (string.IsNullOrEmpty(text) || !text.Contains(Open, StringComparison.Ordinal))
			return text ?? "";
		string language = Language;
		return MarkerRegex.Replace(text, match => Translate(match.Groups[1].Value, language)).Replace(Open, "", StringComparison.Ordinal).Replace(Close, "", StringComparison.Ordinal);
	}

	public static void Prewarm()
	{
		string language = Language;
		if (language.Length == 0)
			return;
		foreach (string phrase in KnownPhrases)
			Translate(phrase, language);
	}

	private static string Translate(string phrase, string language)
	{
		if (language.Length == 0 || string.IsNullOrWhiteSpace(phrase))
			return phrase;
		try
		{
			return TranslationService.Translate(phrase, language);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RpcText::Translate", "The Discord status text could not be translated: " + ex.Message);
			return phrase;
		}
	}
}
