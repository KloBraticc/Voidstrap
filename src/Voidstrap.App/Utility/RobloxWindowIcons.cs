using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Voidstrap.Models;

namespace Voidstrap.Utility;

internal static class RobloxWindowIcons
{
	public const long MaxIconBytes = 8L * 1024 * 1024;

	public static readonly string[] AllowedExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".ico"];

	public static string Folder => Path.Combine(Paths.Data, "WindowIcons");

	public static bool HasOverrides
	{
		get
		{
			List<RobloxWindowIconOverride>? list = App.Settings.Prop.RobloxWindowIconOverrides;
			return list != null && list.Any(entry => entry != null && File.Exists(entry.IconPath));
		}
	}

	public static string? Resolve(long placeId, long universeId)
	{
		List<RobloxWindowIconOverride>? list = App.Settings.Prop.RobloxWindowIconOverrides;
		if (list == null || list.Count == 0)
			return null;
		RobloxWindowIconOverride? exact =
			placeId > 0 ? list.FirstOrDefault(entry => entry != null && entry.PlaceId == placeId && File.Exists(entry.IconPath)) : null;
		if (exact != null)
			return exact.IconPath;
		RobloxWindowIconOverride? whole =
			universeId > 0
				? list.FirstOrDefault(entry =>
					entry != null && entry.AllPlaces && entry.UniverseId == universeId && File.Exists(entry.IconPath)
				)
				: null;
		return whole?.IconPath;
	}

	public static byte[]? ReadIcon(string path)
	{
		try
		{
			FileInfo info = new(path);
			if (!info.Exists || info.Length <= 0 || info.Length > MaxIconBytes)
				return null;
			return File.ReadAllBytes(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine("RobloxWindowIcons", "The custom game icon could not be read: " + ex.Message);
			return null;
		}
	}

	public static string Import(string sourcePath, long placeId)
	{
		string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
		if (!AllowedExtensions.Contains(extension))
			throw new InvalidDataException(Voidstrap.Resources.Strings.Menu_Shortcuts_InvalidIcon);
		FileInfo info = new(sourcePath);
		if (!info.Exists || info.Length <= 0 || info.Length > MaxIconBytes)
			throw new InvalidDataException(Voidstrap.Resources.Strings.Menu_Shortcuts_InvalidIcon);
		Directory.CreateDirectory(Folder);
		string target = Path.Combine(
			Folder,
			placeId.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8] + extension
		);
		File.Copy(sourcePath, target, false);
		return target;
	}

	public static void TryDelete(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return;
		try
		{
			string full = Path.GetFullPath(path);
			string folder = Path.GetFullPath(Folder) + Path.DirectorySeparatorChar;
			if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
				File.Delete(full);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
	}
}
