using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using FontFamily = System.Windows.Media.FontFamily;

namespace Voidstrap.UI;

internal static class LinuxTextFallback
{
	private const int MaxCachedLookups = 8192;

	private static readonly string[] PreferredFamilies =
	[
		"Noto Sans",
		"DejaVu Sans",
		"Noto Sans CJK SC",
		"Noto Sans CJK JP",
		"Noto Sans CJK KR",
		"Noto Sans CJK TC",
		"Source Han Sans SC",
		"WenQuanYi Micro Hei",
		"Droid Sans Fallback",
		"Noto Sans Symbols",
		"Noto Sans Symbols 2",
		"Noto Sans Math",
		"Noto Emoji",
		"Symbola",
		"Liberation Sans",
		"FreeSans",
		"FreeSerif",
		"Unifont"
	];

	private static readonly object Gate = new();

	private static readonly Dictionary<(Typeface Primary, int CodePoint), Typeface?> Lookups = new();

	private static List<FontFamily>? _candidates;

	public static bool Active { get; private set; }

	public static void Install()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		FieldInfo? hook = typeof(TextFormatter).GetField("VoidstrapRunFilter", BindingFlags.Public | BindingFlags.Static);
		if (hook == null)
		{
			App.Logger.WriteLine("LinuxTextFallback", "The text renderer has no run hook, characters outside the primary font stay hidden");
			return;
		}

