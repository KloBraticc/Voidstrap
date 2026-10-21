using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.AssetProxy;

internal static class AssetCacheStripper
{
	private const string LogIdent = "AssetCacheStripper";

	private enum Kind
	{
		Textures,
		Images,
		Meshes,
		Animations
	}

	private static readonly byte[] BlankImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==");

	private static readonly byte[] BlankMesh = Encoding.ASCII.GetBytes("version 1.00\n0\n");

	private static readonly byte[] BlankAnimation = Encoding.ASCII.GetBytes("<roblox version=\"4\"><Item class=\"KeyframeSequence\" referent=\"RBX0\"><Properties><bool name=\"Loop\">false</bool><string name=\"Name\">KeyframeSequence</string></Properties></Item></roblox>");

	private static readonly byte[] KtxMagic = [0xAB, (byte)'K', (byte)'T', (byte)'X'];

	private static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G'];

	private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static string BackupRoot => Path.Combine(Paths.AssetProxy, "CacheStrip");

	public static bool HasRemovals
	{
		get
		{
			var settings = App.Settings.Prop;
			return settings.AssetWarpEnabled && (settings.AssetWarpDisableAllTextures || settings.AssetWarpDisableAllDecals || settings.AssetWarpDisableAllImages || settings.AssetWarpDisableAllAnimations || settings.AssetWarpDisableAllMeshes);
		}
	}

	public static void QueueApply()
	{
		_ = Task.Run(() => ApplyAsync(CancellationToken.None));
	}

	public static async Task ApplyAsync(CancellationToken token)
	{
		await Gate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			Apply(token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Cached asset removal failed: " + ex.Message);
		}
		finally
		{
			Gate.Release();
		}
	}

	private static void Apply(CancellationToken token)
	{
		Dictionary<Kind, bool> wanted = WantedKinds();
		foreach (KeyValuePair<Kind, bool> pair in wanted)
		{
			if (!pair.Value)
				RestoreKind(pair.Key);
		}
		if (!wanted.ContainsValue(true) || !Directory.Exists(RobloxAssetCache.Root))
			return;

		int stripped = 0;
		byte[] header = new byte[RobloxAssetCache.HeaderBytes + 512];
		foreach (string file in Directory.EnumerateFiles(RobloxAssetCache.Root, "*", SearchOption.AllDirectories))
		{
			token.ThrowIfCancellationRequested();
			string name = Path.GetFileName(file);
			if (!RobloxAssetCache.IsValidHash(name))
				continue;
			try
			{
				Kind? kind = Classify(file, header);
				if (kind is not Kind found || !wanted[found])
					continue;
				if (Strip(file, name, found))
					stripped++;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
		if (stripped > 0)
			App.Logger?.WriteLine(LogIdent, "Removed " + stripped + " cached Roblox assets");
	}

	private static Dictionary<Kind, bool> WantedKinds()
	{
		var settings = App.Settings.Prop;
		bool on = settings.AssetWarpEnabled;
		return new Dictionary<Kind, bool>
		{
			[Kind.Textures] = on && settings.AssetWarpDisableAllTextures,
			[Kind.Images] = on && (settings.AssetWarpDisableAllImages || settings.AssetWarpDisableAllDecals),
			[Kind.Meshes] = on && settings.AssetWarpDisableAllMeshes,
			[Kind.Animations] = on && settings.AssetWarpDisableAllAnimations
		};
	}

	private static Kind? Classify(string file, byte[] buffer)
	{
		int read;
		using (FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
		{
			read = stream.Read(buffer, 0, buffer.Length);
		}
		if (read <= RobloxAssetCache.HeaderBytes || buffer[0] != (byte)'R' || buffer[1] != (byte)'B' || buffer[2] != (byte)'X' || buffer[3] != (byte)'H')
			return null;
		ReadOnlySpan<byte> body = buffer.AsSpan(RobloxAssetCache.HeaderBytes, read - RobloxAssetCache.HeaderBytes);
		if (body.StartsWith(KtxMagic) || body.StartsWith("DDS "u8))
			return Kind.Textures;
		if (body.StartsWith(PngMagic) || body.StartsWith(JpegMagic))
			return Kind.Images;
		if (body.StartsWith("version "u8))
			return Kind.Meshes;
		if (body.StartsWith("<roblox"u8) && (body.IndexOf("KeyframeSequence"u8) >= 0 || body.IndexOf("CurveAnimation"u8) >= 0))
			return Kind.Animations;
		return null;
	}

	private static bool Strip(string file, string hash, Kind kind)
	{
		byte[] blank = kind switch
		{
			Kind.Meshes => BlankMesh,
			Kind.Animations => BlankAnimation,
			_ => BlankImage
		};
		FileInfo info = new(file);
		if (info.Length == RobloxAssetCache.HeaderBytes + blank.Length)
			return false;

		string backupDirectory = Path.Combine(BackupRoot, kind.ToString());
		Directory.CreateDirectory(backupDirectory);
		string backup = Path.Combine(backupDirectory, hash);
		byte[] original = File.ReadAllBytes(file);
		if (!RobloxAssetCache.IsCacheEntry(original, original.Length))
			return false;
		File.WriteAllBytes(backup, original);

		byte[] replacement = new byte[RobloxAssetCache.HeaderBytes + blank.Length];
		Array.Copy(original, replacement, RobloxAssetCache.HeaderBytes);
		BitConverter.TryWriteBytes(replacement.AsSpan(RobloxAssetCache.ContentLengthOffset, 4), (uint)blank.Length);
		blank.CopyTo(replacement, RobloxAssetCache.HeaderBytes);
		SetReadOnly(file, false);
		File.WriteAllBytes(file, replacement);
		SetReadOnly(file, true);
		return true;
	}

	private static void RestoreKind(Kind kind)
	{
		string backupDirectory = Path.Combine(BackupRoot, kind.ToString());
		if (!Directory.Exists(backupDirectory))
			return;
		int restored = 0;
		foreach (string backup in Directory.EnumerateFiles(backupDirectory))
		{
			string hash = Path.GetFileName(backup);
			if (!RobloxAssetCache.IsValidHash(hash))
				continue;
			try
			{
				string target = RobloxAssetCache.GetEntryPath(hash);
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				SetReadOnly(target, false);
				File.Copy(backup, target, overwrite: true);
				File.Delete(backup);
				restored++;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "Could not restore cached asset " + hash + ": " + ex.Message);
			}
		}
		if (restored > 0)
			App.Logger?.WriteLine(LogIdent, "Restored " + restored + " cached Roblox assets");
	}

	private static void SetReadOnly(string path, bool readOnly)
	{
		if (!File.Exists(path))
			return;
		FileAttributes attributes = File.GetAttributes(path);
		File.SetAttributes(path, readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly);
	}
}
