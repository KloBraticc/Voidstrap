using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Voidstrap.Utility;

internal readonly record struct ModAutoFixReport(int TexturesFixed, int BackupsCleared, bool BackupsLocked, int DuplicateFiles);

internal static class ModAutoFixer
{
	private const string LogIdent = "ModAutoFixer";

	private const int DdsHeaderBytes = 128;

	private const int Dx10HeaderBytes = 20;

	private const int FourCcOffset = 84;

	private static readonly string[] ContentRoots = { "content", "ExtraContent", "PlatformContent" };

	private static readonly Dictionary<uint, string> ConvertibleFormats = new()
	{
		[70] = "DXT1",
		[72] = "DXT1",
		[73] = "DXT3",
		[74] = "DXT3",
		[75] = "DXT3",
		[76] = "DXT5",
		[78] = "DXT5"
	};

	private const long MaxSkyFaceBytes = 67108864L;

	private static readonly HashSet<string> VersionLockedExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".lua", ".luau"
	};

	private static readonly string[] VersionLockedFolders =
	{
		"content/configs/", "ExtraContent/models/", "ExtraContent/translations/"
	};

	private static readonly HashSet<string> LuaPackageArtExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".png", ".jpg", ".jpeg", ".dds", ".ktx", ".webp", ".ttf", ".otf"
	};

	public static string SkyFolder => Path.Combine("PlatformContent", "pc", "textures", "sky");

	public static bool IsVersionLocked(string relative)
	{
		string normalized = relative.Replace('\\', '/');
		string extension = Path.GetExtension(normalized);
		if (VersionLockedExtensions.Contains(extension))
		{
			return true;
		}
		if (normalized.StartsWith("ExtraContent/LuaPackages/", StringComparison.OrdinalIgnoreCase))
		{
			return !LuaPackageArtExtensions.Contains(extension);
		}
		return VersionLockedFolders.Any(folder => normalized.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
	}

	public static bool IsIgnoredModFile(string relative)
	{
		return IsVersionLocked(relative) || IsDuplicateCopy(relative) || LegacyMaterialTextures.IsRedirectedSource(relative);
	}

	private static bool IsDuplicateCopy(string relative)
	{
		string name = Path.GetFileNameWithoutExtension(relative);
		int open = name.LastIndexOf(" (", StringComparison.Ordinal);
		return open > 0 && name.EndsWith(')') && name.Length - open > 3 && name.AsSpan(open + 2, name.Length - open - 3).IndexOfAnyExceptInRange('0', '9') < 0;
	}

	public static void PrepareModSources(string robloxFolder)
	{
		RobloxLayoutRepair.UseClient(robloxFolder);
		List<string> folders = [Paths.Mods];
		try
		{
			folders.AddRange(ManagedModStore.Load().Select(record => ManagedModStore.GetFolder(record.Id)));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The managed mod folders could not be listed: " + ex.Message);
		}
		RepairModLayout(robloxFolder, folders);
		int faces = ConvertSkyFaces(folders);
		if (faces > 0)
		{
			App.Logger?.WriteLine(LogIdent, "Converted " + faces + " sky faces to PNG so Roblox can read them");
		}
	}

	private static int ConvertSkyFaces(IEnumerable<string> modFolders)
	{
		int converted = 0;
		foreach (string modFolder in modFolders)
		{
			string sky = Path.Combine(modFolder, SkyFolder);
			if (!Directory.Exists(sky))
			{
				continue;
			}
			foreach (string file in Directory.EnumerateFiles(sky, "*.tex"))
			{
				try
				{
					if (IsDuplicateCopy(file) || !NeedsSkyConversion(file))
					{
						continue;
					}
					string temporary = file + ".autofix";
					using (Image<Rgba32> image = Image.Load<Rgba32>(file))
					{
						image.SaveAsPng(temporary);
					}
					File.SetAttributes(file, FileAttributes.Normal);
					File.Move(temporary, file, true);
					converted++;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
				{
					App.Logger?.WriteLine(LogIdent, "Could not convert the sky face " + Path.GetFileName(file) + ": " + ex.Message);
				}
			}
		}
		return converted;
	}

	private static bool NeedsSkyConversion(string path)
	{
		FileInfo info = new(path);
		if (info.Length < 8 || info.Length > MaxSkyFaceBytes)
		{
			return false;
		}
		Span<byte> header = stackalloc byte[8];
		using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			stream.ReadExactly(header);
		}
		bool dds = header[0] == (byte)'D' && header[1] == (byte)'D' && header[2] == (byte)'S' && header[3] == (byte)' ';
		bool png = header[0] == 0x89 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G';
		return !dds && !png;
	}

	public static string OtaPatchBackups => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Roblox",
		"OTAPatchBackups");

	public static ModAutoFixReport Run(string robloxFolder, bool modsActive)
	{
		int textures = FixIncompatibleTextures(robloxFolder);
		int duplicates = CountDuplicateFiles(robloxFolder);
		int cleared = 0;
		bool locked = false;
		if (modsActive)
		{
			cleared = ClearOtaPatchBackups();
			locked = LockOtaPatchBackups();
		}
		else
		{
			UnlockOtaPatchBackups();
		}
		ModAutoFixReport report = new(textures, cleared, locked, duplicates);
		if (textures > 0 || cleared > 0 || locked)
		{
			App.Logger?.WriteLine(LogIdent, "Repaired " + textures + " textures, cleared " + cleared + " patch backups, write lock: " + locked);
		}
		if (duplicates > 0)
		{
			App.Logger?.WriteLine(LogIdent, "Found " + duplicates + " duplicate copies in the client folder, they are ignored by Roblox and can be removed");
		}
		return report;
	}

	private static void RepairModLayout(string robloxFolder, IEnumerable<string> folders)
	{
		try
		{
			RobloxLayoutRepair.Repair(robloxFolder, folders);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The mod layout could not be checked against this Roblox version: " + ex.Message);
		}
	}

	public static int FixIncompatibleTextures(string robloxFolder)
	{
		int fixedCount = 0;
		foreach (string relativeRoot in ContentRoots)
		{
			string root = Path.Combine(robloxFolder, relativeRoot);
			if (!Directory.Exists(root))
			{
				continue;
			}
			IEnumerable<string> files;
			try
			{
				files = Directory.EnumerateFiles(root, "*.dds", SearchOption.AllDirectories);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Could not scan " + relativeRoot + ": " + ex.Message);
				continue;
			}
			foreach (string file in files)
			{
				if (TryFixTexture(file))
				{
					fixedCount++;
				}
			}
		}
		return fixedCount;
	}

	public static bool TryFixTexture(string path)
	{
		try
		{
			byte[] header = new byte[DdsHeaderBytes + Dx10HeaderBytes];
			using (FileStream probe = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				if (probe.Length < header.Length || probe.Read(header, 0, header.Length) < header.Length)
				{
					return false;
				}
			}
			if (header[0] != (byte)'D' || header[1] != (byte)'D' || header[2] != (byte)'S' || header[3] != (byte)' ')
			{
				return false;
			}
			if (header[FourCcOffset] != (byte)'D' || header[FourCcOffset + 1] != (byte)'X'
				|| header[FourCcOffset + 2] != (byte)'1' || header[FourCcOffset + 3] != (byte)'0')
			{
				return false;
			}
			uint format = BitConverter.ToUInt32(header, DdsHeaderBytes);
			uint dimension = BitConverter.ToUInt32(header, DdsHeaderBytes + 4);
			uint miscFlag = BitConverter.ToUInt32(header, DdsHeaderBytes + 8);
			uint arraySize = BitConverter.ToUInt32(header, DdsHeaderBytes + 12);
			if (dimension != 3 || miscFlag != 0 || arraySize != 1)
			{
				return false;
			}
			if (!ConvertibleFormats.TryGetValue(format, out string? fourCc))
			{
				return false;
			}

			byte[] payload = File.ReadAllBytes(path);
			if (payload.Length <= DdsHeaderBytes + Dx10HeaderBytes)
			{
				return false;
			}
			byte[] rewritten = new byte[payload.Length - Dx10HeaderBytes];
			Array.Copy(payload, 0, rewritten, 0, DdsHeaderBytes);
			Array.Copy(payload, DdsHeaderBytes + Dx10HeaderBytes, rewritten, DdsHeaderBytes, payload.Length - DdsHeaderBytes - Dx10HeaderBytes);
			for (int index = 0; index < 4; index++)
			{
				rewritten[FourCcOffset + index] = (byte)fourCc[index];
			}

			string temporary = path + ".autofix";
			File.WriteAllBytes(temporary, rewritten);
			File.Move(temporary, path, true);
			App.Logger?.WriteLine(LogIdent, "Converted " + Path.GetFileName(path) + " from DX10 format " + format + " to " + fourCc);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Could not repair " + Path.GetFileName(path) + ": " + ex.Message);
			return false;
		}
	}

	public static int ClearOtaPatchBackups()
	{
		string folder = OtaPatchBackups;
		UnlockOtaPatchBackups();
		int removed = 0;
		try
		{
			if (!Directory.Exists(folder))
			{
				Directory.CreateDirectory(folder);
				return 0;
			}
			foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				try
				{
					File.SetAttributes(file, FileAttributes.Normal);
					File.Delete(file);
					removed++;
				}
				catch (Exception ex)
				{
					App.Logger?.WriteLine(LogIdent, "Could not remove a patch backup: " + ex.Message);
				}
			}
			foreach (string directory in Directory.EnumerateDirectories(folder))
			{
				try
				{
					Directory.Delete(directory, recursive: true);
				}
				catch (Exception ex)
				{
					App.Logger?.WriteLine(LogIdent, "Could not remove a patch backup folder: " + ex.Message);
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The patch backup folder could not be cleared: " + ex.Message);
		}
		return removed;
	}

	public static bool LockOtaPatchBackups()
	{
		return TrySetWriteBlock(OtaPatchBackups, block: true);
	}

	public static bool UnlockOtaPatchBackups()
	{
		return TrySetWriteBlock(OtaPatchBackups, block: false);
	}

	private static bool TrySetWriteBlock(string folder, bool block)
	{
		if (!Platform.IsWindows)
		{
			return false;
		}
		try
		{
			Directory.CreateDirectory(folder);
			DirectoryInfo info = new(folder);
			DirectorySecurity security = info.GetAccessControl();
			SecurityIdentifier? user = WindowsIdentity.GetCurrent().User;
			if (user is null)
			{
				return false;
			}
			FileSystemAccessRule rule = new(
				user,
				FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteData | FileSystemRights.AppendData,
				InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
				PropagationFlags.None,
				AccessControlType.Deny);
			if (block)
			{
				security.AddAccessRule(rule);
			}
			else
			{
				security.RemoveAccessRule(rule);
			}
			info.SetAccessControl(security);
			App.Logger?.WriteLine(LogIdent, block
				? "Blocked writes to the Roblox patch backup folder so mods survive over the air patches"
				: "Restored writes to the Roblox patch backup folder");
			return block;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The patch backup folder permissions could not be changed: " + ex.Message);
			return false;
		}
	}

	private static int CountDuplicateFiles(string robloxFolder)
	{
		int count = 0;
		foreach (string relativeRoot in ContentRoots)
		{
			string root = Path.Combine(robloxFolder, relativeRoot);
			if (!Directory.Exists(root))
			{
				continue;
			}
			try
			{
				foreach (string file in Directory.EnumerateFiles(root, "* (?).*", SearchOption.AllDirectories))
				{
					string name = Path.GetFileNameWithoutExtension(file);
					if (name.EndsWith(")", StringComparison.Ordinal) && name.Contains(" (", StringComparison.Ordinal))
					{
						count++;
					}
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Could not scan " + relativeRoot + " for duplicates: " + ex.Message);
			}
		}
		return count;
	}
}
