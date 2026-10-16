using System;

namespace Voidstrap.Core.AssetWarp;

public static class AssetTypeRemovalPolicy
{
	public static bool ShouldRemove(string typeId, string typeName, bool textures, bool decals, bool images, bool animations, bool meshes)
	{
		if (int.TryParse(typeId, out int numericType) && numericType > 0)
			return numericType switch
			{
				63 => textures,
				13 => decals,
				1 => images,
				24 => animations,
				40 or 4 => meshes,
				_ => false
			};
		bool textureMatch = textures && (typeName.Equals("texture", StringComparison.OrdinalIgnoreCase) || typeName.Equals("texturepack", StringComparison.OrdinalIgnoreCase));
		bool decalMatch = decals && typeName.Equals("decal", StringComparison.OrdinalIgnoreCase);
		bool imageMatch = images && typeName.Equals("image", StringComparison.OrdinalIgnoreCase);
		bool animationMatch = animations && typeName.Equals("animation", StringComparison.OrdinalIgnoreCase);
		bool meshMatch = meshes && (typeName.Equals("mesh", StringComparison.OrdinalIgnoreCase) || typeName.Equals("meshpart", StringComparison.OrdinalIgnoreCase));
		return textureMatch || decalMatch || imageMatch || animationMatch || meshMatch;
	}
}
