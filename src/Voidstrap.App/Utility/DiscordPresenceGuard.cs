using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DiscordRPC;
using RichPresence = DiscordRPC.RichPresence;
using Assets = DiscordRPC.Assets;
using Button = DiscordRPC.Button;

namespace Voidstrap.Utility;

internal static class DiscordPresenceGuard
{
	public const int TextLimit = 128;

	private const int KeyLimit = 256;

	private const int LabelByteLimit = 31;

	private const string Ellipsis = "...";

	private const string Pad = "⠀";

	private const int UpdatesPerWindow = 4;

	private static readonly TimeSpan UpdateWindow = TimeSpan.FromSeconds(20);

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DiscordRpcClient, Queue<DateTime>> RecentUpdates = new();

	private static bool TryReserveUpdate(DiscordRpcClient client)
	{
		Queue<DateTime> recent = RecentUpdates.GetOrCreateValue(client);
		lock (recent)
		{
			DateTime now = DateTime.UtcNow;
			while (recent.Count > 0 && now - recent.Peek() >= UpdateWindow)
				recent.Dequeue();
			if (recent.Count >= UpdatesPerWindow)
				return false;
			recent.Enqueue(now);
			return true;
		}
	}

	public static bool SetPresenceSafe(this DiscordRpcClient client, RichPresence? presence)
	{
		try
		{
			if (client.IsDisposed || !TryReserveUpdate(client))
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
		if (presence == null)
			return null;
		RichPresence copy = presence.Clone();
		copy.Details = Render(presence.Details);
		copy.State = Render(presence.State);
		if (presence.Buttons is { Length: > 0 } buttons)
		{
			Button[] rendered = new Button[buttons.Length];
			for (int index = 0; index < buttons.Length; index++)
				rendered[index] = new Button { Label = Label(RpcText.Render(buttons[index].Label), "Open"), Url = buttons[index].Url };
			copy.Buttons = rendered;
		}
		if (presence.Assets == null)
			return copy;
		string large = Key(presence.Assets.LargeImageKey);
		string small = Key(presence.Assets.SmallImageKey);
		if (large.Length == 0 && small.Length == 0)
		{
			copy.Assets = null;
			return copy;
		}
		copy.Assets = new Assets
		{
			LargeImageKey = large.Length > 0 ? large : small,
			LargeImageText = Render(presence.Assets.LargeImageText) ?? "",
			SmallImageKey = small.Length > 0 ? small : large,
			SmallImageText = Render(presence.Assets.SmallImageText) ?? ""
		};
		return copy;
	}

	private static string? Render(string? value)
	{
		return value == null || !value.Contains('⟪') ? value : Text(RpcText.Render(value));
	}
}
