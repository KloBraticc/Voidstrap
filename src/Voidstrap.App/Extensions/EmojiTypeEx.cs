using System.Collections.Generic;
using Voidstrap.Enums;

namespace Voidstrap.Extensions;

internal static class EmojiTypeEx
{
	public static IReadOnlyDictionary<EmojiType, string> Filenames { get; } = new Dictionary<EmojiType, string>
	{
		{
			EmojiType.Catmoji,
			"Catmoji.ttf"
		},
		{
			EmojiType.Windows11,
			"Win1122H2SegoeUIEmoji.ttf"
		},
		{
			EmojiType.Windows10,
			"Win10April2018SegoeUIEmoji.ttf"
		},
		{
			EmojiType.Windows8,
			"Win8.1SegoeUIEmoji.ttf"
		}
	};

	public static IReadOnlyDictionary<EmojiType, string> Hashes { get; } = new Dictionary<EmojiType, string>
	{
		{
			EmojiType.Catmoji,
			"58D781FF4800AB10144A6FC0BB30479881F5048ECD57196148C9AB791FDAB622"
		},
		{
			EmojiType.Windows11,
			"4E3CEC7D1995B6D74102C0B4669E4507AC35CBF9A9830A93AC14C6E40DFE36A9"
		},
		{
			EmojiType.Windows10,
			"7C0244DD8EEB7C6BDECDFC3F9E59833527FC18A66D0295CE47339069692A2B4F"
		},
		{
			EmojiType.Windows8,
			"86BE288EED6561684BE645F671409210C914815E3833A0FC3B587CBF64C03928"
		}
	};

	public static IReadOnlyDictionary<EmojiType, long> Sizes { get; } = new Dictionary<EmojiType, long>
	{
		[EmojiType.Catmoji] = 1335828,
		[EmojiType.Windows11] = 2838884,
		[EmojiType.Windows10] = 2072388,
		[EmojiType.Windows8] = 676304
	};

	public static string GetHash(this EmojiType emojiType)
	{
		return Hashes[emojiType];
	}

	public static string GetUrl(this EmojiType emojiType)
	{
		if (emojiType == EmojiType.Default)
		{
			return "";
		}
		return "https://github.com/BloxstrapLabs/rbxcustom-fontemojis/releases/download/my-phone-is-78-percent/" + Filenames[emojiType];
	}
}