		hook.SetValue(null, new Func<TextRun, string, TextRun?>(Split));
		Active = true;
		App.Logger.WriteLine("LinuxTextFallback", "Characters outside the primary font now use installed fallback fonts");
	}

	internal static TextRun? Split(TextRun run, string text)
	{
		if (text.Length == 0 || text[0] is '\r' or '\n' or '\t')
			return null;

		TextRunProperties properties = run.Properties;
		Typeface primary = properties.Typeface;
		if (!primary.TryGetGlyphTypeface(out GlyphTypeface primaryGlyphs))
			return null;

		IDictionary<int, ushort> primaryMap = primaryGlyphs.CharacterToGlyphMap;
		char first = text[0];
		if (char.IsSurrogate(first))
			return Supplementary(text, properties, primary, primaryMap);

		if (IsRightToLeft(first))
			return RightToLeft(text, properties, primary, primaryMap);

		if (primaryMap.ContainsKey(first))
		{
			int covered = CoveredLength(text, primaryMap, null);
			return covered == text.Length ? null : new TextCharacters(text, 0, covered, properties);
		}

		if (!IsInvisibleFormat(first) && !char.IsControl(first))
		{
			Typeface? fallback = Resolve(primary, first);
			if (fallback != null && fallback.TryGetGlyphTypeface(out GlyphTypeface fallbackGlyphs))
				return new TextCharacters(text, 0, CoveredLength(text, fallbackGlyphs.CharacterToGlyphMap, primaryMap), new FallbackProperties(properties, fallback));
		}

		return new TextHidden(1);
	}

	private static TextRun Supplementary(string text, TextRunProperties properties, Typeface primary, IDictionary<int, ushort> primaryMap)
	{
		if (text.Length < 2 || !char.IsSurrogatePair(text[0], text[1]))
			return new TextHidden(1);

		string folded = text.Substring(0, 2).Normalize(NormalizationForm.FormKC);
		if (folded.Length == 1 && !char.IsSurrogate(folded[0]) && !char.IsControl(folded[0]))
		{
			if (primaryMap.ContainsKey(folded[0]))
				return new TextCharacters(folded, 0, 1, properties);

			Typeface? fallback = Resolve(primary, folded[0]);
			if (fallback != null)
				return new TextCharacters(folded, 0, 1, new FallbackProperties(properties, fallback));
		}

		return new TextHidden(2);
	}

	private static TextRun RightToLeft(string text, TextRunProperties properties, Typeface primary, IDictionary<int, ushort> primaryMap)
	{
		Typeface? face = primaryMap.ContainsKey(text[0]) ? primary : Resolve(primary, text[0]);
		if (face == null || !face.TryGetGlyphTypeface(out GlyphTypeface glyphs))
			return new TextHidden(1);

		IDictionary<int, ushort> map = glyphs.CharacterToGlyphMap;
		int length = 1;
		int end = 1;
		while (length < text.Length)
		{
			char next = text[length];
			bool joins = next == ' ' || CharUnicodeInfo.GetUnicodeCategory(next) == UnicodeCategory.NonSpacingMark;
			if (!(IsRightToLeft(next) || joins) || !map.ContainsKey(next))
				break;
			length++;
			if (next != ' ')
				end = length;
		}

		string segment = text.Substring(0, end);
		StringBuilder reversed = new(end);
		int[] starts = StringInfo.ParseCombiningCharacters(segment);
		for (int index = starts.Length - 1; index >= 0; index--)
		{
			int start = starts[index];
			int stop = index + 1 < starts.Length ? starts[index + 1] : segment.Length;
			reversed.Append(segment, start, stop - start);
		}

		TextRunProperties runProperties = ReferenceEquals(face, primary) ? properties : new FallbackProperties(properties, face);
		return new TextCharacters(reversed.ToString(), 0, end, runProperties);
	}

	private static bool IsRightToLeft(char value)
	{
		return value is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= 'ﻼ' && !char.IsDigit(value);
	}

	private static int CoveredLength(string text, IDictionary<int, ushort> map, IDictionary<int, ushort>? preferred)
	{
		int length = 1;
		while (length < text.Length)
		{
			char next = text[length];
			if (char.IsSurrogate(next) || !map.ContainsKey(next) || preferred != null && preferred.ContainsKey(next))
				break;
			length++;
		}

		return length;
	}

	private static bool IsInvisibleFormat(char value)
	{
		return value is >= '︀' and <= '️' or '‍' or '‌' or '⃣';
	}

	private static Typeface? Resolve(Typeface primary, int codePoint)
	{
		lock (Gate)
		{
			if (Lookups.TryGetValue((primary, codePoint), out Typeface? cached))
				return cached;

			Typeface? found = null;
			foreach (FontFamily family in Candidates())
			{
				Typeface candidate = new(family, primary.Style, primary.Weight, primary.Stretch);
				if (candidate.TryGetGlyphTypeface(out GlyphTypeface glyphs) && glyphs.CharacterToGlyphMap.ContainsKey(codePoint))
				{
					found = candidate;
					break;
				}
			}

			if (Lookups.Count >= MaxCachedLookups)
				Lookups.Clear();
			Lookups[(primary, codePoint)] = found;
			return found;
		}
	}

	private static List<FontFamily> Candidates()
	{
		if (_candidates != null)
			return _candidates;

		List<FontFamily> ordered = new();
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, FontFamily> installed = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			foreach (FontFamily family in Fonts.SystemFontFamilies)
			{
				string name = family.Source;
				if (!string.IsNullOrWhiteSpace(name) && !name.Contains("Color", StringComparison.OrdinalIgnoreCase))
					installed.TryAdd(name, family);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxTextFallback", "Installed fonts could not be listed: " + ex.Message);
		}

		foreach (string name in PreferredFamilies)
		{
			if (installed.TryGetValue(name, out FontFamily? family) && seen.Add(name))
				ordered.Add(family);
		}

		foreach ((string name, FontFamily family) in installed)
		{
			if (seen.Add(name))
				ordered.Add(family);
		}

		App.Logger.WriteLine("LinuxTextFallback", "Fallback search covers " + ordered.Count + " font families");
		_candidates = ordered;
		return ordered;
	}

	private sealed class FallbackProperties : TextRunProperties
	{
		private readonly TextRunProperties _source;

		private readonly Typeface _typeface;

		public FallbackProperties(TextRunProperties source, Typeface typeface)
		{
			_source = source;
			_typeface = typeface;
			PixelsPerDip = source.PixelsPerDip;
		}

		public override Typeface Typeface => _typeface;

		public override double FontRenderingEmSize => _source.FontRenderingEmSize;

		public override double FontHintingEmSize => _source.FontHintingEmSize;

		public override TextDecorationCollection TextDecorations => _source.TextDecorations;

		public override Brush ForegroundBrush => _source.ForegroundBrush;

		public override Brush BackgroundBrush => _source.BackgroundBrush;

		public override CultureInfo CultureInfo => _source.CultureInfo;

		public override TextEffectCollection TextEffects => _source.TextEffects;

		public override BaselineAlignment BaselineAlignment => _source.BaselineAlignment;

		public override TextRunTypographyProperties TypographyProperties => _source.TypographyProperties;

		public override NumberSubstitution NumberSubstitution => _source.NumberSubstitution;
	}
}
