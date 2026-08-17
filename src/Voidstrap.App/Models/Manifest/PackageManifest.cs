using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Voidstrap.Models.Manifest;

public class PackageManifest : List<Package>
{
	public PackageManifest()
	{
	}

	public PackageManifest(string data)
	{
		if (string.IsNullOrWhiteSpace(data))
		{
			throw new InvalidDataException("Package manifest is empty");
		}
		using StringReader stringReader = new StringReader(data);
		string text = stringReader.ReadLine();
		if (text != "v0")
		{
			throw new NotSupportedException("Unexpected package manifest version: " + text + " (expected v0!)");
		}
		while (true)
		{
			string text2 = stringReader.ReadLine();
			if (text2 == null)
			{
				break;
			}
			if (text2.Length == 0)
			{
				continue;
			}
			if (text2 is "RobloxPlayerLauncher.exe" or "RobloxPlayerInstaller.exe")
			{
				break;
			}
			string text3 = stringReader.ReadLine();
			string text4 = stringReader.ReadLine();
			string text5 = stringReader.ReadLine();
			if (string.IsNullOrEmpty(text3) || string.IsNullOrEmpty(text4) || string.IsNullOrEmpty(text5))
			{
				throw new InvalidDataException("Package manifest ended in the middle of a package entry");
			}
			if (!string.Equals(Path.GetFileName(text2), text2, StringComparison.Ordinal) || text3.Length != 32 || !text3.All(Uri.IsHexDigit))
			{
				throw new InvalidDataException("Package manifest contains an invalid package identity");
			}
			if (!int.TryParse(text4, out int packedSize) || packedSize <= 0 || !int.TryParse(text5, out int size) || size <= 0)
			{
				throw new InvalidDataException("Package manifest contains an invalid package size");
			}
			if (this.Any(package => package.Name.Equals(text2, StringComparison.OrdinalIgnoreCase) || package.Signature.Equals(text3, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidDataException("Package manifest contains a duplicate package");
			}
			Add(new Package
			{
				Name = text2,
				Signature = text3,
				PackedSize = packedSize,
				Size = size
			});
		}
		if (Count == 0)
		{
			throw new InvalidDataException("Package manifest lists no packages");
		}
	}
}
