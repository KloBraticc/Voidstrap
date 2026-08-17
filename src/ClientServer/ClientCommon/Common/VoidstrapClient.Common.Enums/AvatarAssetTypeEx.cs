using System;

namespace VoidstrapClient.Common.Enums;

public static class AvatarAssetTypeEx
{
	public static bool IsSlotValid(this AvatarAssetType type, AvatarSlot slot)
	{
		switch (type)
		{
		case AvatarAssetType.TShirt:
			return slot == AvatarSlot.TShirt;
		case AvatarAssetType.Shirt:
			return slot == AvatarSlot.Shirt;
		case AvatarAssetType.Pants:
			return slot == AvatarSlot.Pants;
		case AvatarAssetType.Hat:
			if ((uint)(slot - 3) <= 2u)
			{
				return true;
			}
			return false;
		case AvatarAssetType.Face:
			return slot == AvatarSlot.Face;
		case AvatarAssetType.Head:
			return slot == AvatarSlot.Head;
		case AvatarAssetType.LeftArm:
			return slot == AvatarSlot.LeftArm;
		case AvatarAssetType.RightArm:
			return slot == AvatarSlot.RightArm;
		case AvatarAssetType.LeftLeg:
			return slot == AvatarSlot.LeftLeg;
		case AvatarAssetType.RightLeg:
			return slot == AvatarSlot.RightLeg;
		case AvatarAssetType.Torso:
			return slot == AvatarSlot.Torso;
		case AvatarAssetType.Package:
			return false;
		case AvatarAssetType.Gear:
			return slot == AvatarSlot.Gear;
		default:
			throw new Exception($"Unhandled avatar asset type {type}");
		}
	}

	public static bool IsShownOnCatalog(this AvatarAssetType type)
	{
		if (type != AvatarAssetType.LeftArm && type != AvatarAssetType.RightArm && type != AvatarAssetType.LeftLeg && type != AvatarAssetType.RightLeg)
		{
			return type != AvatarAssetType.Torso;
		}
		return false;
	}

	public static bool CanHaveCustomAssets(this AvatarAssetType type)
	{
		if ((uint)type <= 2u)
		{
			return true;
		}
		return false;
	}

	public static AvatarSlot ConvertToAvatarSlot(this AvatarAssetType type)
	{
		return type switch
		{
			AvatarAssetType.TShirt => AvatarSlot.TShirt, 
			AvatarAssetType.Shirt => AvatarSlot.Shirt, 
			AvatarAssetType.Pants => AvatarSlot.Pants, 
			AvatarAssetType.Hat => AvatarSlot.Hat1, 
			AvatarAssetType.Face => AvatarSlot.Face, 
			AvatarAssetType.Head => AvatarSlot.Head, 
			AvatarAssetType.Torso => AvatarSlot.Torso, 
			AvatarAssetType.LeftArm => AvatarSlot.LeftArm, 
			AvatarAssetType.RightArm => AvatarSlot.RightArm, 
			AvatarAssetType.LeftLeg => AvatarSlot.LeftLeg, 
			AvatarAssetType.RightLeg => AvatarSlot.RightLeg, 
			AvatarAssetType.Gear => AvatarSlot.Gear, 
			_ => throw new Exception("Unknown avatar asset type"), 
		};
	}
}
