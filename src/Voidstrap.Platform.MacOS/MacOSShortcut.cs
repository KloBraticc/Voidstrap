using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSShortcut
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private static readonly object Gate = new();
	private static nint _appKit;

	public static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

	public static void Delete(string path)
	{
		if (Directory.Exists(path))
			Directory.Delete(path, true);
		else if (File.Exists(path))
			File.Delete(path);
	}

	public static void Create(string shortcutPath, string executable, string arguments, string? iconPath)
	{
		string staging = shortcutPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			string macOS = Path.Combine(staging, "Contents", "MacOS");
			Directory.CreateDirectory(macOS);
			string identifier = "com.voidstrap.shortcut." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shortcutPath)))[..16].ToLowerInvariant();
			string name = Path.GetFileNameWithoutExtension(shortcutPath);
			File.WriteAllText(Path.Combine(staging, "Contents", "Info.plist"),
				"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
				"<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
				"<plist version=\"1.0\"><dict>\n" +
				"<key>CFBundleExecutable</key><string>launch</string>\n" +
				"<key>CFBundleIdentifier</key><string>" + identifier + "</string>\n" +
				"<key>CFBundleName</key><string>" + SecurityElement.Escape(name) + "</string>\n" +
				"<key>CFBundlePackageType</key><string>APPL</string>\n" +
				"<key>CFBundleInfoDictionaryVersion</key><string>6.0</string>\n" +
				"<key>LSUIElement</key><true/>\n" +
				"</dict></plist>\n");
			string launcher = Path.Combine(macOS, "launch");
			File.WriteAllText(launcher, "#!/bin/sh\n" + BuildCommand(executable, arguments) + "\n");
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
			Delete(shortcutPath);
			Directory.Move(staging, shortcutPath);
		}
		finally
		{
			if (Directory.Exists(staging))
				Directory.Delete(staging, true);
		}
		ApplyIcon(shortcutPath, iconPath, BundleOf(executable) ?? executable);
	}

	private static string BuildCommand(string executable, string arguments)
	{
		IEnumerable<string> tokens = Split(arguments).Select(Quote);
		string? bundle = BundleOf(executable);
		return bundle != null
			? "exec /usr/bin/open -n -a " + Quote(bundle) + " --args " + string.Join(' ', tokens)
			: "exec " + Quote(executable) + " " + string.Join(' ', tokens);
	}

	private static List<string> Split(string arguments)
	{
		List<string> tokens = new();
		StringBuilder current = new();
		bool quoted = false;
		bool any = false;
		foreach (char c in arguments)
		{
			if (c == '"')
			{
				quoted = !quoted;
				any = true;
			}
			else if (char.IsWhiteSpace(c) && !quoted)
			{
				if (any)
					tokens.Add(current.ToString());
				current.Clear();
				any = false;
			}
			else
			{
				current.Append(c);
				any = true;
			}
		}
		if (any)
			tokens.Add(current.ToString());
		return tokens;
	}

	private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

	private static string? BundleOf(string executable)
	{
		int index = executable.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
		return index < 0 ? null : executable[..(index + 4)];
	}

	private static void ApplyIcon(string shortcutPath, string? iconPath, string fallback)
	{
		lock (Gate)
		{
			if (!OperatingSystem.IsMacOS())
				return;
			nint pool = objc_autoreleasePoolPush();
			try
			{
				if (_appKit == 0)
					_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
				nint workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
				nint image = 0;
				if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath) && Path.GetExtension(iconPath).ToLowerInvariant() is ".png" or ".ico" or ".icns" or ".jpg" or ".jpeg")
				{
					nint allocated = Send(objc_getClass("NSImage"), sel_registerName("alloc"));
					image = SendObject(allocated, sel_registerName("initWithContentsOfFile:"), NSString(iconPath));
					if (image != 0)
						Send(image, sel_registerName("autorelease"));
				}
				if (image == 0)
					image = SendObject(workspace, sel_registerName("iconForFile:"), NSString(fallback));
				if (image != 0)
					SetIcon(workspace, sel_registerName("setIcon:forFile:options:"), image, NSString(shortcutPath), 0);
			}
			catch
			{
			}
			finally
			{
				objc_autoreleasePoolPop(pool);
			}
		}
	}

	public static bool SetCustomIcon(string path, string? pngPath)
	{
		lock (Gate)
		{
			if (!OperatingSystem.IsMacOS() || !Directory.Exists(path) && !File.Exists(path))
				return false;
			nint pool = objc_autoreleasePoolPush();
			try
			{
				if (_appKit == 0)
					_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
				nint workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
				nint image = 0;
				if (!string.IsNullOrEmpty(pngPath) && File.Exists(pngPath))
				{
					image = SendObject(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithContentsOfFile:"), NSString(pngPath));
					if (image == 0)
						return false;
					Send(image, sel_registerName("autorelease"));
				}
				return SetIcon(workspace, sel_registerName("setIcon:forFile:options:"), image, NSString(path), 0);
			}
			catch
			{
				return false;
			}
			finally
			{
				objc_autoreleasePoolPop(pool);
			}
		}
	}

	private static nint NSString(string value) => SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), value);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint objc_getClass(string name);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint sel_registerName(string name);

	[LibraryImport(ObjectiveC)]
	private static partial nint objc_autoreleasePoolPush();

	[LibraryImport(ObjectiveC)]
	private static partial void objc_autoreleasePoolPop(nint pool);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint Send(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendObject(nint receiver, nint selector, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint SendString(nint receiver, nint selector, string argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SetIcon(nint receiver, nint selector, nint image, nint path, nuint options);
}
