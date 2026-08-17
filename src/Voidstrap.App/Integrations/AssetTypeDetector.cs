namespace Voidstrap.Integrations;

public static class AssetTypeDetector
{
	public static AssetTypeInfo Detect(byte[] data, int offset = 0)
	{
		if (data == null || data.Length <= offset + 4)
		{
			return AssetTypeInfo.Unknown;
		}
		if (Match(0, new byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 }))
		{
			return new AssetTypeInfo
			{
				Type = "PNG image",
				Category = "Image",
				Extension = ".png",
				IsImage = true
			};
		}
		if (Match(0, new byte[3] { 255, 216, 255 }))
		{
			return new AssetTypeInfo
			{
				Type = "JPEG image",
				Category = "Image",
				Extension = ".jpg",
				IsImage = true
			};
		}
		if (Match(0, new byte[4] { 71, 73, 70, 56 }))
		{
			return new AssetTypeInfo
			{
				Type = "GIF image",
				Category = "Image",
				Extension = ".gif",
				IsImage = true
			};
		}
		if (Match(0, new byte[2] { 66, 77 }))
		{
			return new AssetTypeInfo
			{
				Type = "Bitmap image",
				Category = "Image",
				Extension = ".bmp",
				IsImage = true
			};
		}
		if (Match(0, new byte[4] { 82, 73, 70, 70 }) && Match(8, new byte[4] { 87, 69, 66, 80 }))
		{
			return new AssetTypeInfo
			{
				Type = "WebP image",
				Category = "Image",
				Extension = ".webp",
				IsImage = true
			};
		}
		if (Match(0, new byte[4] { 171, 75, 84, 88 }) || Match(1, new byte[4] { 75, 84, 88, 32 }))
		{
			return new AssetTypeInfo
			{
				Type = "KTX texture",
				Category = "Image",
				Extension = ".ktx",
				IsImage = true
			};
		}
		if (Match(0, new byte[4] { 79, 103, 103, 83 }))
		{
			return new AssetTypeInfo
			{
				Type = "OGG audio",
				Category = "Audio",
				Extension = ".ogg",
				IsImage = false
			};
		}
		if (Match(0, new byte[3] { 73, 68, 51 }) || (data[offset] == byte.MaxValue && (data[offset + 1] & 0xE0) == 224))
		{
			return new AssetTypeInfo
			{
				Type = "MP3 audio",
				Category = "Audio",
				Extension = ".mp3",
				IsImage = false
			};
		}
		if (Match(0, new byte[8] { 118, 101, 114, 115, 105, 111, 110, 32 }))
		{
			return new AssetTypeInfo
			{
				Type = "Mesh",
				Category = "Mesh",
				Extension = ".mesh",
				IsImage = false,
				IsMesh = true
			};
		}
		if (Match(0, new byte[8] { 60, 114, 111, 98, 108, 111, 120, 33 }))
		{
			return new AssetTypeInfo
			{
				Type = "Model (binary)",
				Category = "Model",
				Extension = ".rbxm",
				IsImage = false
			};
		}
		if (Match(0, new byte[7] { 60, 114, 111, 98, 108, 111, 120 }))
		{
			return new AssetTypeInfo
			{
				Type = "Model (XML)",
				Category = "Model",
				Extension = ".rbxmx",
				IsImage = false
			};
		}
		if (data[offset] == 123 || data[offset] == 91)
		{
			return new AssetTypeInfo
			{
				Type = "JSON data",
				Category = "Data",
				Extension = ".json",
				IsImage = false
			};
		}
		return AssetTypeInfo.Unknown;
		bool Match(int at, params byte[] sig)
		{
			if (offset + at + sig.Length > data.Length)
			{
				return false;
			}
			for (int i = 0; i < sig.Length; i++)
			{
				if (data[offset + at + i] != sig[i])
				{
					return false;
				}
			}
			return true;
		}
	}
}
