using DiscordRPC;
using RichPresence = DiscordRPC.RichPresence;
using Assets = DiscordRPC.Assets;

namespace Voidstrap.Utility;

internal static class DiscordPresenceGuard
{
	public static void SetPresenceSafe(this DiscordRpcClient client, RichPresence? presence)
	{
		client.SetPresence(Complete(presence));
	}

	private static RichPresence? Complete(RichPresence? presence)
	{
		if (presence?.Assets == null)
			return presence;
		string large = presence.Assets.LargeImageKey ?? "";
		string small = presence.Assets.SmallImageKey ?? "";
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
