using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static partial class MacOSProcessPolicy
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const int PrioDarwinProcess = 4;
	private const int PrioDarwinBackground = 0x1000;
	private static nint _appKit;

	public static int FrontmostProcessId()
	{
		if (!OperatingSystem.IsMacOS())
			return 0;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			if (_appKit == 0)
				_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
			nint workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
			nint application = workspace == 0 ? 0 : Send(workspace, sel_registerName("frontmostApplication"));
			return application == 0 ? 0 : SendInt(application, sel_registerName("processIdentifier"));
		}
		catch
		{
			return 0;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public static bool SetBackground(int processId, bool background)
	{
		if (!OperatingSystem.IsMacOS() || processId <= 0)
			return false;
		return setpriority(PrioDarwinProcess, (uint)processId, background ? PrioDarwinBackground : 0) == 0;
	}

	[LibraryImport("libc", SetLastError = true)]
	private static partial int setpriority(int which, uint who, int prio);

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
	private static partial int SendInt(nint receiver, nint selector);
}
