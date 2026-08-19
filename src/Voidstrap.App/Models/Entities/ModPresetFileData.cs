using System;
using System.IO;

namespace Voidstrap.Models.Entities;

public class ModPresetFileData
{
	public string FilePath { get; private set; }

	public string FullFilePath => Path.Combine(Paths.Mods, FilePath);

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
		if (!IsAvailable || !File.Exists(FullFilePath))
		{
			return false;
		}

		try
		{
			using FileStream fileStream = FileStream;
			fileStream.Position = 0L;
			return App.ComputeSha256(fileStream).SequenceEqual(ResourceHash);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ModPresetFileData", "Could not read " + FullFilePath + ": " + ex.Message);
			return false;
		}
	}
}
