using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voidstrap.Models;

namespace Voidstrap.Utility;

internal static class CustomFontMod
{
	private const string AssetId = "rbxasset://fonts/CustomFont.ttf";

	private static string FamiliesDirectory => Path.Combine(Paths.Mods, "content", "fonts", "families");

	public static void Apply(string sourceDirectory, string logIdent)
	{
		if (!File.Exists(Paths.CustomFont))
		{
			RemoveGeneratedFamilies();
			return;
		}

		Directory.CreateDirectory(FamiliesDirectory);
		foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*.json", SearchOption.TopDirectoryOnly))
		{
			string targetPath = Path.Combine(FamiliesDirectory, Path.GetFileName(sourcePath));
			if (File.Exists(targetPath))
			{
				continue;
			}

			try
			{
				FontFamily family = JsonFile.Deserialize<FontFamily>(sourcePath, JsonOptions.Tolerant, 4194304);
				bool changed = false;
				foreach (FontFace face in family.Faces)
				{
					if (!string.Equals(face.AssetId, AssetId, StringComparison.Ordinal))
					{
						face.AssetId = AssetId;
						changed = true;
					}
				}
				if (changed)
				{
					JsonFile.SerializeAtomic(targetPath, family, JsonOptions.Indented);
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(logIdent, "Skipping invalid font family JSON: " + Path.GetFileName(sourcePath) + ", " + ex.Message);
			}
		}
		App.Logger.WriteLine(logIdent, "Custom font applied.");
	}

	public static void RemoveGeneratedFamilies()
	{
		if (!Directory.Exists(FamiliesDirectory))
		{
			return;
		}

		foreach (string path in Directory.EnumerateFiles(FamiliesDirectory, "*.json", SearchOption.TopDirectoryOnly))
		{
			try
			{
				if (!IsGeneratedFamily(path))
				{
					continue;
				}
				Filesystem.AssertReadOnly(path);
				File.Delete(path);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("CustomFontMod::RemoveGeneratedFamilies", "Could not remove " + Path.GetFileName(path) + ": " + ex.Message);
			}
		}

		if (!Directory.EnumerateFileSystemEntries(FamiliesDirectory).Any())
		{
			Directory.Delete(FamiliesDirectory);
		}
	}

	public static IReadOnlyList<string> FindGeneratedFamilyNames(string directory)
	{
		if (!Directory.Exists(directory))
		{
			return [];
		}

		List<string> names = [];
		foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
		{
			try
			{
				if (IsGeneratedFamily(path))
				{
					names.Add(Path.GetFileName(path));
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("CustomFontMod::FindGeneratedFamilyNames", "Could not inspect " + Path.GetFileName(path) + ": " + ex.Message);
			}
		}
		return names;
	}

	private static bool IsGeneratedFamily(string path)
	{
		FontFamily family = JsonFile.Deserialize<FontFamily>(path, JsonOptions.Tolerant, 4194304);
		FontFace[] faces = family.Faces?.ToArray() ?? [];
		return faces.Length > 0 && faces.All(face => string.Equals(face.AssetId, AssetId, StringComparison.Ordinal));
	}
}
