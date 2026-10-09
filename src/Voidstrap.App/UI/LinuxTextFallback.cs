using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using FontFamily = System.Windows.Media.FontFamily;

namespace Voidstrap.UI;

internal static class LinuxTextFallback
{
	private const int MaxCachedLookups = 8192;

	private const int MaxUncoveredCodePoints = 4096;

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
		"Unifont",
		"Helvetica Neue",
		"PingFang SC",
		"PingFang TC",
		"PingFang HK",
		"Hiragino Sans",
		"Hiragino Kaku Gothic ProN",
		"Apple SD Gothic Neo",
		"Geeza Pro",
		"Thonburi",
		"Kohinoor Devanagari",
		"Apple Symbols",
		"Arial Unicode MS",
		"STIX Two Math",
		"Lucida Grande"
	];

	private static readonly object Gate = new();

	private static readonly Dictionary<(Typeface Primary, int CodePoint), Typeface?> Lookups = new();

	private static List<FontFamily>? _candidates;

	private static readonly HashSet<int> Uncovered = new();

	private static readonly HashSet<string> PreferredNames = new(PreferredFamilies, StringComparer.OrdinalIgnoreCase);

#if CROSSPLAT
	private static Dictionary<string, ProGPU.Text.FontInfo>? _fontFiles;
