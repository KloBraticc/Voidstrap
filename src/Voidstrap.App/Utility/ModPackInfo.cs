using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voidstrap.Utility;

public sealed class ModPackInfo
{
	public const string FileName = "ModPack.lock";

	private const string LogIdent = "ModPackInfo";

	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public string Source { get; set; } = "";

	public long Id { get; set; }

	public string Slug { get; set; } = "";

	public string Name { get; set; } = "";

	public string Author { get; set; } = "";

	public string IconUrl { get; set; } = "";

	public string ProfileUrl { get; set; } = "";

	public string Category { get; set; } = "";

	public string InstallKind { get; set; } = "";

	public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;

	public static string GetPath(string modFolder)
	{
		return Path.Combine(modFolder, FileName);
	}

	public static void Write(string modFolder, ModPackInfo info)
	{
		try
		{
			Directory.CreateDirectory(modFolder);
			File.WriteAllText(GetPath(modFolder), JsonSerializer.Serialize(info, Options), new UTF8Encoding(false));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The mod pack details could not be saved: " + ex.Message);
		}
	}

	public static ModPackInfo? Read(string modFolder)
	{
		string path = GetPath(modFolder);
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			FileInfo info = new(path);
			if (info.Length <= 0 || info.Length > 64 * 1024)
			{
				return null;
			}
			return JsonSerializer.Deserialize<ModPackInfo>(File.ReadAllText(path), Options);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The mod pack details could not be read: " + ex.Message);
			return null;
		}
	}
}
