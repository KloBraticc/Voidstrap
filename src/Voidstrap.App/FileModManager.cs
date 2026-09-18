using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp.Processing;
using Voidstrap.Utility;

namespace Voidstrap;

public static class FileModManager
{
	private const string StudsFile = "PlatformContent/pc/textures/studs.dds";

	private const string LegacyBackupSuffix = ".bak2";

	private const int KeptOriginalVersions = 4;

	private const long MaximumImageBytes = 64L * 1024 * 1024;

	private static readonly object Gate = new object();

	private static int _applyQueued;

	private static readonly string[] ParticleFiles =
	[
		"content/textures/particles/common_alpha.dds",
		"content/textures/particles/explosion01_core_alpha.png",
		"content/textures/particles/explosion01_core_main.dds",
		"content/textures/particles/explosion01_implosion_color.png",
		"content/textures/particles/explosion01_implosion_main.dds",
		"content/textures/particles/explosion01_shockwave_main.dds",
		"content/textures/particles/explosion01_smoke_alpha.dds",
		"content/textures/particles/explosion01_smoke_color_new.dds",
		"content/textures/particles/explosion01_smoke_main.dds",
		"content/textures/particles/explosion_alpha.dds",
		"content/textures/particles/explosion_color.dds",
		"content/textures/particles/fire_alpha.dds",
		"content/textures/particles/fire_color.dds",
		"content/textures/particles/fire_main.dds",
		"content/textures/particles/fire_sparks_color.dds",
		"content/textures/particles/fire_sparks_main.dds",
		"content/textures/particles/forcefield_alpha.dds",
		"content/textures/particles/forcefield_glow_alpha.dds",
		"content/textures/particles/forcefield_glow_color.dds",
		"content/textures/particles/forcefield_glow_main.dds",
		"content/textures/particles/forcefield_vortex_color.dds",
		"content/textures/particles/forcefield_vortex_main.dds",
		"content/textures/particles/legacy_fire_alpha_color.dds",
		"content/textures/particles/smoke_color.dds",
		"content/textures/particles/smoke_main.dds",
		"content/textures/particles/sparkles_color.dds",
		"content/textures/particles/sparkles_main.dds"
	];

	private static readonly string[] TerrainFiles =
	[
		"PlatformContent/pc/terrain/materials.json",
		"PlatformContent/pc/terrain/materials2022.json"
	];

	private static readonly string[] WaterFiles = Enumerable.Range(1, DdsTextureCodec.WaterFrameCount)
		.Select(static index => "PlatformContent/pc/textures/water/normal_" + index.ToString("00", CultureInfo.InvariantCulture) + ".dds")
		.ToArray();

	private static readonly string[] TextureKeys = ["texture", "texture_top", "texture_side", "texture_bottom"];

	private static readonly string[] RetiredFiles =
	[
		"PlatformContent/pc/textures/brdfLUT.dds",
		"PlatformContent/pc/textures/wangIndex.dds"
	];

	private static readonly string[] LegacyPlaceholderFiles =
	[
		StudsFile,
		.. RetiredFiles,
		"content/textures/particles/fire_main.dds",
		"content/textures/particles/explosion01_core_main.dds",
		"content/textures/particles/explosion01_implosion_main.dds",
		"content/textures/particles/explosion01_shockwave_main.dds",
		"content/textures/particles/explosion01_smoke_main.dds",
		"content/textures/particles/forcefield_glow_main.dds",
		"content/textures/particles/forcefield_vortex_main.dds",
		"content/textures/particles/smoke_main.dds",
		"content/textures/particles/sparkles_main.dds",
		"content/textures/particles/fire_sparks_main.dds"
	];

	private static string StateFolder => Path.Combine(Paths.Data, "FileMods");

	private static string OwnedPath => Path.Combine(StateFolder, "Owned.json");

	private static string OriginalsFolder => Path.Combine(StateFolder, "Originals");

