using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Voidstrap.RobloxInterfaces;

namespace Voidstrap.Models.Manifest;

public class FileManifest : List<ManifestFile>
{
	private FileManifest(string data)
	{
		using StringReader stringReader = new StringReader(data);
		while (true)
		{
			string? text = stringReader.ReadLine();
			if (text == null)
			{
				break;
			}
			string? text2 = stringReader.ReadLine();
			if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
			{
				throw new InvalidDataException("The file manifest is truncated or malformed");
			}
			Add(new ManifestFile
			{
				Name = text,
				Signature = text2
			});
		}
	}

	public static async Task<FileManifest> Get(string versionGuid)
	{
		IReadOnlyList<string> locations = Deployment.GetLocations("/" + versionGuid + "-rbxManifest.txt");
		System.Exception lastError = null;
		foreach (string location in locations)
		{
			try
			{
				return new FileManifest(await Voidstrap.Utility.Http.GetString(location).ConfigureAwait(false));
			}
			catch (System.Exception ex)
			{
				lastError = ex;
				App.Logger.WriteLine("FileManifest::Get", $"Manifest fetch failed from {location}: {ex.Message}");
			}
		}
		throw lastError ?? new System.Net.Http.HttpRequestException("Failed to fetch file manifest from any mirror.");
	}
}
