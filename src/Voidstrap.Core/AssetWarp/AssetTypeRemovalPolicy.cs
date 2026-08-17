using System;

namespace Voidstrap.Core.AssetWarp;

public static class AssetTypeRemovalPolicy
{
	public static bool ShouldRemove(string typeId, string typeName, bool textures, bool decals, bool images, bool animations, bool meshes)
	{
		bool textureMatch = textures && (typeId == "63" || typeName.Equals("texture", StringComparison.OrdinalIgnoreCase) || typeName.Equals("texturepack", StringComparison.OrdinalIgnoreCase));
		bool decalMatch = decals && (typeId == "13" || typeName.Equals("decal", StringComparison.OrdinalIgnoreCase));
		bool imageMatch = images && (typeId == "1" || typeName.Equals("image", StringComparison.OrdinalIgnoreCase));
		bool animationMatch = animations && (typeId == "24" || typeName.Equals("animation", StringComparison.OrdinalIgnoreCase));
		bool meshMatch = meshes && (typeId == "40" || typeId == "4" || typeName.Equals("mesh", StringComparison.OrdinalIgnoreCase) || typeName.Equals("meshpart", StringComparison.OrdinalIgnoreCase));
		return textureMatch || decalMatch || imageMatch || animationMatch || meshMatch;
	}
}
