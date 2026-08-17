using System;
using System.IO;
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
			OperationResult certificate = await InstallCertificateAsync(processes, ct).ConfigureAwait(false);
			if (!certificate.Succeeded)
			{
				return certificate;
			}

			OperationResult armed = await new LinuxSoberProxyArming(processes).ArmAsync(proxyAddress, ct).ConfigureAwait(false);
			if (!armed.Succeeded)
			{
				return armed;
			}

			Volatile.Write(ref _armed, true);
			WriteMarker(true);
			App.Logger?.WriteLine(LogIdent, "Sober is routed through the AssetWarp proxy at " + proxyAddress.Authority);
			return OperationResult.Success();
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
			Volatile.Write(ref _armed, false);
			WriteMarker(false);
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
			Gate.Release();
		}
	}

	public static void DisableBlocking()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || !NeedsCleanup())
		{
			return;
		}

		try
		{
			using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
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

	private static async Task<OperationResult> InstallCertificateAsync(SystemProcessService processes, CancellationToken ct)
	{
		X509Certificate2? root = AssetProxyCA.RootCa;
		if (root is null)
		{
			return OperationResult.Fail("AssetWarpCertificateMissing", "The AssetWarp certificate has not been created yet");
		}

		LinuxCertificateTrust trust = new(processes);
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

		if (trust.IsInstalled(path))
		{
			return OperationResult.Success();
		}

		return await trust.InstallAsync(path, ct).ConfigureAwait(false);
	}
}