	private static string WaterFramesFolder => Path.Combine(StateFolder, "WaterFrames");

	public static void QueueApplyFromSettings()
	{
		if (Interlocked.Exchange(ref _applyQueued, 1) == 1)
			return;
		_ = Task.Run(async () =>
		{
			await Task.Delay(350).ConfigureAwait(false);
			Interlocked.Exchange(ref _applyQueued, 0);
			ApplyFromSettings();
		});
	}

	public static void ApplyFromSettings()
	{
		(string? directory, string? guid) = FindPlayerVersion();
		ApplyFromSettings(directory, guid);
	}

	public static byte[][]? BuildWaterPreview(int style, int strength, int speed, int frozenFrame, int glitch)
	{
		try
		{
			style = Math.Clamp(style, 0, 5);
			List<byte[]>? originals = null;
			if (style is 0 or 1 or 3 or 4)
			{
				(string? directory, string? guid) = FindPlayerVersion();
				originals = new List<byte[]>(WaterFiles.Length);
				foreach (string rel in WaterFiles)
				{
					byte[]? original = PeekOriginal(directory, guid, rel);
					if (original == null)
						return null;
					originals.Add(original);
				}
			}
			List<byte[]>? heights = null;
			if (style == 5)
			{
				lock (Gate)
					heights = LoadWaterFrames();
				if (heights.Count == 0)
					return null;
			}
			byte[][] frames = style == 0 ? originals!.ToArray() : DdsTextureCodec.BuildWaterFrames(style, strength, speed, frozenFrame, glitch, originals, heights);
			Dictionary<byte[], byte[]> composed = new Dictionary<byte[], byte[]>(ReferenceEqualityComparer.Instance);
			byte[][] previews = new byte[frames.Length][];
			for (int index = 0; index < frames.Length; index++)
			{
				if (!composed.TryGetValue(frames[index], out byte[]? preview))
				{
					preview = DdsTextureCodec.ComposeWaterPreview(frames[index]);
					composed[frames[index]] = preview;
				}
				previews[index] = preview;
			}
			return previews;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::BuildWaterPreview", "Could not build the water preview: " + ex.Message);
			return null;
		}
	}

	private static (string? Directory, string? Guid) FindPlayerVersion()
	{
		try
		{
			Voidstrap.AppData.RobloxPlayerData player = new Voidstrap.AppData.RobloxPlayerData();
			RobloxInstallCompression.EnsureExtracted(player);
			string playerGuid = player.State.VersionGuid;
			if (Voidstrap.AppData.CommonAppData.IsVersionGuidValid(playerGuid) && Directory.Exists(player.Directory))
				return (player.Directory, playerGuid);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::FindPlayerVersion", "Could not find the Roblox install: " + ex.Message);
		}
		return (null, null);
	}

