using System.Buffers.Binary;
using System.ComponentModel;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Voidstrap.Platform.Linux;

public static class LinuxBundleInstaller
{
	private const long MaxExtractedBytes = 2147483648L;
	private const string ArchiveRoot = "Voidstrap";
	private const string ExecutableName = "Voidstrap";
	private const string BackupSuffix = ".voidstrap.previous";
	private const string MarkerSuffix = ".owner";
	private const string MarkerHeader = "Voidstrap Linux bundle backup\n";
	private const int AtomicExchangeFlag = 2;
	private const int CurrentWorkingDirectoryDescriptor = -100;
	private const int ElfHeaderSize = 64;
	private const int ElfProgramHeaderSize = 56;
	private const int OpenDirectoryFlag = 65536;
	private const int CloseOnExecuteFlag = 524288;
	private const uint LoadableSegmentType = 1;

	private static readonly HashSet<string> SupportedRuntimeIdentifiers = new(StringComparer.Ordinal)
	{
		"linux-x64",
		"linux-arm64",
		"linux-musl-x64",
		"linux-musl-arm64"
	};

	private static readonly string[] ManagedRoots =
	[
		"/app",
		"/bin",
		"/lib",
		"/lib64",
		"/gnu/store",
		"/nix/store",
		"/opt",
		"/sbin",
		"/snap",
		"/usr",
		"/var/lib",
		"/var/opt"
	];