#endif

	private const string EmojiFontResource = "Voidstrap.Resources.Fonts.VoidstrapEmoji.ttf";

	private const string EmojiMapResource = "Voidstrap.Resources.Fonts.VoidstrapEmoji.map";

	private const string EmojiFileName = "VoidstrapEmoji.ttf";

	private const string EmojiFamilyName = "Voidstrap Emoji";

	private const char EmojiPad = '\uE000';

	private const int EmojiPreparationWaitMilliseconds = 2000;

	private static readonly (int First, int Last)[] EmojiPresentationRanges =
	[
		(0x231A, 0x231B), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE),
		(0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
		(0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4),
		(0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD),
		(0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E),
		(0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
		(0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55)
	];

	private static Task? _emojiPreparation;

	private static string? _emojiDirectory;

	private static Dictionary<string, char>? _emojiSequences;

	private static HashSet<int>? _emojiStarts;

	private static bool _emojiAttempted;

	private static EmojiTables? _emoji;

	public static bool Active { get; private set; }

	public static void Install()
	{
		if (Voidstrap.Utility.Platform.IsWindows)
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
		_emojiPreparation = Task.Run(PrepareEmoji);
	}

	private static void PrepareEmoji()
	{
		try
		{
			Assembly assembly = typeof(LinuxTextFallback).Assembly;
			using Stream? font = assembly.GetManifestResourceStream(EmojiFontResource);
			using Stream? map = assembly.GetManifestResourceStream(EmojiMapResource);
			if (font == null || map == null)
			{
				App.Logger.WriteLine("LinuxTextFallback", "This build has no bundled emoji font, emoji stay hidden");
				return;
			}

			Dictionary<string, char> sequences = new(StringComparer.Ordinal);
			HashSet<int> starts = new();
			using (StreamReader reader = new(map))
			{
				StringBuilder key = new();
				string? line;
				while ((line = reader.ReadLine()) != null)
				{
					int tab = line.IndexOf('\t');
					if (tab <= 0
						|| !int.TryParse(line.AsSpan(tab + 1).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int mapped)
						|| mapped is <= EmojiPad or > 0xF8FF)
						continue;

					key.Clear();
					int first = -1;
					foreach (string part in line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries))
					{
						if (!int.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int point) || !Rune.IsValid(point))
						{
							key.Clear();
							break;
						}
						if (first < 0)
							first = point;
						key.Append(char.ConvertFromUtf32(point));
					}

					if (key.Length == 0)
						continue;
					sequences.TryAdd(key.ToString(), (char)mapped);
					starts.Add(first);
				}
			}

			byte[] content;
			using (MemoryStream buffer = new())
			{
				font.CopyTo(buffer);
				content = buffer.ToArray();
			}

			string directory = EmojiCacheDirectory();
			Directory.CreateDirectory(directory);
			string path = Path.Combine(directory, EmojiFileName);
			if (!File.Exists(path) || new FileInfo(path).Length != content.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
			{
				string temporary = path + "." + Guid.NewGuid().ToString("N");
				File.WriteAllBytes(temporary, content);
				File.Move(temporary, path, true);
			}

			lock (Gate)
			{
				_emojiSequences = sequences;
				_emojiStarts = starts;
				_emojiDirectory = directory;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxTextFallback", "The emoji font could not be prepared: " + ex.Message);
		}
	}

	private static string EmojiCacheDirectory()
	{
		if (Voidstrap.Utility.Platform.IsMacOS)
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Voidstrap", "Fonts");

		string? cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
		if (string.IsNullOrWhiteSpace(cache) || !Path.IsPathRooted(cache))
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			if (string.IsNullOrWhiteSpace(home) || !Path.IsPathRooted(home))
				return Path.Combine(Path.GetTempPath(), "Voidstrap", "Fonts");
			cache = Path.Combine(home, ".cache");
		}

		return Path.Combine(cache, "voidstrap", "fonts");
	}

	private static EmojiTables? Emoji()
	{
		Task? preparation = _emojiPreparation;
		if (preparation is { IsCompleted: false })
			SpinWait.SpinUntil(() => preparation.IsCompleted, EmojiPreparationWaitMilliseconds);

		lock (Gate)
		{
			if (_emojiAttempted || _emojiDirectory == null || _emojiSequences == null || _emojiStarts == null)
				return _emoji;

			_emojiAttempted = true;
			try
			{
				FontFamily family = new(new Uri(_emojiDirectory + Path.DirectorySeparatorChar), "./#" + EmojiFamilyName);
				Typeface typeface = new(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
				if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs) && glyphs.CharacterToGlyphMap.ContainsKey(EmojiPad))
				{
					_emoji = new EmojiTables(typeface, _emojiSequences, _emojiStarts);
					App.Logger.WriteLine("LinuxTextFallback", "Emoji now render in color, " + _emojiSequences.Count + " emoji sequences are available");
				}
				else
				{
					App.Logger.WriteLine("LinuxTextFallback", "The bundled emoji font could not be read, emoji stay hidden");
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("LinuxTextFallback", "The bundled emoji font could not be loaded: " + ex.Message);
			}

			return _emoji;
		}
	}

	private static bool IsEmojiPresentation(int codePoint)
	{
		if (codePoint < 0x231A || codePoint > 0x2B55)
			return false;
		foreach ((int first, int last) in EmojiPresentationRanges)
		{
			if (codePoint < first)
				return false;
			if (codePoint <= last)
				return true;
		}
		return false;
	}

	private static bool StartsEmoji(string text, int index)
	{
		char value = text[index];
		if (char.IsSurrogate(value))
			return true;
		if (index + 1 < text.Length && text[index + 1] is '\uFE0F' or '\u20E3' or '\u200D')
			return true;
		if (index + 2 < text.Length && text[index + 1] == '\uD83C' && text[index + 2] is >= '\uDFFB' and <= '\uDFFF')
			return true;
		return IsEmojiPresentation(value);
	}

	private static bool TryMapEmoji(ReadOnlySpan<char> cluster, IDictionary<int, ushort> primaryMap, EmojiTables emoji, out char mapped)
	{
		mapped = '\0';
		if (Rune.DecodeFromUtf16(cluster, out Rune first, out int firstLength) != OperationStatus.Done)
			return false;

		int codePoint = first.Value;
		if (!emoji.Starts.Contains(codePoint) || cluster.Contains('\uFE0E'))
			return false;
		if (codePoint < 0x80 && !cluster.Contains('\u20E3'))
			return false;
		if (cluster.Length == firstLength && codePoint < 0x10000 && !IsEmojiPresentation(codePoint) && primaryMap.ContainsKey(codePoint))
			return false;

		string key = cluster.ToString();
		if (emoji.Sequences.TryGetValue(key, out mapped))
			return true;
		return key.Contains('\uFE0F') && emoji.Sequences.TryGetValue(key.Replace("\uFE0F", "", StringComparison.Ordinal), out mapped);
	}

	private static TextRun? EmojiRun(string text, TextRunProperties properties, IDictionary<int, ushort> primaryMap)
	{
		EmojiTables? emoji = Emoji();
		if (emoji == null)
			return null;

		StringBuilder? translated = null;
		int consumed = 0;
		while (consumed < text.Length)
		{
			int cluster = StringInfo.GetNextTextElementLength(text.AsSpan(consumed));
			if (cluster <= 0 || !TryMapEmoji(text.AsSpan(consumed, cluster), primaryMap, emoji, out char mapped))
				break;
			translated ??= new StringBuilder(text.Length);
			translated.Append(mapped);
			translated.Append(EmojiPad, cluster - 1);
			consumed += cluster;
		}

		if (translated == null)
			return null;
		string value = translated.ToString();
		return new TextCharacters(value, 0, value.Length, new FallbackProperties(properties, emoji.Typeface));
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
		if (StartsEmoji(text, 0) || !primaryMap.ContainsKey(first))
		{
			TextRun? emoji = EmojiRun(text, properties, primaryMap);
			if (emoji != null)
				return emoji;
		}

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
			if (_emoji != null && _emoji.Starts.Contains(next) && StartsEmoji(text, length))
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
		if (IsPrivateUse(codePoint))
			return null;

		lock (Gate)
		{
			if (Lookups.TryGetValue((primary, codePoint), out Typeface? cached))
				return cached;
			if (Uncovered.Contains(codePoint))
				return null;

			Typeface? found = null;
			foreach (FontFamily family in Candidates())
			{
				bool? covers = Covers(family, codePoint);
				if (covers == false || (covers == null && !PreferredNames.Contains(family.Source)))
					continue;

				Typeface candidate = new(family, primary.Style, primary.Weight, primary.Stretch);
				if (candidate.TryGetGlyphTypeface(out GlyphTypeface glyphs) && glyphs.CharacterToGlyphMap.ContainsKey(codePoint))
				{
					found = candidate;
					break;
				}
			}

			if (found == null)
			{
				if (Uncovered.Count >= MaxUncoveredCodePoints)
					Uncovered.Clear();
				Uncovered.Add(codePoint);
			}

			if (Lookups.Count >= MaxCachedLookups)
				Lookups.Clear();
			Lookups[(primary, codePoint)] = found;
			return found;
		}
	}

	private static bool IsPrivateUse(int codePoint)
	{
		return codePoint is >= 0xE000 and <= 0xF8FF or >= 0xF0000;
	}

	private static bool? Covers(FontFamily family, int codePoint)
	{
#if CROSSPLAT
		try
		{
			Dictionary<string, ProGPU.Text.FontInfo>? files = _fontFiles;
			if (files == null)
			{
				files = new Dictionary<string, ProGPU.Text.FontInfo>(StringComparer.OrdinalIgnoreCase);
				foreach (ProGPU.Text.FontInfo font in ProGPU.Text.FontApi.GetSystemFonts())
				{
					if (string.IsNullOrWhiteSpace(font.FamilyName) || string.IsNullOrWhiteSpace(font.FilePath))
						continue;
					if (!files.TryGetValue(font.FamilyName, out ProGPU.Text.FontInfo? known) || IsRegularFace(font) && !IsRegularFace(known))
						files[font.FamilyName] = font;
				}
				_fontFiles = files;
			}

			return files.TryGetValue(family.Source, out ProGPU.Text.FontInfo? info)
				? ProGPU.Text.FontApi.ContainsGlyph(info, (uint)codePoint)
				: null;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxTextFallback", "Font coverage could not be read, checking fonts directly: " + ex.Message);
			_fontFiles = new Dictionary<string, ProGPU.Text.FontInfo>(StringComparer.OrdinalIgnoreCase);
			return null;
		}
#else
		return null;
#endif
	}

#if CROSSPLAT
	private static bool IsRegularFace(ProGPU.Text.FontInfo font)
	{
		return font.Weight == 400 && !font.IsItalic && font.Width == 5;
	}
#endif

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

	private sealed record EmojiTables(Typeface Typeface, Dictionary<string, char> Sequences, HashSet<int> Starts);

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
