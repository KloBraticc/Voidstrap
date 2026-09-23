using System.Globalization;
using System.Text;
using DiscordRPC;
using RichPresence = DiscordRPC.RichPresence;
using Assets = DiscordRPC.Assets;

namespace Voidstrap.Utility;

internal static class DiscordPresenceGuard
{
	public const int TextLimit = 128;

	private const int KeyLimit = 256;

	private const int LabelByteLimit = 31;

	private const string Ellipsis = "...";

	private const string Pad = "⠀";

	public static bool SetPresenceSafe(this DiscordRpcClient client, RichPresence? presence)
	{
		try
		{
			if (client.IsDisposed)
				return false;
			client.SetPresence(Complete(presence));
			return true;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			App.Logger?.WriteLine("DiscordPresenceGuard", "Discord did not accept the presence: " + ex.Message);
			return false;
		}
	}

	public static string Text(string? value, int limit = TextLimit)
	{
		string text = string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		if (text.Length > limit)
		{
			int budget = limit - Ellipsis.Length;
			int cut = 0;
			foreach (int start in StringInfo.ParseCombiningCharacters(text))
			{
				if (start > budget)
					break;
				cut = start;
			}
			text = text[..cut].TrimEnd() + Ellipsis;
		}
		return text.Length == 1 ? text + Pad : text;
	}

	public static string Label(string? value, string fallback)
	{
		string text = Text(value, LabelByteLimit);
		if (text.Length == 0)
			text = fallback;
		while (Encoding.UTF8.GetByteCount(text) > LabelByteLimit)
		{
			int[] starts = StringInfo.ParseCombiningCharacters(text);
			text = text[..starts[^1]].TrimEnd();
		}
		return text;
	}

	public static string Key(string? value)
	{
		string key = (value ?? "").Trim();
		return key.Length <= KeyLimit ? key : "";
	}

	private static RichPresence? Complete(RichPresence? presence)
	{
		if (presence?.Assets == null)
			return presence;
		string large = Key(presence.Assets.LargeImageKey);
		string small = Key(presence.Assets.SmallImageKey);
		if (large.Length > 0 && small.Length > 0)
			return presence;
		RichPresence copy = presence.Clone();
		if (large.Length == 0 && small.Length == 0)
		{
			copy.Assets = null;
			return copy;
		}
		copy.Assets = new Assets
		{
			LargeImageKey = large.Length > 0 ? large : small,
			LargeImageText = presence.Assets.LargeImageText ?? "",
			SmallImageKey = small.Length > 0 ? small : large,
			SmallImageText = presence.Assets.SmallImageText ?? ""
		};
		return copy;
	}
}
