using System;

namespace VoidstrapClient.Common.Enums;

public static class AvatarSlotEx
{
	public static bool IsHat(this AvatarSlot slot)
	{
		if ((uint)(slot - 3) <= 2u)
		{
			return true;
		}
		return false;
	}

	public static AvatarAssetType ConvertToAvatarAssetType(this AvatarSlot slot)
	{
		return slot switch
		{
			AvatarSlot.TShirt => AvatarAssetType.TShirt, 
			AvatarSlot.Shirt => AvatarAssetType.Shirt, 
			AvatarSlot.Pants => AvatarAssetType.Pants, 
			AvatarSlot.Hat1 => AvatarAssetType.Hat, 
			AvatarSlot.Hat2 => AvatarAssetType.Hat, 
			AvatarSlot.Hat3 => AvatarAssetType.Hat, 
			AvatarSlot.Face => AvatarAssetType.Face, 
			AvatarSlot.Head => AvatarAssetType.Head, 
			AvatarSlot.Torso => AvatarAssetType.Torso, 
			AvatarSlot.LeftArm => AvatarAssetType.LeftArm, 
			AvatarSlot.RightArm => AvatarAssetType.RightArm, 
			AvatarSlot.LeftLeg => AvatarAssetType.LeftLeg, 
			AvatarSlot.RightLeg => AvatarAssetType.RightLeg, 
			AvatarSlot.Gear => AvatarAssetType.Gear, 
			_ => throw new Exception($"Unknown avatar slot {slot}"), 
		};
	}
}