	public static string GetCurrentRuntimeIdentifier()
	{
		string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier.ToLowerInvariant();
		if (SupportedRuntimeIdentifiers.Contains(runtimeIdentifier))
		{
			return runtimeIdentifier;
		}

		string architecture = RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "x64",
			Architecture.Arm64 => "arm64",
			_ => throw new PlatformNotSupportedException("The current Linux architecture is unsupported")
		};
		return IsMuslRuntime() ? $"linux-musl-{architecture}" : $"linux-{architecture}";
	}

	public static string GetAssetName(string tag, string runtimeIdentifier)
	{
		if (!SupportedRuntimeIdentifiers.Contains(runtimeIdentifier))
		{
			throw new ArgumentOutOfRangeException(nameof(runtimeIdentifier), "The Linux runtime identifier is unsupported");
		}

		string version = tag.Trim().TrimStart('v', 'V');
		string[] parts = version.Split('.');
		if (parts.Length is < 2 or > 4 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
		{
			throw new ArgumentException("The release tag is invalid", nameof(tag));
		}

		return $"Voidstrap_{version}_{runtimeIdentifier}.tar.gz";
	}

	public static bool IsPackageManagedLocation(string installDirectory)
	{
		string normalized = NormalizeUnixPath(installDirectory);
		if (normalized == "/")
		{
			return true;
		}

		return ManagedRoots.Any(root => normalized.Equals(root, StringComparison.Ordinal) || normalized.StartsWith(root + "/", StringComparison.Ordinal));
	}

	public static void Install(string archivePath, string currentExecutablePath)
	{
		InstallAsync(archivePath, currentExecutablePath, CancellationToken.None).GetAwaiter().GetResult();
	}

	public static void Install(string archivePath, string currentExecutablePath, Action? afterBackupCreated)
	{
		Action<LinuxBundleInstallBoundary>? faultInjector = afterBackupCreated is null
			? null
			: boundary =>
			{
				if (boundary == LinuxBundleInstallBoundary.BackupPersisted)
				{
					afterBackupCreated();
				}
			};
		InstallCoreAsync(archivePath, currentExecutablePath, CancellationToken.None, faultInjector, AtomicExchange).GetAwaiter().GetResult();
	}

	public static Task InstallAsync(string archivePath, string currentExecutablePath, CancellationToken cancellationToken)
	{
		return InstallCoreAsync(archivePath, currentExecutablePath, cancellationToken, null, AtomicExchange);
	}

	internal static Task InstallAsync(
		string archivePath,
		string currentExecutablePath,
		CancellationToken cancellationToken,
		Action<LinuxBundleInstallBoundary>? faultInjector,
		Action<string, string>? atomicExchange = null)
	{
		return InstallCoreAsync(archivePath, currentExecutablePath, cancellationToken, faultInjector, atomicExchange ?? AtomicExchange);
	}

	private static async Task InstallCoreAsync(
		string archivePath,
		string currentExecutablePath,
		CancellationToken cancellationToken,
		Action<LinuxBundleInstallBoundary>? faultInjector,
		Action<string, string> atomicExchange)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(currentExecutablePath);
		cancellationToken.ThrowIfCancellationRequested();

		string rawInstallDirectory = Path.GetDirectoryName(currentExecutablePath) ?? string.Empty;
		if (IsPackageManagedLocation(rawInstallDirectory))
		{
			throw new UnauthorizedAccessException("Package managed installations cannot update themselves");
		}

		string executablePath = Path.GetFullPath(currentExecutablePath);
		string installDirectory = Path.GetDirectoryName(executablePath) ?? throw new InvalidOperationException("The installation directory is unavailable");
		string parentDirectory = Directory.GetParent(installDirectory)?.FullName ?? throw new InvalidOperationException("The installation parent directory is unavailable");
		if (IsPackageManagedLocation(installDirectory))
		{
			throw new UnauthorizedAccessException("Package managed installations cannot update themselves");
		}
		if (!Directory.Exists(installDirectory) || IsLink(installDirectory))
		{
			throw new DirectoryNotFoundException("The installation directory is unavailable");
		}
		if (!File.Exists(executablePath) || new FileInfo(executablePath).Length == 0 || IsLink(executablePath))
		{
			throw new FileNotFoundException("The current Voidstrap executable is unavailable", executablePath);
		}
		if (!File.Exists(archivePath))
		{
			throw new FileNotFoundException("The update bundle is unavailable", archivePath);
		}

		EnsureWritable(parentDirectory);
		EnsureWritable(installDirectory);
		EnsureAtomicExchangeAvailable(parentDirectory, atomicExchange);
		SyncDirectory(parentDirectory);
		InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.AtomicExchangeValidated);
		cancellationToken.ThrowIfCancellationRequested();

		string stageDirectory = installDirectory + ".voidstrap.stage." + Guid.NewGuid().ToString("N");
		string backupDirectory = installDirectory + BackupSuffix;
		string markerPath = backupDirectory + MarkerSuffix;
		string markerContents = MarkerHeader + installDirectory;
		bool activated = false;
		bool backupPersisted = false;

		try
		{
			Directory.CreateDirectory(stageDirectory);
			UnixFileMode? executableMode = await ExtractBundleAsync(archivePath, stageDirectory, cancellationToken, faultInjector).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			ValidateCandidate(stageDirectory, executableMode);
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.CandidateStaged);
			cancellationToken.ThrowIfCancellationRequested();
			SyncDirectoryTree(stageDirectory, cancellationToken);
			SyncDirectory(parentDirectory);
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.StageDurable);
			cancellationToken.ThrowIfCancellationRequested();
			PrepareBackupSlot(backupDirectory, markerPath, markerContents);
			SyncDirectory(parentDirectory);
			WriteDurableFile(markerPath, markerContents);
			SyncDirectory(parentDirectory);
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.BackupPrepared);
			cancellationToken.ThrowIfCancellationRequested();
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.BeforeActivation);
			cancellationToken.ThrowIfCancellationRequested();
			atomicExchange(installDirectory, stageDirectory);
			activated = true;
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.AfterActivation);
			SyncDirectory(parentDirectory);
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.ActivationDurable);
			Directory.Move(stageDirectory, backupDirectory);
			backupPersisted = true;
			SyncDirectory(parentDirectory);
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.BackupDurable);
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.BackupPersisted);
		}
		catch (Exception failure)
		{
			Exception? rollbackFailure = null;
			try
			{
				if (activated)
				{
					string previousLocation = backupPersisted ? backupDirectory : stageDirectory;
					if (!Directory.Exists(installDirectory) || !Directory.Exists(previousLocation) ||
						(backupPersisted && !IsOwnedBackup(backupDirectory, markerPath, markerContents)))
					{
						throw new IOException("The update transaction cannot be rolled back safely");
					}
					atomicExchange(installDirectory, previousLocation);
					activated = false;
					SyncDirectory(parentDirectory);
					if (backupPersisted && Directory.Exists(backupDirectory) && !IsLink(backupDirectory))
					{
						Directory.Delete(backupDirectory, true);
						SyncDirectory(parentDirectory);
					}
				}
				if (DeleteOwnedMarker(markerPath, markerContents))
				{
					SyncDirectory(parentDirectory);
				}
			}
			catch (Exception exception)
			{
				rollbackFailure = exception;
			}
			if (rollbackFailure is not null)
			{
				throw new AggregateException("The update failed and could not be rolled back", failure, rollbackFailure);
			}
			throw;
		}
		finally
		{
			if (!activated && Directory.Exists(stageDirectory) && !IsLink(stageDirectory))
			{
				Directory.Delete(stageDirectory, true);
				SyncDirectory(parentDirectory);
			}
		}
	}

	private static void InvokeBoundary(Action<LinuxBundleInstallBoundary>? faultInjector, LinuxBundleInstallBoundary boundary)
	{
		faultInjector?.Invoke(boundary);
	}

	private static void EnsureAtomicExchangeAvailable(string parentDirectory, Action<string, string> atomicExchange)
	{
		string firstProbe = Path.Combine(parentDirectory, ".voidstrap.exchange." + Guid.NewGuid().ToString("N"));
		string secondProbe = Path.Combine(parentDirectory, ".voidstrap.exchange." + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(firstProbe);
			Directory.CreateDirectory(secondProbe);
			atomicExchange(firstProbe, secondProbe);
		}
		finally
		{
			if (Directory.Exists(firstProbe) && !IsLink(firstProbe))
			{
				Directory.Delete(firstProbe);
			}
			if (Directory.Exists(secondProbe) && !IsLink(secondProbe))
			{
				Directory.Delete(secondProbe);
			}
		}
	}

	private static void AtomicExchange(string firstPath, string secondPath)
	{
		if (!OperatingSystem.IsLinux())
		{
			throw new PlatformNotSupportedException("Atomic Linux update activation is unavailable");
		}

		nint syscallNumber = RuntimeInformation.ProcessArchitecture switch
		{
			Architecture.X64 => 316,
			Architecture.Arm64 => 276,
			_ => throw new PlatformNotSupportedException("The current Linux architecture is unsupported")
		};

		try
		{
			nint result = InvokeSyscall(
				syscallNumber,
				CurrentWorkingDirectoryDescriptor,
				firstPath,
				CurrentWorkingDirectoryDescriptor,
				secondPath,
				AtomicExchangeFlag);
			if (result == 0)
			{
				return;
			}
			int error = Marshal.GetLastPInvokeError();
			throw new IOException("Atomic Linux update activation is unavailable", new Win32Exception(error));
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			throw new PlatformNotSupportedException("Atomic Linux update activation is unavailable", exception);
		}
	}

	[DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
	private static extern nint InvokeSyscall(
		nint number,
		nint firstDirectoryDescriptor,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string firstPath,
		nint secondDirectoryDescriptor,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string secondPath,
		nint flags);

	[DllImport("libc", EntryPoint = "open", SetLastError = true)]
	private static extern int OpenDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

	[DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
	private static extern int FlushFileDescriptor(int fileDescriptor);

	[DllImport("libc", EntryPoint = "close", SetLastError = true)]
	private static extern int CloseFileDescriptor(int fileDescriptor);

	private static bool IsMuslRuntime()
	{
		if (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase) || File.Exists("/etc/alpine-release"))
		{
			return true;
		}

		try
		{
			return Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*.so.1", SearchOption.TopDirectoryOnly).Any();
		}
		catch
		{
			return false;
		}
	}

	private static string NormalizeUnixPath(string path)
	{
		string normalized = path.Replace('\\', '/');
		if (!normalized.StartsWith("/", StringComparison.Ordinal))
		{
			return normalized.TrimEnd('/');
		}

		List<string> parts = [];
		foreach (string part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			if (part == ".")
			{
				continue;
			}
			if (part == "..")
			{
				if (parts.Count > 0)
				{
					parts.RemoveAt(parts.Count - 1);
				}
				continue;
			}
			parts.Add(part);
		}
		return "/" + string.Join('/', parts);
	}

	private static void EnsureWritable(string directory)
	{
		if (!Directory.Exists(directory) || IsLink(directory))
		{
			throw new UnauthorizedAccessException("The update target is unavailable");
		}
		if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) &&
			(File.GetUnixFileMode(directory) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
		{
			throw new UnauthorizedAccessException("The update target is not writable");
		}

		string probePath = Path.Combine(directory, ".voidstrap.write." + Guid.NewGuid().ToString("N"));
		try
		{
			using FileStream stream = new(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
			stream.WriteByte(0);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new UnauthorizedAccessException("The update target is not writable", ex);
		}
		finally
		{
			if (File.Exists(probePath) && !IsLink(probePath))
			{
				File.Delete(probePath);
			}
		}
	}

	private static async Task<UnixFileMode?> ExtractBundleAsync(
		string archivePath,
		string stageDirectory,
		CancellationToken cancellationToken,
		Action<LinuxBundleInstallBoundary>? faultInjector)
	{
		string stageRoot = Path.GetFullPath(stageDirectory) + Path.DirectorySeparatorChar;
		HashSet<string> extractedPaths = new(GetPathComparer());
		List<(string Path, UnixFileMode Mode)> directoryModes = [];
		long extractedBytes = 0;
		UnixFileMode? executableMode = null;

		await using FileStream archive = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		await using GZipStream gzip = new(archive, CompressionMode.Decompress, false);
		using TarReader reader = new(gzip, false);
		while (await reader.GetNextEntryAsync(false, cancellationToken).ConfigureAwait(false) is TarEntry entry)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string relativePath = ValidateArchivePath(entry.Name);
			if (relativePath.Length == 0)
			{
				if (entry.EntryType != TarEntryType.Directory)
				{
					throw new InvalidDataException("The update bundle root is invalid");
				}
				continue;
			}

			string destination = Path.GetFullPath(Path.Combine(stageDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
			if (!destination.StartsWith(stageRoot, GetPathComparison()) || !extractedPaths.Add(destination))
			{
				throw new InvalidDataException("The update bundle contains an unsafe path");
			}

			switch (entry.EntryType)
			{
				case TarEntryType.Directory:
					Directory.CreateDirectory(destination);
					directoryModes.Add((destination, entry.Mode));
					break;
				case TarEntryType.RegularFile:
				case TarEntryType.V7RegularFile:
					if (entry.Length < 0 || extractedBytes > MaxExtractedBytes - entry.Length)
					{
						throw new InvalidDataException("The update bundle is too large");
					}
					extractedBytes += entry.Length;
					Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
					await using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
					{
						await CopyEntryAsync(entry.DataStream, output, entry.Length, cancellationToken, faultInjector).ConfigureAwait(false);
						ApplyMode(destination, entry.Mode);
						await output.FlushAsync(cancellationToken).ConfigureAwait(false);
						output.Flush(true);
					}
					if (new FileInfo(destination).Length != entry.Length)
					{
						throw new InvalidDataException("The update bundle contains a truncated file");
					}
					if (relativePath.Equals(ExecutableName, StringComparison.Ordinal))
					{
						executableMode = entry.Mode;
					}
					break;
				default:
					throw new InvalidDataException("The update bundle contains an unsupported entry");
			}
		}

		foreach ((string path, UnixFileMode mode) in directoryModes.OrderByDescending(item => item.Path.Length))
		{
			ApplyMode(path, mode);
		}
		return executableMode;
	}

	private static void SyncDirectoryTree(string rootDirectory, CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		List<string> directories = Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories)
			.OrderByDescending(path => path.Length)
			.ToList();
		directories.Add(rootDirectory);
		foreach (string directory in directories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			SyncDirectory(directory);
		}
	}

	private static void SyncDirectory(string directory)
	{
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		int descriptor = OpenDirectory(directory, OpenDirectoryFlag | CloseOnExecuteFlag);
		if (descriptor < 0)
		{
			int error = Marshal.GetLastPInvokeError();
			throw new IOException("The update directory could not be synchronized", new Win32Exception(error));
		}
		try
		{
			if (FlushFileDescriptor(descriptor) != 0)
			{
				int error = Marshal.GetLastPInvokeError();
				throw new IOException("The update directory could not be synchronized", new Win32Exception(error));
			}
		}
		finally
		{
			CloseFileDescriptor(descriptor);
		}
	}

	private static void WriteDurableFile(string path, string contents)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(contents);
		using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		stream.Write(bytes);
		stream.Flush(true);
	}

	private static async Task CopyEntryAsync(
		Stream? input,
		Stream output,
		long expectedLength,
		CancellationToken cancellationToken,
		Action<LinuxBundleInstallBoundary>? faultInjector)
	{
		if (input is null)
		{
			if (expectedLength == 0)
			{
				return;
			}
			throw new InvalidDataException("The update bundle contains a truncated file");
		}

		byte[] buffer = new byte[81920];
		long copied = 0;
		while (copied < expectedLength)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int requested = (int)Math.Min(buffer.Length, expectedLength - copied);
			int read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				throw new InvalidDataException("The update bundle contains a truncated file");
			}
			await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
			copied += read;
			InvokeBoundary(faultInjector, LinuxBundleInstallBoundary.ExtractionChunkWritten);
		}
		cancellationToken.ThrowIfCancellationRequested();
	}

	private static string ValidateArchivePath(string entryName)
	{
		if (string.IsNullOrWhiteSpace(entryName) || entryName.IndexOf('\0') >= 0)
		{
			throw new InvalidDataException("The update bundle contains an invalid path");
		}

		string normalized = entryName.Replace('\\', '/').TrimEnd('/');
		if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains("//", StringComparison.Ordinal))
		{
			throw new InvalidDataException("The update bundle contains an unsafe path");
		}

		string[] parts = normalized.Split('/');
		if (parts.Length == 0 || !parts[0].Equals(ArchiveRoot, StringComparison.Ordinal) || parts.Any(part => part.Length == 0 || part is "." or ".."))
		{
			throw new InvalidDataException("The update bundle root is invalid");
		}

		return string.Join('/', parts.Skip(1));
	}

	private static void ValidateCandidate(string stageDirectory, UnixFileMode? executableMode)
	{
		string executablePath = Path.Combine(stageDirectory, ExecutableName);
		if (!File.Exists(executablePath) || IsLink(executablePath) || new FileInfo(executablePath).Length == 0)
		{
			throw new InvalidDataException("The update bundle has no valid Voidstrap executable");
		}
		if (executableMode is null || (executableMode.Value & UnixFileMode.UserExecute) == 0)
		{
			throw new InvalidDataException("The update bundle has no executable Voidstrap program");
		}
		ValidateElfExecutable(executablePath);
	}

	private static void ValidateElfExecutable(string executablePath)
	{
		Span<byte> header = stackalloc byte[ElfHeaderSize];
		using FileStream stream = File.OpenRead(executablePath);
		if (!ReadExactly(stream, header) ||
			header[0] != 0x7f || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F' ||
			header[4] != 2 || header[5] != 1 || header[6] != 1 ||
			BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]) != 1 ||
			BinaryPrimitives.ReadUInt16LittleEndian(header[52..54]) != ElfHeaderSize)
		{
			throw new InvalidDataException("The update bundle contains an invalid Voidstrap program");
		}

		ushort expectedMachine = RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => 0x3e,
			Architecture.Arm64 => 0xb7,
			_ => throw new PlatformNotSupportedException("The current Linux architecture is unsupported")
		};
		ushort executableType = BinaryPrimitives.ReadUInt16LittleEndian(header[16..18]);
		ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]);
		if (executableType is not 2 and not 3 || machine != expectedMachine)
		{
			throw new InvalidDataException("The update bundle contains a Voidstrap program for another architecture");
		}

		ulong programHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[32..40]);
		ushort programHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header[54..56]);
		ushort programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(header[56..58]);
		ulong fileLength = checked((ulong)stream.Length);
		ulong programHeadersLength = (ulong)programHeaderSize * programHeaderCount;
		if (programHeaderOffset < ElfHeaderSize || programHeaderSize != ElfProgramHeaderSize || programHeaderCount == 0 ||
			programHeaderOffset > fileLength || programHeadersLength > fileLength - programHeaderOffset)
		{
			throw new InvalidDataException("The update bundle contains an invalid Voidstrap program");
		}

		Span<byte> programHeader = stackalloc byte[ElfProgramHeaderSize];
		bool hasLoadableSegment = false;
		stream.Position = checked((long)programHeaderOffset);
		for (int index = 0; index < programHeaderCount; index++)
		{
			if (!ReadExactly(stream, programHeader))
			{
				throw new InvalidDataException("The update bundle contains an invalid Voidstrap program");
			}
			uint type = BinaryPrimitives.ReadUInt32LittleEndian(programHeader[0..4]);
			ulong segmentOffset = BinaryPrimitives.ReadUInt64LittleEndian(programHeader[8..16]);
			ulong segmentFileSize = BinaryPrimitives.ReadUInt64LittleEndian(programHeader[32..40]);
			ulong segmentMemorySize = BinaryPrimitives.ReadUInt64LittleEndian(programHeader[40..48]);
			if (segmentOffset > fileLength || segmentFileSize > fileLength - segmentOffset ||
				(type == LoadableSegmentType && (segmentMemorySize < segmentFileSize || segmentMemorySize == 0)))
			{
				throw new InvalidDataException("The update bundle contains an invalid Voidstrap program");
			}
			if (type == LoadableSegmentType)
			{
				hasLoadableSegment = true;
			}
		}
		if (!hasLoadableSegment)
		{
			throw new InvalidDataException("The update bundle contains an invalid Voidstrap program");
		}
	}

	private static bool ReadExactly(Stream stream, Span<byte> buffer)
	{
		int totalRead = 0;
		while (totalRead < buffer.Length)
		{
			int read = stream.Read(buffer[totalRead..]);
			if (read == 0)
			{
				return false;
			}
			totalRead += read;
		}
		return true;
	}

	private static void PrepareBackupSlot(string backupDirectory, string markerPath, string markerContents)
	{
		bool backupExists = Directory.Exists(backupDirectory) || File.Exists(backupDirectory);
		bool markerExists = File.Exists(markerPath) || Directory.Exists(markerPath);
		if (!backupExists && !markerExists)
		{
			return;
		}
		if (!backupExists && File.Exists(markerPath) && !IsLink(markerPath) && MarkerMatches(markerPath, markerContents))
		{
			File.Delete(markerPath);
			return;
		}

		if (!IsOwnedBackup(backupDirectory, markerPath, markerContents))
		{
			throw new IOException("The previous update backup is not owned by this installation");
		}

		Directory.Delete(backupDirectory, true);
		File.Delete(markerPath);
	}

	private static bool IsOwnedBackup(string backupDirectory, string markerPath, string markerContents)
	{
		return Directory.Exists(backupDirectory) && !IsLink(backupDirectory) && File.Exists(markerPath) && !IsLink(markerPath) && MarkerMatches(markerPath, markerContents);
	}

	private static bool MarkerMatches(string markerPath, string markerContents)
	{
		try
		{
			FileInfo marker = new(markerPath);
			return marker.Length <= 4096 && File.ReadAllText(markerPath).Equals(markerContents, StringComparison.Ordinal);
		}
		catch
		{
			return false;
		}
	}

	private static bool DeleteOwnedMarker(string markerPath, string markerContents)
	{
		if (File.Exists(markerPath) && !IsLink(markerPath) && MarkerMatches(markerPath, markerContents))
		{
			File.Delete(markerPath);
			return true;
		}
		return false;
	}

	private static bool IsLink(string path)
	{
		try
		{
			FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
			return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
		}
		catch
		{
			return true;
		}
	}

	private static void ApplyMode(string path, UnixFileMode mode)
	{
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
		{
			UnixFileMode permissions = mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
			File.SetUnixFileMode(path, permissions);
		}
	}

	private static StringComparer GetPathComparer()
	{
		return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	}

	private static StringComparison GetPathComparison()
	{
		return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
	}
}

internal enum LinuxBundleInstallBoundary
{
	AtomicExchangeValidated,
	ExtractionChunkWritten,
	CandidateStaged,
	StageDurable,
	BackupPrepared,
	BeforeActivation,
	AfterActivation,
	ActivationDurable,
	BackupDurable,
	BackupPersisted
}
