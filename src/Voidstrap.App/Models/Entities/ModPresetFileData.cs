using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Voidstrap.Models.Entities;

public class ModPresetFileData
{
	private static readonly Dictionary<string, string[]> PreviousResourceHashes = new(StringComparer.Ordinal)
	{
		{ "Sounds.OldWalk.mp3", ["11DA1DDA2276B4A29315B0F91ED6DEB29D309E5FFE4FDE256A80B86534043E28"] }
	};

	public string FilePath { get; private set; }

	public string FullFilePath => Path.Combine(Paths.Mods, FilePath.Replace('\\', Path.DirectorySeparatorChar));

	public FileStream FileStream => File.OpenRead(FullFilePath);

	public string ResourceIdentifier { get; private set; }

	public Stream ResourceStream => Resource.GetStream(ResourceIdentifier);

	public byte[] ResourceHash { get; private set; }

	public bool IsAvailable { get; private set; }

	public ModPresetFileData(string contentPath, string resource)
	{
		FilePath = contentPath.Replace('\\', Path.DirectorySeparatorChar);
		ResourceIdentifier = resource;
		ResourceHash = Array.Empty<byte>();

		try
		{
			using Stream stream = ResourceStream;
			stream.Position = 0L;
			ResourceHash = App.ComputeSha256(stream);
			IsAvailable = true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ModPresetFileData", "Could not read the built in file " + resource + ": " + ex.Message);
		}
	}

	public bool HashMatches()
	{
		return ReadFileHash() is byte[] hash && hash.SequenceEqual(ResourceHash);
	}

	public bool MatchesPreviousVersion()
	{
		if (!PreviousResourceHashes.TryGetValue(ResourceIdentifier, out string[]? previous) || ReadFileHash() is not byte[] hash)
		{
			return false;
		}
		string hex = Convert.ToHexString(hash);
		return previous.Any(value => string.Equals(value, hex, StringComparison.OrdinalIgnoreCase));
	}

	private byte[]? ReadFileHash()
	{
		if (!IsAvailable || !File.Exists(FullFilePath))
		{
			return null;
		}

		try
		{
			using FileStream fileStream = FileStream;
			fileStream.Position = 0L;
			return App.ComputeSha256(fileStream);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ModPresetFileData", "Could not read " + FullFilePath + ": " + ex.Message);
			return null;
		}
	}
}
