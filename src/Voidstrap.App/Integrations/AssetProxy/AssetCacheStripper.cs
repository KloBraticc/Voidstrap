using System;
using System.Buffers.Binary;
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

	private static readonly byte[] Ktx2Identifier = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

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

	private static readonly string[] DownloadHosts =
	[
		"sc0.rbxcdn.com", "sc1.rbxcdn.com", "sc2.rbxcdn.com", "sc3.rbxcdn.com",
		"sc4.rbxcdn.com", "sc5.rbxcdn.com", "sc6.rbxcdn.com", "sc7.rbxcdn.com",
		"c0.rbxcdn.com", "c1.rbxcdn.com", "c2.rbxcdn.com", "c3.rbxcdn.com",
		"c4.rbxcdn.com", "c5.rbxcdn.com", "c6.rbxcdn.com", "c7.rbxcdn.com"
	];

	private const string FirewallRuleName = "Voidstrap AssetWarp offline cache";

	private static readonly object BlockGate = new();

	private static readonly HashSet<string> BlockedAddresses = new(StringComparer.OrdinalIgnoreCase);

	private static CancellationTokenSource? _blockRefresh;

	public static bool BlocksDownloads => Voidstrap.Utility.Platform.IsWindows && App.Settings.Prop.AssetWarpOfflineCache && HasRemovals && !AssetProxyServer.IsRequired;

	public static void BlockDownloads()
	{
		if (!BlocksDownloads)
			return;
		if (!ProcessElevation.IsAdministrator())
		{
			App.Logger?.WriteLine(LogIdent, "Offline cache mode needs administrator access, Roblox can still download removed assets this session");
			return;
		}
		string program = Path.Combine(new Voidstrap.AppData.RobloxPlayerData().Directory, "RobloxPlayerBeta.exe");
		lock (BlockGate)
		{
			BlockedAddresses.Clear();
			if (!RefreshBlock(program))
				return;
			CancellationTokenSource refresh = new();
			_blockRefresh = refresh;
			_ = RefreshLoopAsync(program, refresh.Token);
		}
		App.Logger?.WriteLine(LogIdent, "Offline cache mode is on, Roblox asset downloads are blocked for this session");
	}

	public static void UnblockDownloads()
	{
		CancellationTokenSource? refresh;
		lock (BlockGate)
		{
			refresh = _blockRefresh;
			_blockRefresh = null;
			BlockedAddresses.Clear();
		}
		if (refresh == null)
			return;
		refresh.Cancel();
		refresh.Dispose();
		RemoveFirewallRule();
		App.Logger?.WriteLine(LogIdent, "Offline cache mode ended, Roblox asset downloads are allowed again");
	}

	public static void RemoveStaleFirewallRule()
	{
		if (!Voidstrap.Utility.Platform.IsWindows || _blockRefresh != null || !ProcessElevation.IsAdministrator())
			return;
		System.Diagnostics.Process[] running = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
		bool robloxRunning = running.Length > 0;
		foreach (System.Diagnostics.Process process in running)
			process.Dispose();
		if (!robloxRunning)
			RemoveFirewallRule();
	}

	private static async Task RefreshLoopAsync(string program, CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
				lock (BlockGate)
				{
					if (!token.IsCancellationRequested)
						RefreshBlock(program);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private static bool RefreshBlock(string program)
	{
		int before = BlockedAddresses.Count;
		foreach (string host in DownloadHosts)
		{
			try
			{
				foreach (System.Net.IPAddress address in System.Net.Dns.GetHostAddresses(host))
					BlockedAddresses.Add(address.ToString());
			}
			catch (System.Net.Sockets.SocketException)
			{
			}
		}
		if (BlockedAddresses.Count == 0)
		{
			App.Logger?.WriteLine(LogIdent, "Offline cache mode could not resolve the Roblox asset servers");
			return false;
		}
		if (BlockedAddresses.Count == before && before > 0)
			return true;
		RemoveFirewallRule();
		return RunNetsh(["advfirewall", "firewall", "add", "rule", "name=" + FirewallRuleName, "dir=out", "action=block", "enable=yes", "profile=any", "program=" + program, "remoteip=" + string.Join(",", BlockedAddresses)]);
	}

	private static void RemoveFirewallRule()
	{
		RunNetsh(["advfirewall", "firewall", "delete", "rule", "name=" + FirewallRuleName], logFailure: false);
	}

	private static bool RunNetsh(IEnumerable<string> arguments, bool logFailure = true)
	{
		try
		{
			System.Diagnostics.ProcessStartInfo startInfo = new("netsh")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (string argument in arguments)
				startInfo.ArgumentList.Add(argument);
			using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);
			if (process == null)
				return false;
			string output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(10000);
			if (process.ExitCode != 0 && logFailure)
				App.Logger?.WriteLine(LogIdent, "The firewall rule could not be updated: " + output.Trim());
			return process.ExitCode == 0;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			if (logFailure)
				App.Logger?.WriteLine(LogIdent, "The firewall rule could not be updated: " + ex.Message);
			return false;
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
		List<(string Hash, long Size)> sizes = [];
		foreach (KeyValuePair<Kind, bool> pair in wanted)
		{
			if (!pair.Value)
				RestoreKind(pair.Key, sizes);
		}
		if (wanted.ContainsValue(true) && Directory.Exists(RobloxAssetCache.Root))
			StripCache(wanted, token);
		foreach (KeyValuePair<Kind, bool> pair in wanted)
		{
			if (pair.Value)
				CollectStrippedSizes(pair.Key, sizes);
		}
		if (sizes.Count > 0 && RobloxCacheIndex.UpdateSizes(sizes))
			App.Logger?.WriteLine(LogIdent, "Updated " + sizes.Count + " entries in the Roblox cache index");
	}

	private static void CollectStrippedSizes(Kind kind, List<(string Hash, long Size)> sizes)
	{
		string backupDirectory = Path.Combine(BackupRoot, kind.ToString());
		if (!Directory.Exists(backupDirectory))
			return;
		foreach (string backup in Directory.EnumerateFiles(backupDirectory))
		{
			string hash = Path.GetFileName(backup);
			if (!RobloxAssetCache.IsValidHash(hash))
				continue;
			FileInfo entry = new(RobloxAssetCache.GetEntryPath(hash));
			if (entry.Exists)
				sizes.Add((hash, entry.Length));
		}
	}

	private static void StripCache(Dictionary<Kind, bool> wanted, CancellationToken token)
	{

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
		string backupDirectory = Path.Combine(BackupRoot, kind.ToString());
		Directory.CreateDirectory(backupDirectory);
		string backup = Path.Combine(backupDirectory, hash);
		bool backedUp = File.Exists(backup);
		byte[] original = File.ReadAllBytes(backedUp ? backup : file);
		if (!RobloxAssetCache.IsCacheEntry(original, original.Length))
			return false;

		byte[]? blank = kind switch
		{
			Kind.Meshes => BlankMesh,
			Kind.Animations => BlankAnimation,
			Kind.Textures => BuildBlankKtx(original.AsSpan(RobloxAssetCache.HeaderBytes)),
			_ => BlankImage
		};
		if (blank == null || new FileInfo(file).Length == RobloxAssetCache.HeaderBytes + blank.Length)
			return false;
		if (!backedUp)
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

	private static byte[]? BuildBlankKtx(ReadOnlySpan<byte> body)
	{
		if (body.Length < 80 || !body.StartsWith(Ktx2Identifier))
			return null;
		uint format = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
		uint dfdOffset = BinaryPrimitives.ReadUInt32LittleEndian(body[48..]);
		uint dfdLength = BinaryPrimitives.ReadUInt32LittleEndian(body[52..]);
		if (dfdLength == 0 || dfdOffset + (ulong)dfdLength > (ulong)body.Length)
			return null;
		int block = format is >= 135 and <= 146 ? 16 : 8;
		const int dfdStart = 80 + 24;
		int dataStart = (dfdStart + (int)dfdLength + 15) / 16 * 16;
		byte[] output = new byte[dataStart + block];
		Ktx2Identifier.CopyTo(output, 0);
		Span<byte> span = output;
		BinaryPrimitives.WriteUInt32LittleEndian(span[12..], format);
		BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 1);
		BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 4);
		BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 4);
		BinaryPrimitives.WriteUInt32LittleEndian(span[36..], 1);
		BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 1);
		BinaryPrimitives.WriteUInt32LittleEndian(span[48..], dfdStart);
		BinaryPrimitives.WriteUInt32LittleEndian(span[52..], dfdLength);
		BinaryPrimitives.WriteUInt64LittleEndian(span[80..], (ulong)dataStart);
		BinaryPrimitives.WriteUInt64LittleEndian(span[88..], (ulong)block);
		BinaryPrimitives.WriteUInt64LittleEndian(span[96..], (ulong)block);
		body.Slice((int)dfdOffset, (int)dfdLength).CopyTo(span[dfdStart..]);
		return output;
	}

	private static void RestoreKind(Kind kind, List<(string Hash, long Size)> sizes)
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
				sizes.Add((hash, new FileInfo(target).Length));
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
