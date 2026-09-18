using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations.AssetProxy;

internal static class LinuxAssetWarpBridge
{
	private const string LogIdent = "AssetProxy";

	private static readonly SemaphoreSlim Gate = new(1, 1);

	private static string CertificatePath => Path.Combine(Paths.AssetProxy, "Certificates", "proxy-ca.crt");

	private static string MarkerPath => Path.Combine(Paths.AssetProxy, "sober-proxy.armed");

	private static string SoberDataRoot => Path.Combine(
		Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".var", "app", "org.vinegarhq.Sober", "data", "sober");

	private static string RobloxTrustPath => Path.Combine(SoberDataRoot, "exe", "ssl", "cacert.pem");

	private static string RobloxTrustTemporaryPath => RobloxTrustPath + ".voidstrap.tmp";

	private static string RobloxTrustBackupPath => Path.Combine(Paths.AssetProxy, "Certificates", "sober-cacert.backup");

	private static string RobloxTrustStatePath => Path.Combine(Paths.AssetProxy, "Certificates", "sober-cacert.state");

	private static string LegacyRobloxTrustOverlayPath => Path.Combine(SoberDataRoot, "asset_overlay", "ssl", "cacert.pem");

	private static string LegacySandboxBundlePath => Path.Combine(SoberDataRoot, "asset_overlay", "ssl", "voidstrap-ca-bundle.pem");

	private const string RobloxTrustEntryName = "assets/ssl/cacert.pem";

	private static bool _armed;

	public static bool IsArmed => Volatile.Read(ref _armed);

	public static bool NeedsCleanup()
	{
		if (IsArmed)
		{
			return true;
		}

		try
		{
			return File.Exists(MarkerPath);
		}
		catch
		{
			return false;
		}
	}

