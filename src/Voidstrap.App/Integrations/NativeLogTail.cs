using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Voidstrap.Integrations;

internal sealed partial class NativeLogTail : IDisposable
{
	private const string Library = "voidlog";

	private const int StateUnknown = 0;
	private const int StateReady = 1;
	private const int StateMissing = 2;

	private static int _state;

	internal enum Result
	{
		Line,
		Idle,
		Restarted,
		Failed
	}

	private IntPtr _handle;
	private byte[] _buffer = new byte[16 * 1024];

	[LibraryImport(Library, EntryPoint = "voidlog_version")]
	private static partial uint Version();

	[LibraryImport(Library, EntryPoint = "voidlog_open")]
	private static unsafe partial IntPtr OpenNative(byte* path, nuint pathLength, byte* markers, nuint markersLength);

	[LibraryImport(Library, EntryPoint = "voidlog_next")]
	private static unsafe partial int NextNative(IntPtr handle, byte* buffer, nuint capacity, out nuint length);

	[LibraryImport(Library, EntryPoint = "voidlog_close")]
	private static partial void CloseNative(IntPtr handle);

	public static bool IsAvailable
	{
		get
		{
			int state = Volatile.Read(ref _state);
			if (state != StateUnknown)
				return state == StateReady;
			try
			{
				bool ok = Version() == 1;
				Volatile.Write(ref _state, ok ? StateReady : StateMissing);
				return ok;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				App.Logger?.WriteLine("NativeLogTail", $"Native voidlog library is unavailable ({ex.GetType().Name}), reading the Roblox log in managed code");
				Volatile.Write(ref _state, StateMissing);
				return false;
			}
		}
	}

	private NativeLogTail(IntPtr handle)
	{
		_handle = handle;
	}

	public static unsafe NativeLogTail? Open(string path, string[] markers)
	{
		if (!IsAvailable)
			return null;
		byte[] pathBytes = Encoding.UTF8.GetBytes(path);
		byte[] markerBytes = Encoding.UTF8.GetBytes(string.Join('\n', markers));
		IntPtr handle;
		fixed (byte* pathPointer = pathBytes)
		fixed (byte* markerPointer = markerBytes)
			handle = OpenNative(pathPointer, (nuint)pathBytes.Length, markerPointer, (nuint)markerBytes.Length);
		return handle == IntPtr.Zero ? null : new NativeLogTail(handle);
	}

	public unsafe Result Next(out string? line)
	{
		line = null;
		if (_handle == IntPtr.Zero)
			return Result.Failed;
		while (true)
		{
			int status;
			nuint length;
			fixed (byte* buffer = _buffer)
				status = NextNative(_handle, buffer, (nuint)_buffer.Length, out length);
			switch (status)
			{
				case 1:
					line = Encoding.UTF8.GetString(_buffer, 0, (int)length);
					return Result.Line;
				case 0:
					return Result.Idle;
				case 2:
					return Result.Restarted;
				case 3:
					_buffer = new byte[(int)length];
					continue;
				default:
					return Result.Failed;
			}
		}
	}

	public void Dispose()
	{
		if (_handle == IntPtr.Zero)
			return;
		CloseNative(_handle);
		_handle = IntPtr.Zero;
	}
}