	private static byte[]? PeekOriginal(string? versionDirectory, string? versionGuid, string rel)
	{
		if (string.IsNullOrEmpty(versionDirectory) || versionGuid is null || !Voidstrap.AppData.CommonAppData.IsVersionGuidValid(versionGuid))
			return null;
		string cache = Path.Combine(OriginalsFolder, versionGuid, LocalPath(rel));
		if (File.Exists(cache))
			return File.ReadAllBytes(cache);
		string live = Path.Combine(versionDirectory, LocalPath(rel));
		if (!File.Exists(live))
			return null;
		byte[] bytes = File.ReadAllBytes(live);
		string modsFile = ModsPath(rel);
		return File.Exists(modsFile) && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(modsFile)) ? null : bytes;
	}

	public static void ApplyFromSettings(string? versionDirectory, string? versionGuid)
	{
		lock (Gate)
		{
			try
			{
				bool firstRun = !File.Exists(OwnedPath);
				Dictionary<string, string> owned = LoadOwned();
				RestoreLegacyBackups();
				AdoptLegacyFiles(owned, firstRun);
				var settings = App.Settings.Prop;
				Sync(owned, StudsFile, settings.TextureStudStyle == 1 ? DdsTextureCodec.CreateFlatStuds(settings.TextureStudShade) : null);
				byte alpha = DdsTextureCodec.PercentToByte(settings.TextureParticleOpacity);
				byte[]? particleDds = settings.TextureParticleStyle switch
				{
					1 => DdsTextureCodec.CreateDds("DXT5"u8, new byte[16]),
					2 => DdsTextureCodec.CreateDds("DXT5"u8, [alpha, alpha, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0]),
					_ => null
				};
				byte[]? particlePng = settings.TextureParticleStyle switch
				{
					1 => CreatePng(0, 0),
					2 => CreatePng(255, alpha),
					_ => null
				};
				foreach (string rel in ParticleFiles)
					Sync(owned, rel, rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? particlePng : particleDds);
				foreach (string rel in TerrainFiles)
				{
					if (!settings.TextureTerrainEnabled)
					{
						Sync(owned, rel, null);
						continue;
					}
					byte[]? original = GetOriginal(owned, versionDirectory, versionGuid, rel);
					byte[]? patched = original == null ? null : TryPatchTerrain(original, rel, Math.Clamp(settings.TextureTerrainSize, 0.05, 1), Math.Clamp(settings.TextureTerrainRepeat, 1, 10));
					if (patched != null)
						Sync(owned, rel, patched);
				}
				ApplyWater(owned, versionDirectory, versionGuid, settings.TextureWaterStyle, settings.TextureWaterStrength, settings.TextureWaterSpeed, settings.TextureWaterFrame, settings.TextureWaterGlitch);
				if (versionGuid is not null && Voidstrap.AppData.CommonAppData.IsVersionGuidValid(versionGuid))
					PruneOriginals(versionGuid);
				SaveOwned(owned);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteException("FileModManager::ApplyFromSettings", ex);
			}
		}
	}

	public static int CountWaterFrames()
	{
		try
		{
			return Directory.Exists(WaterFramesFolder) ? Math.Min(Directory.GetFiles(WaterFramesFolder, "frame_*.png").Length, DdsTextureCodec.WaterFrameCount) : 0;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::CountWaterFrames", "Could not read the water images: " + ex.Message);
			return 0;
		}
	}

	public static int ImportWaterFrames(IReadOnlyList<string> paths)
	{
		List<byte[]> frames = new List<byte[]>();
		foreach (string path in paths)
		{
			if (frames.Count >= DdsTextureCodec.WaterFrameCount)
				break;
			FileInfo file = new FileInfo(path);
			if (!file.Exists || file.Length == 0 || file.Length > MaximumImageBytes)
				throw new InvalidDataException("Choose images smaller than 64 MB.");
			byte[] bytes = File.ReadAllBytes(path);
			try
			{
				SixLabors.ImageSharp.ImageInfo info = SixLabors.ImageSharp.Image.Identify(bytes);
				if (info.Width <= 0 || info.Height <= 0 || info.Width > 4096 || info.Height > 4096)
					throw new InvalidDataException("Use images that are 4096 pixels or smaller on each side.");
				SixLabors.ImageSharp.Formats.DecoderOptions options = new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = (uint)DdsTextureCodec.WaterFrameCount };
				using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.L8> image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.L8>(options, bytes);
				image.Mutate(static context => context.Resize(DdsTextureCodec.WaterSize, DdsTextureCodec.WaterSize));
				for (int index = 0; index < image.Frames.Count && frames.Count < DdsTextureCodec.WaterFrameCount; index++)
				{
					using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.L8> single = image.Frames.CloneFrame(index);
					byte[] heights = new byte[DdsTextureCodec.WaterSize * DdsTextureCodec.WaterSize];
					single.CopyPixelDataTo(heights);
					frames.Add(heights);
				}
			}
			catch (SixLabors.ImageSharp.UnknownImageFormatException)
			{
				throw new InvalidDataException(Path.GetFileName(path) + " is not an image format that can be used.");
			}
		}
		if (frames.Count == 0)
			throw new InvalidDataException("No images were chosen.");
		lock (Gate)
		{
			ClearWaterFramesCore();
			Directory.CreateDirectory(WaterFramesFolder);
			for (int index = 0; index < frames.Count; index++)
			{
				using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.L8> image = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.L8>(frames[index], DdsTextureCodec.WaterSize, DdsTextureCodec.WaterSize);
				using FileStream output = File.Create(Path.Combine(WaterFramesFolder, "frame_" + index.ToString("00", CultureInfo.InvariantCulture) + ".png"));
				image.Save(output, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
			}
		}
		return frames.Count;
	}

	public static void ClearWaterFrames()
	{
		lock (Gate)
			ClearWaterFramesCore();
	}

	private static void ClearWaterFramesCore()
	{
		if (!Directory.Exists(WaterFramesFolder))
			return;
		foreach (string file in Directory.GetFiles(WaterFramesFolder, "frame_*.png"))
			Filesystem.DeleteWritableFile(file);
	}

	private static void ApplyWater(Dictionary<string, string> owned, string? versionDirectory, string? versionGuid, int style, int strength, int speed, int frozenFrame, int glitch)
	{
		style = Math.Clamp(style, 0, 5);
		if (style == 0 || (style == 5 && CountWaterFrames() == 0))
		{
			foreach (string rel in WaterFiles)
				Sync(owned, rel, null);
			return;
		}
		try
		{
			List<byte[]>? originals = null;
			if (style is 1 or 3 or 4)
			{
				originals = new List<byte[]>(WaterFiles.Length);
				foreach (string rel in WaterFiles)
				{
					byte[]? original = GetOriginal(owned, versionDirectory, versionGuid, rel);
					if (original == null)
						return;
					originals.Add(original);
				}
			}
			List<byte[]>? heights = style == 5 ? LoadWaterFrames() : null;
			byte[][] frames = DdsTextureCodec.BuildWaterFrames(style, strength, speed, frozenFrame, glitch, originals, heights);
			for (int index = 0; index < WaterFiles.Length; index++)
				Sync(owned, WaterFiles[index], frames[index]);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::ApplyWater", "Could not build the water textures: " + ex.Message);
		}
	}

	private static List<byte[]> LoadWaterFrames()
	{
		List<byte[]> frames = new List<byte[]>();
		if (!Directory.Exists(WaterFramesFolder))
			return frames;
		foreach (string file in Directory.GetFiles(WaterFramesFolder, "frame_*.png").OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).Take(DdsTextureCodec.WaterFrameCount))
		{
			using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.L8> image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.L8>(file);
			if (image.Width != DdsTextureCodec.WaterSize || image.Height != DdsTextureCodec.WaterSize)
				image.Mutate(static context => context.Resize(DdsTextureCodec.WaterSize, DdsTextureCodec.WaterSize));
			byte[] heights = new byte[DdsTextureCodec.WaterSize * DdsTextureCodec.WaterSize];
			image.CopyPixelDataTo(heights);
			frames.Add(heights);
		}
		return frames;
	}

	internal static byte[] PatchTerrain(byte[] original, double scale, double detiling)
	{
		ReadOnlySpan<byte> json = original;
		if (json.StartsWith("﻿"u8))
			json = json[3..];
		if (JsonNode.Parse(json) is not JsonObject root || root["materials"] is not JsonArray materials)
			throw new InvalidDataException("The terrain materials file has an unexpected layout.");
		double legacy = Math.Round(1.0 / detiling, 4);
		foreach (JsonNode? node in materials)
		{
			if (node is not JsonObject material)
				continue;
			foreach (string key in TextureKeys)
			{
				if (material[key] is not JsonObject texture)
					continue;
				if (texture["tiling"] is JsonValue tiling && tiling.TryGetValue(out double value))
					texture["tiling"] = Math.Max(0.001, Math.Round(value * scale, 4));
				if (texture.ContainsKey("detiling"))
					texture["detiling"] = detiling;
				if (texture.ContainsKey("detiling_legacy"))
					texture["detiling_legacy"] = legacy;
			}
		}
		return Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions.Indented));
	}

	private static byte[]? TryPatchTerrain(byte[] original, string rel, double scale, double detiling)
	{
		try
		{
			return PatchTerrain(original, scale, detiling);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::TryPatchTerrain", "Could not patch " + rel + ": " + ex.Message);
			return null;
		}
	}

	private static byte[]? GetOriginal(Dictionary<string, string> owned, string? versionDirectory, string? versionGuid, string rel)
	{
		if (string.IsNullOrEmpty(versionDirectory) || versionGuid is null || !Voidstrap.AppData.CommonAppData.IsVersionGuidValid(versionGuid))
			return null;
		try
		{
			string cacheFolder = Path.Combine(OriginalsFolder, versionGuid);
			string cache = Path.Combine(cacheFolder, LocalPath(rel));
			byte[] original;
			if (File.Exists(cache))
			{
				original = File.ReadAllBytes(cache);
			}
			else
			{
				string live = Path.Combine(versionDirectory, LocalPath(rel));
				if (!File.Exists(live))
					return null;
				original = File.ReadAllBytes(live);
				string modsFile = ModsPath(rel);
				if (File.Exists(modsFile) && original.AsSpan().SequenceEqual(File.ReadAllBytes(modsFile)))
				{
					if (owned.Remove(rel))
						Filesystem.DeleteWritableFile(modsFile);
					App.Logger?.WriteLine("FileModManager::GetOriginal", "Skipped " + rel + " because the installed copy already has a mod applied.");
					return null;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
				File.WriteAllBytes(cache, original);
			}
			Directory.SetLastWriteTimeUtc(cacheFolder, DateTime.UtcNow);
			return original;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::GetOriginal", "Could not read the original " + rel + ": " + ex.Message);
			return null;
		}
	}

	private static void Sync(Dictionary<string, string> owned, string rel, byte[]? content)
	{
		string path = ModsPath(rel);
		try
		{
			bool exists = File.Exists(path);
			bool ours = exists && owned.TryGetValue(rel, out string? hash) && string.Equals(hash, Hash(File.ReadAllBytes(path)), StringComparison.Ordinal);
			if (exists && !ours)
			{
				if (owned.Remove(rel) || content != null)
					App.Logger?.WriteLine("FileModManager::Sync", "Left your own mod file in place: " + rel);
				return;
			}
			if (content == null)
			{
				if (exists)
					Filesystem.DeleteWritableFile(path);
				owned.Remove(rel);
				return;
			}
			string contentHash = Hash(content);
			if (!ours || !string.Equals(owned[rel], contentHash, StringComparison.Ordinal))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				Filesystem.AssertReadOnly(path);
				File.WriteAllBytes(path, content);
			}
			owned[rel] = contentHash;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::Sync", "Could not update " + rel + ": " + ex.Message);
		}
	}

	private static void AdoptLegacyFiles(Dictionary<string, string> owned, bool firstRun)
	{
		try
		{
			foreach (string rel in LegacyPlaceholderFiles)
			{
				string path = ModsPath(rel);
				if (owned.ContainsKey(rel) || !IsLegacyPlaceholder(path))
					continue;
				if (Array.IndexOf(RetiredFiles, rel) >= 0)
					Filesystem.DeleteWritableFile(path);
				else
					owned[rel] = Hash(File.ReadAllBytes(path));
			}
			if (!firstRun)
				return;
			foreach (string rel in TerrainFiles)
			{
				string path = ModsPath(rel);
				if (!owned.ContainsKey(rel) && IsLegacyTerrainOutput(path))
					owned[rel] = Hash(File.ReadAllBytes(path));
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::AdoptLegacyFiles", "Could not check older low quality texture files: " + ex.Message);
		}
	}

	private static void RestoreLegacyBackups()
	{
		string root = Path.Combine(Paths.Base, "RblxVersions");
		if (!Directory.Exists(root))
			return;
		foreach (string version in Directory.EnumerateDirectories(root))
		{
			foreach (string rel in LegacyPlaceholderFiles.Concat(TerrainFiles))
			{
				string live = Path.Combine(version, LocalPath(rel));
				string backup = live + LegacyBackupSuffix;
				if (!File.Exists(backup))
					continue;
				try
				{
					if (!IsLegacyPlaceholder(backup))
						Filesystem.CopyWritableFile(backup, live);
					Filesystem.DeleteWritableFile(backup);
				}
				catch (Exception ex)
				{
					App.Logger?.WriteLine("FileModManager::RestoreLegacyBackups", "Could not restore " + rel + ": " + ex.Message);
				}
			}
		}
	}

	private static void PruneOriginals(string currentGuid)
	{
		try
		{
			if (!Directory.Exists(OriginalsFolder))
				return;
			IEnumerable<DirectoryInfo> stale = new DirectoryInfo(OriginalsFolder).EnumerateDirectories()
				.Where(folder => !string.Equals(folder.Name, currentGuid, StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(static folder => folder.LastWriteTimeUtc)
				.Skip(KeptOriginalVersions - 1);
			foreach (DirectoryInfo folder in stale)
				folder.Delete(true);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::PruneOriginals", "Could not remove old texture copies: " + ex.Message);
		}
	}

	private static bool IsLegacyPlaceholder(string path)
	{
		FileInfo info = new FileInfo(path);
		if (!info.Exists || info.Length != 384)
			return false;
		byte[] bytes = File.ReadAllBytes(path);
		return bytes.AsSpan(0, 4).SequenceEqual("DDS "u8)
			&& BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) == 16
			&& BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) == 16
			&& BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88)) == 8;
	}

	private static bool IsLegacyTerrainOutput(string path)
	{
		if (!File.Exists(path))
			return false;
		try
		{
			if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["materials"] is not JsonArray materials)
				return false;
			double? shared = null;
			foreach (JsonObject material in materials.OfType<JsonObject>())
			{
				foreach (string key in TextureKeys)
				{
					if (material[key] is not JsonObject texture)
						continue;
					if (texture["tiling"] is not JsonValue tilingValue || !tilingValue.TryGetValue(out double tiling))
						return false;
					if (texture["detiling_legacy"] is not JsonValue legacyValue || !legacyValue.TryGetValue(out double legacy) || legacy != tiling)
						return false;
					if (shared is null)
						shared = tiling;
					else if (shared != tiling)
						return false;
				}
			}
			return shared is not null;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static Dictionary<string, string> LoadOwned()
	{
		try
		{
			if (File.Exists(OwnedPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(OwnedPath)) is { } saved)
				return new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("FileModManager::LoadOwned", "Could not read the low quality texture state: " + ex.Message);
		}
		return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	private static void SaveOwned(Dictionary<string, string> owned)
	{
		Directory.CreateDirectory(StateFolder);
		File.WriteAllText(OwnedPath, JsonSerializer.Serialize(owned, JsonOptions.Indented));
	}

	private static byte[] CreatePng(byte shade, byte alpha)
	{
		SixLabors.ImageSharp.PixelFormats.Rgba32 color = new SixLabors.ImageSharp.PixelFormats.Rgba32(shade, shade, shade, alpha);
		using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 4, color);
		using MemoryStream output = new MemoryStream();
		image.Save(output, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
		return output.ToArray();
	}

	private static string Hash(byte[] bytes)
	{
		return Convert.ToHexString(SHA256.HashData(bytes));
	}

	private static string LocalPath(string rel)
	{
		return rel.Replace('/', Path.DirectorySeparatorChar);
	}

	private static string ModsPath(string rel)
	{
		return Path.Combine(Paths.Mods, LocalPath(rel));
	}
}
