using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public static partial class LinuxArchive
{
	private const string Library = "libarchive.so.13";
	private const int ArchiveEof = 1;
	private const int ArchiveWarn = -20;
	private const int BlockSize = 1 << 16;
	private const int FileTypeMask = 0xF000;
	private const int RegularFile = 0x8000;

	private static readonly Lazy<bool> Available = new(() =>
	{
		if (!HostSystem.UsesLinuxHost || !NativeLibrary.TryLoad(Library, typeof(LinuxArchive).Assembly, null, out IntPtr handle))
			return false;
		return NativeLibrary.TryGetExport(handle, "archive_read_support_format_all", out _);
	});

	public static bool IsAvailable => Available.Value;

	public static int ExtractFiles(string archivePath, string destination, int maxEntries, long maxBytes, CancellationToken token)
	{
		if (!IsAvailable)
			throw new InvalidDataException("libarchive is not installed.");

		string root = Path.GetFullPath(destination);
		string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
		Directory.CreateDirectory(root);

		IntPtr archive = archive_read_new();
		if (archive == IntPtr.Zero)
			throw new InvalidDataException("libarchive could not start.");
		try
		{
			archive_read_support_filter_all(archive);
			archive_read_support_format_all(archive);
			if (archive_read_open_filename(archive, archivePath, BlockSize) < ArchiveWarn)
				throw Failure(archive);

			byte[] buffer = new byte[BlockSize];
			int files = 0;
			long total = 0;
			while (true)
			{
				token.ThrowIfCancellationRequested();
				int status = archive_read_next_header(archive, out IntPtr entry);
				if (status == ArchiveEof)
					break;
				if (status < ArchiveWarn)
					throw Failure(archive);
				if ((archive_entry_filetype(entry) & FileTypeMask) != RegularFile || archive_entry_hardlink(entry) != IntPtr.Zero)
					continue;

				string? name = EntryName(entry);
				if (string.IsNullOrWhiteSpace(name))
					continue;
				string target = Path.GetFullPath(Path.Combine(root, name));
				if (!target.StartsWith(rootPrefix, StringComparison.Ordinal))
					continue;
				if (++files > maxEntries)
					throw new InvalidOperationException("The package expands far beyond the allowed size and was discarded.");

				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				using FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
				while (true)
				{
					token.ThrowIfCancellationRequested();
					nint read = archive_read_data(archive, buffer, (nuint)buffer.Length);
					if (read == 0)
						break;
					if (read < 0)
						throw Failure(archive);
					total += read;
					if (total > maxBytes)
						throw new InvalidOperationException("The package expands far beyond the allowed size and was discarded.");
					output.Write(buffer, 0, (int)read);
				}
			}
			return files;
		}
		finally
		{
			archive_read_free(archive);
		}
	}

	private static string? EntryName(IntPtr entry)
	{
		IntPtr name = archive_entry_pathname_utf8(entry);
		if (name == IntPtr.Zero)
			name = archive_entry_pathname(entry);
		if (name == IntPtr.Zero)
			return null;
		string? text = Marshal.PtrToStringUTF8(name);
		if (text is null)
			return null;
		string[] parts = text.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Any(part => part is "." or ".."))
			return null;
		return parts.Length == 0 ? null : Path.Combine(parts);
	}

	private static InvalidDataException Failure(IntPtr archive)
	{
		IntPtr message = archive_error_string(archive);
		string text = message == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(message) ?? "unknown error";
		return new InvalidDataException("libarchive could not read the package: " + text);
	}

	[LibraryImport(Library)]
	private static partial IntPtr archive_read_new();

	[LibraryImport(Library)]
	private static partial int archive_read_support_filter_all(IntPtr archive);

	[LibraryImport(Library)]
	private static partial int archive_read_support_format_all(IntPtr archive);

	[LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int archive_read_open_filename(IntPtr archive, string fileName, nuint blockSize);

	[LibraryImport(Library)]
	private static partial int archive_read_next_header(IntPtr archive, out IntPtr entry);

	[LibraryImport(Library)]
	private static partial nint archive_read_data(IntPtr archive, [Out] byte[] buffer, nuint size);

	[LibraryImport(Library)]
	private static partial int archive_read_free(IntPtr archive);

	[LibraryImport(Library)]
	private static partial IntPtr archive_error_string(IntPtr archive);

	[LibraryImport(Library)]
	private static partial IntPtr archive_entry_pathname(IntPtr entry);

	[LibraryImport(Library)]
	private static partial IntPtr archive_entry_pathname_utf8(IntPtr entry);

	[LibraryImport(Library)]
	private static partial IntPtr archive_entry_hardlink(IntPtr entry);

	[LibraryImport(Library)]
	private static partial int archive_entry_filetype(IntPtr entry);
}
