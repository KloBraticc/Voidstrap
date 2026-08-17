using VoidstrapClient.Common.Enums;

namespace VoidstrapClient.WebServer;

internal static class CharacterCompatibility
{
	public static bool IsCompatible(AvatarSlot avatarSlot)
	{
		switch (avatarSlot)
		{
		case AvatarSlot.TShirt:
			return Config.Instance.Client.CharacterCompatibility.TShirts;
		case AvatarSlot.Hat1:
		case AvatarSlot.Hat2:
		case AvatarSlot.Hat3:
			return Config.Instance.Client.CharacterCompatibility.Hats;
		case AvatarSlot.Shirt:
		case AvatarSlot.Pants:
			return Config.Instance.Client.CharacterCompatibility.ShirtsAndPants;
		case AvatarSlot.Face:
			return Config.Instance.Client.CharacterCompatibility.Faces;
		case AvatarSlot.Head:
			return Config.Instance.Client.CharacterCompatibility.Heads;
		case AvatarSlot.Torso:
		case AvatarSlot.LeftArm:
		case AvatarSlot.RightArm:
		case AvatarSlot.LeftLeg:
		case AvatarSlot.RightLeg:
			return Config.Instance.Client.CharacterCompatibility.BodyParts;
		case AvatarSlot.Gear:
			return Config.Instance.IsRenderMode;
		default:
			return true;
		}
	}

	public static bool IsCompatible(AvatarAssetType avatarAssetType)
	{
		if (avatarAssetType == AvatarAssetType.Package)
		{
			return false;
		}
		return IsCompatible(avatarAssetType.ConvertToAvatarSlot());
	}
}
