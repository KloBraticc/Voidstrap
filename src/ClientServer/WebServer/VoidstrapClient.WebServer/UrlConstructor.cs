using System.Collections.Generic;
using VoidstrapClient.Common;
using VoidstrapClient.Common.Enums;
using VoidstrapClient.Common.Models;
using VoidstrapClient.WebServer.Enums;

namespace VoidstrapClient.WebServer;

internal static class UrlConstructor
{
	private static OutfitManager? _outfitManager;

	private static Character GetCharacter()
	{
		if (_outfitManager == null)
		{
			_outfitManager = new OutfitManager(Config.Instance.User.OutfitPreferences);
		}
		Outfit preferredOutfitForClient = _outfitManager.GetPreferredOutfitForClient(Config.Instance.Client.ClientName);
		if (preferredOutfitForClient != null)
		{
			return preferredOutfitForClient.Character;
		}
		return Config.Instance.User.Character;
	}

	private static List<ulong> GetCompatibleAvatarItems()
	{
		Character character = GetCharacter();
		List<ulong> list = new List<ulong>();
		foreach (KeyValuePair<AvatarSlot, ulong> item in character.Equipped)
		{
			if (CharacterCompatibility.IsCompatible(item.Key))
			{
				list.Add(item.Value);
			}
		}
		return list;
	}

	private static string GetCharacterAppearanceFetchUrl()
	{
		Character character = GetCharacter();
		if (Config.Instance.Client.CharacterCompatibility.FigureBodyColours)
		{
			return $"http://www.roblox.com/asset/characterfetchfigure.ashx?figureType={(int)character.FigureCharacterType}";
		}
		List<ulong> compatibleAvatarItems = GetCompatibleAvatarItems();
		string arg = string.Join(',', compatibleAvatarItems);
		string arg2 = $"{character.Head},{character.LeftArm},{character.RightArm},{character.LeftLeg},{character.RightLeg},{character.Torso}";
		return $"http://www.roblox.com/asset/characterfetchlist.ashx?items={arg}&colors={arg2}";
	}

	private static string GetCharacterAppearanceWhole()
	{
		string text = "";
		Character character = GetCharacter();
		foreach (KeyValuePair<AvatarSlot, ulong> item in character.Equipped)
		{
			AvatarSlot key = item.Key;
			ulong value = item.Value;
			if (CharacterCompatibility.IsCompatible(key))
			{
				int value2 = AvatarItems.GetById(value)?.AssetVersion ?? 0;
				text += $"http://www.roblox.com/asset/?id={value}&version={value2};";
			}
		}
		if (!Config.Instance.Client.CharacterCompatibility.FigureBodyColours)
		{
			return text + $"http://www.roblox.com/asset/bodycolorslist.ashx?colors={character.Head},{character.LeftArm},{character.RightArm},{character.LeftLeg},{character.RightLeg},{character.Torso}";
		}
		return text + $"http://www.roblox.com/asset/bodycolorsfigure.ashx?figureType={(int)character.FigureCharacterType}";
	}

	public static string GetCharacterAppearanceUrl()
	{
		if (Config.Instance.Client.CharacterLoadType != CharacterLoadType.Fetch)
		{
			return GetCharacterAppearanceWhole();
		}
		return GetCharacterAppearanceFetchUrl();
	}
}
