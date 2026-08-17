using System.Runtime.InteropServices;

namespace Voidstrap.Utility;

internal static partial class CloudFiles
{
	private const int Pinned = 0x00080000;

	private const int Unpinned = 0x00100000;

	private const int RecallOnOpen = 0x00040000;

	private const int RecallOnDataAccess = 0x00400000;

	private const int Offline = 0x00001000;

	private const int PlaceholderMask = RecallOnOpen | RecallOnDataAccess | Offline;

	[LibraryImport("kernel32.dll", EntryPoint = "SetFileAttributesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetFileAttributes(string fileName, uint attributes);

	public static bool IsPlaceholder(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			return ((int)File.GetAttributes(path) & PlaceholderMask) != 0;
		}
		catch
		{
			return false;
		}
	}

	public static bool IsCloudFailure(Exception exception, string? path)
	{
		if (exception is not IOException && exception is not UnauthorizedAccessException)
		{
			return false;
		}
		return IsPlaceholder(path) || Voidstrap.Installer.IsCloudSyncedPath(path);
	}

	public static bool Hydrate(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return false;
		}
		if (!IsPlaceholder(path))
		{
			return true;
		}
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			if (stream.Length > 0)
			{
				stream.ReadByte();
			}
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("CloudFiles::Hydrate", "Could not download " + path + " from the cloud provider: " + ex.Message);
			return false;
		}
	}

	public static void PinLocally(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Platform.IsWindows)
		{
			return;
		}
		try
		{
			if (!Directory.Exists(path) && !File.Exists(path))
			{
				return;
			}
			int current = (int)File.GetAttributes(path);
			int wanted = (current & ~Unpinned) | Pinned;
			if (wanted != current)
			{
				SetFileAttributes(path, (uint)wanted);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("CloudFiles::PinLocally", "Could not pin " + path + ": " + ex.Message);
		}
	}

	public static void PinInstallRoot()
	{
		if (!Platform.IsWindows || !Paths.Initialized || !Voidstrap.Installer.IsCloudSyncedPath(Paths.Base))
		{
			return;
		}
		PinLocally(Paths.Base);
		PinLocally(Paths.Application);
		foreach (string directory in SafeChildren(Paths.Base))
		{
			PinLocally(directory);
		}
	}

	private static string[] SafeChildren(string root)
	{
		try
		{
			return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
		}
		catch
		{
			return [];
		}
	}
}
