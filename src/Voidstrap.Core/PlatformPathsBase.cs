using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public abstract class PlatformPathsBase : IPlatformPaths
{
	protected PlatformPathsBase(PlatformStoragePaths storage)
	{
		Storage = storage;
	}

	public PlatformStoragePaths Storage { get; }

	public Task<OperationResult> EnsureDirectoriesAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			foreach (string path in GetDirectories())
			{
				bool existed = Directory.Exists(path);
				Directory.CreateDirectory(path);
				if (OperatingSystem.IsLinux() && !existed)
				{
					TrySetPrivateUnixMode(path);
				}
			}

			return Task.FromResult(OperationResult.Success());
		}
		catch (OperationCanceledException)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "Directory initialization was canceled"));
		}
		catch (IOException exception)
		{
			return Task.FromResult(OperationResult.Fail("DirectoryInitializationFailed", exception.Message));
		}
		catch (UnauthorizedAccessException exception)
		{
			return Task.FromResult(OperationResult.Fail("DirectoryAccessDenied", exception.Message, CapabilityState.RequiresPermission));
		}
		catch (Exception exception)
		{
			return Task.FromResult(OperationResult.Fail("DirectoryInitializationFailed", exception.Message));
		}
	}

	[SupportedOSPlatform("linux")]
	private static void TrySetPrivateUnixMode(string path)
	{
		try
		{
			File.SetUnixFileMode(
				path,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
		catch (PlatformNotSupportedException)
		{
		}
	}

	private IEnumerable<string> GetDirectories()
	{
		yield return Storage.ApplicationSupport;
		yield return Storage.Configuration;
		yield return Storage.Data;
		yield return Storage.Cache;
		yield return Storage.Logs;
		yield return Storage.Downloads;
		yield return Storage.Extensions;
		yield return Storage.Temporary;
	}
}
