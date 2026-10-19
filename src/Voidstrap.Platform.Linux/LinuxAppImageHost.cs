using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public static class LinuxAppImageHost
{
	private const int HeaderLength = 20;

	public static bool IsRunning => TryGetCurrentPath(out _);

	public static string ResolveApplicationPath(string fallbackPath)
	{
		if (TryGetCurrentPath(out string appImagePath))
		{
			return appImagePath;
		}
		if (TryGetStoreWrapperPath(fallbackPath, out string wrapperPath))
		{
			return wrapperPath;
		}
		return fallbackPath;
	}

	public static bool TryGetStoreWrapperPath(string? executablePath, out string wrapperPath)
	{
		wrapperPath = string.Empty;
		if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(executablePath))
		{
			return false;
		}

		try
		{
			string fullPath = Path.GetFullPath(executablePath);
			if (!TryGetStorePackageRoot(fullPath, out string packageRoot))
			{
				return false;
			}

			string binDirectory = Path.Combine(packageRoot, "bin");
			if (fullPath.StartsWith(binDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(fullPath))
			{
				wrapperPath = fullPath;
				return true;
			}

			foreach (string name in new[] { "voidstrap", "Voidstrap", Path.GetFileName(fullPath) })
			{
				if (string.IsNullOrWhiteSpace(name))
				{
					continue;
				}

				string candidate = Path.Combine(binDirectory, name);
				if (File.Exists(candidate))
				{
					wrapperPath = candidate;
					return true;
				}
			}
		}
		catch
		{
		}

		return false;
	}

	public static bool TryGetStorePackageRoot(string? path, out string packageRoot)
	{
		packageRoot = string.Empty;
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		foreach (string prefix in new[] { "/nix/store/", "/gnu/store/" })
		{
			if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length <= prefix.Length)
			{
				continue;
			}

			int slash = path.IndexOf('/', prefix.Length);
			packageRoot = slash < 0 ? path : path[..slash];
			return packageRoot.Length > prefix.Length;
		}

		return false;
	}

	public static bool TryGetCurrentPath(out string appImagePath)
	{
		appImagePath = string.Empty;
		if (!OperatingSystem.IsLinux())
		{
			return false;
		}

		string? configuredImage = Environment.GetEnvironmentVariable("APPIMAGE");
		string? configuredDirectory = Environment.GetEnvironmentVariable("APPDIR");
		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(configuredImage)
			|| string.IsNullOrWhiteSpace(configuredDirectory)
			|| string.IsNullOrWhiteSpace(processPath)
			|| !Path.IsPathRooted(configuredImage)
			|| !Path.IsPathRooted(configuredDirectory)
			|| configuredImage.IndexOfAny(['\r', '\n', '\0']) >= 0)
		{
			return false;
		}

		try
		{
			string imagePath = ResolveFinalPath(Path.GetFullPath(configuredImage));
			string appDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredDirectory));
			string executablePath = Path.GetFullPath(processPath);
			if (!executablePath.StartsWith(appDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				|| !File.Exists(imagePath)
				|| !HasValidHeader(imagePath))
			{
				return false;
			}

			appImagePath = imagePath;
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static bool HasValidHeader(string path)
	{
		try
		{
			Span<byte> header = stackalloc byte[HeaderLength];
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			int read = 0;
			while (read < header.Length)
			{
				int count = stream.Read(header[read..]);
				if (count == 0)
				{
					return false;
				}
				read += count;
			}

			if (header[0] != 0x7f
				|| header[1] != (byte)'E'
				|| header[2] != (byte)'L'
				|| header[3] != (byte)'F'
				|| header[4] != 2
				|| header[5] != 1
				|| header[8] != (byte)'A'
				|| header[9] != (byte)'I'
				|| header[10] is not 1 and not 2)
			{
				return false;
			}

			ushort expectedMachine = RuntimeInformation.OSArchitecture switch
			{
				Architecture.X64 => 0x3e,
				Architecture.Arm64 => 0xb7,
				_ => 0
			};
			return expectedMachine != 0 && BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]) == expectedMachine;
		}
		catch
		{
			return false;
		}
	}

	private static string ResolveFinalPath(string path)
	{
		FileInfo file = new(path);
		FileSystemInfo? target = file.ResolveLinkTarget(true);
		return target == null ? file.FullName : Path.GetFullPath(target.FullName);
	}
}