	private static void WriteMarker(bool armed)
	{
		try
		{
			if (armed)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
				File.WriteAllText(MarkerPath, "armed");
			}
			else if (File.Exists(MarkerPath))
			{
				File.Delete(MarkerPath);
			}
		}
		catch
		{
		}
	}

	public static async Task<OperationResult> EnableAsync(Uri proxyAddress, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(proxyAddress);

		await Gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			SystemProcessService processes = new();
			OperationResult certificate = await WriteCertificateAsync(ct).ConfigureAwait(false);
			if (!certificate.Succeeded)
			{
				return certificate;
			}

			bool robloxTrusted = await InstallRobloxTrustAsync(ct).ConfigureAwait(false);

			if (!robloxTrusted)
			{
				await RollbackAsync(processes).ConfigureAwait(false);
				return OperationResult.Fail(
					"AssetWarpRobloxTrustUnavailable",
					"AssetWarp could not add its certificate to the Roblox trust store, so Roblox would reject the proxy");
			}

			OperationResult armed = await new LinuxSoberProxyArming(processes).ArmAsync(proxyAddress, ct).ConfigureAwait(false);
			if (!armed.Succeeded)
			{
				await RollbackAsync(processes).ConfigureAwait(false);
				return armed;
			}

			Volatile.Write(ref _armed, true);
			WriteMarker(true);
			App.Logger?.WriteLine(LogIdent, "Sober is routed through the AssetWarp proxy at " + proxyAddress.Authority);
			return OperationResult.Success();
		}
		catch
		{
			await RollbackAsync(new SystemProcessService()).ConfigureAwait(false);
			throw;
		}
		finally
		{
			Gate.Release();
		}
	}

	public static async Task DisableAsync(CancellationToken ct = default)
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		await Gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			OperationResult result = await new LinuxSoberProxyArming(new SystemProcessService()).DisarmAsync(ct).ConfigureAwait(false);
			App.Logger?.WriteLine(LogIdent, result.Succeeded
				? "Sober proxy settings restored"
				: "Sober proxy settings could not be restored: " + (result.Failure?.Message ?? "unknown failure"));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Sober proxy settings could not be restored: " + ex.Message);
		}
		finally
		{
			RemoveRobloxTrust();
			RemoveLegacyTrustFiles();
			Volatile.Write(ref _armed, false);
			WriteMarker(false);
			Gate.Release();
		}
	}

	public static void DisableBlocking(TimeSpan? budget = null)
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		TimeSpan limit = budget ?? TimeSpan.FromSeconds(8);
		if (limit <= TimeSpan.Zero)
		{
			return;
		}

		try
		{
			using CancellationTokenSource deadline = new(limit);
			DisableAsync(deadline.Token).GetAwaiter().GetResult();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Sober proxy settings could not be restored: " + ex.Message);
		}
	}

	public static async Task<OperationResult> RemoveCertificateAsync(CancellationToken ct = default)
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return OperationResult.Success();
		}

		return await new LinuxCertificateTrust(new SystemProcessService()).RemoveAsync(ct).ConfigureAwait(false);
	}

	private static string? FindRobloxPackage()
	{
		string packages = Path.Combine(SoberDataRoot, "packages");
		if (!Directory.Exists(packages))
		{
			return null;
		}

		foreach (string candidate in Directory.EnumerateFiles(packages, "base.apk", SearchOption.AllDirectories))
		{
			return candidate;
		}

		return null;
	}

	private static async Task<bool> InstallRobloxTrustAsync(CancellationToken ct)
	{
		try
		{
			X509Certificate2? root = AssetProxyCA.RootCa;
			if (root is null)
			{
				return false;
			}

			string? package = FindRobloxPackage();
			if (package is null)
			{
				App.Logger?.WriteLine(LogIdent, "The Roblox package was not found, its trust store could not be patched");
				return false;
			}

			string bundled;
			using (ZipArchive archive = ZipFile.OpenRead(package))
			{
				ZipArchiveEntry? entry = archive.GetEntry(RobloxTrustEntryName);
				if (entry is null)
				{
					App.Logger?.WriteLine(LogIdent, "The Roblox package does not contain a trust store at " + RobloxTrustEntryName);
					return false;
				}

				using StreamReader reader = new(entry.Open());
				bundled = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
			}

			if (bundled.IndexOf("BEGIN CERTIFICATE", StringComparison.Ordinal) < 0)
			{
				return false;
			}

			RemoveRobloxTrust();
			string target = RobloxTrustPath;
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			Directory.CreateDirectory(Path.GetDirectoryName(RobloxTrustStatePath)!);
			if (File.Exists(target))
			{
				File.Copy(target, RobloxTrustBackupPath, true);
				await File.WriteAllTextAsync(RobloxTrustStatePath, "restore", ct).ConfigureAwait(false);
			}
			else
			{
				if (File.Exists(RobloxTrustBackupPath))
					File.Delete(RobloxTrustBackupPath);
				await File.WriteAllTextAsync(RobloxTrustStatePath, "delete", ct).ConfigureAwait(false);
			}

			await File.WriteAllTextAsync(RobloxTrustTemporaryPath, bundled.TrimEnd() + "\n" + root.ExportCertificatePem().Trim() + "\n", ct).ConfigureAwait(false);
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(RobloxTrustTemporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			File.Move(RobloxTrustTemporaryPath, target, true);

			App.Logger?.WriteLine(LogIdent, "Added the AssetWarp root to the Roblox runtime trust store");
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The Roblox trust store could not be patched: " + ex.Message);
			return false;
		}
	}

	private static void RemoveRobloxTrust()
	{
		try
		{
			string state = File.Exists(RobloxTrustStatePath) ? File.ReadAllText(RobloxTrustStatePath).Trim() : "";
			if (state.Equals("restore", StringComparison.Ordinal) && File.Exists(RobloxTrustBackupPath))
			{
				Directory.CreateDirectory(Path.GetDirectoryName(RobloxTrustPath)!);
				File.Move(RobloxTrustBackupPath, RobloxTrustPath, true);
				App.Logger?.WriteLine(LogIdent, "Restored the previous Roblox runtime trust store");
			}
			else if (state.Equals("delete", StringComparison.Ordinal) && File.Exists(RobloxTrustPath))
			{
				File.Delete(RobloxTrustPath);
				App.Logger?.WriteLine(LogIdent, "Removed the temporary Roblox runtime trust store");
			}

			if (File.Exists(RobloxTrustBackupPath))
				File.Delete(RobloxTrustBackupPath);
			if (File.Exists(RobloxTrustStatePath))
				File.Delete(RobloxTrustStatePath);
			if (File.Exists(RobloxTrustTemporaryPath))
				File.Delete(RobloxTrustTemporaryPath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The Roblox runtime trust store could not be restored: " + ex.Message);
		}
	}

	private static void RemoveLegacyTrustFiles()
	{
		try
		{
			if (File.Exists(LegacyRobloxTrustOverlayPath))
				File.Delete(LegacyRobloxTrustOverlayPath);
			if (File.Exists(LegacySandboxBundlePath))
				File.Delete(LegacySandboxBundlePath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Legacy Sober trust files could not be removed: " + ex.Message);
		}
	}

	private static async Task RollbackAsync(SystemProcessService processes)
	{
		try
		{
			await new LinuxSoberProxyArming(processes).DisarmAsync(CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Sober proxy rollback failed: " + ex.Message);
		}
		RemoveRobloxTrust();
		RemoveLegacyTrustFiles();
		Volatile.Write(ref _armed, false);
		WriteMarker(false);
	}

	private static async Task<OperationResult> WriteCertificateAsync(CancellationToken ct)
	{
		X509Certificate2? root = AssetProxyCA.RootCa;
		if (root is null)
		{
			return OperationResult.Fail("AssetWarpCertificateMissing", "The AssetWarp certificate has not been created yet");
		}

		string path = CertificatePath;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, root.ExportCertificatePem().Trim() + "\n", ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return OperationResult.Fail("AssetWarpCertificateWriteFailed", "The AssetWarp certificate could not be written: " + ex.Message);
		}

		return OperationResult.Success();
	}
}
