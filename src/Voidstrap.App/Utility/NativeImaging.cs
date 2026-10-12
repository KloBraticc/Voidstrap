using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Voidstrap.Utility;

internal static partial class NativeImaging
{
	private const string Library = "voidimage";

	private const int StateUnknown = 0;
	private const int StateReady = 1;
	private const int StateMissing = 2;

	private static int _state;

	[LibraryImport(Library, EntryPoint = "vi_version")]
	private static partial uint Version();

	[LibraryImport(Library, EntryPoint = "vi_decode_bgra")]
	private static unsafe partial int DecodeBgra(byte* data, nuint length, uint maxWidth, ulong maxBytes, out uint width, out uint height, out IntPtr pixels, out nuint pixelLength);

	[LibraryImport(Library, EntryPoint = "vi_resize_bgra")]
	private static unsafe partial int ResizeBgra(byte* pixels, nuint length, uint width, uint height, uint newWidth, uint newHeight, uint filter, out IntPtr result, out nuint resultLength);

	[LibraryImport(Library, EntryPoint = "vi_encode_bgra")]
	private static unsafe partial int EncodeBgra(byte* pixels, nuint length, uint width, uint height, uint format, uint level, out IntPtr result, out nuint resultLength);

	[LibraryImport(Library, EntryPoint = "vi_free")]
	private static partial void Free(IntPtr pixels, nuint length);

	public static bool IsAvailable
	{
		get
		{
			int state = Volatile.Read(ref _state);
			if (state != StateUnknown)
				return state == StateReady;
			int resolved;
			try
			{
				resolved = Version() >= 2 ? StateReady : StateMissing;
			}
			catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
			{
				resolved = StateMissing;
			}
			Interlocked.CompareExchange(ref _state, resolved, StateUnknown);
			if (resolved == StateMissing)
				App.Logger?.WriteLine("NativeImaging", "Native image library is not available, using the managed decoder");
			return resolved == StateReady;
		}
	}

	public static unsafe bool TryDecodeBgra(byte[] bytes, int maxWidth, long maxBytes, out int width, out int height, out byte[] pixels)
	{
		width = 0;
		height = 0;
		pixels = Array.Empty<byte>();
		if (bytes == null || bytes.Length == 0 || maxBytes <= 0 || !IsAvailable)
			return false;
		IntPtr native = IntPtr.Zero;
		nuint length = 0;
		try
		{
			int status;
			uint decodedWidth;
			uint decodedHeight;
			fixed (byte* data = bytes)
				status = DecodeBgra(data, (nuint)bytes.Length, (uint)Math.Max(0, maxWidth), (ulong)maxBytes, out decodedWidth, out decodedHeight, out native, out length);
			if (status != 0 || native == IntPtr.Zero)
				return false;
			ulong expected = (ulong)decodedWidth * decodedHeight * 4;
			if (decodedWidth == 0 || decodedHeight == 0 || decodedWidth > int.MaxValue || decodedHeight > int.MaxValue || expected != length || expected > (ulong)maxBytes || expected > int.MaxValue)
				return false;
			byte[] managed = GC.AllocateUninitializedArray<byte>((int)expected);
			Marshal.Copy(native, managed, 0, managed.Length);
			width = (int)decodedWidth;
			height = (int)decodedHeight;
			pixels = managed;
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("NativeImaging", "Native decode failed: " + ex.Message);
			return false;
		}
		finally
		{
			if (native != IntPtr.Zero)
				Free(native, length);
		}
	}
	public static unsafe bool TryResizeBgra(byte[] pixels, int width, int height, int newWidth, int newHeight, int filter, out byte[] result)
	{
		result = Array.Empty<byte>();
		if (pixels == null || width <= 0 || height <= 0 || newWidth <= 0 || newHeight <= 0 || pixels.LongLength != (long)width * height * 4 || !IsAvailable)
			return false;
		IntPtr native = IntPtr.Zero;
		nuint length = 0;
		try
		{
			int status;
			fixed (byte* data = pixels)
				status = ResizeBgra(data, (nuint)pixels.Length, (uint)width, (uint)height, (uint)newWidth, (uint)newHeight, (uint)filter, out native, out length);
			long expected = (long)newWidth * newHeight * 4;
			if (status != 0 || native == IntPtr.Zero || (long)length != expected || expected > int.MaxValue)
				return false;
			result = GC.AllocateUninitializedArray<byte>((int)expected);
			Marshal.Copy(native, result, 0, result.Length);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("NativeImaging", "Native resize failed: " + ex.Message);
			result = Array.Empty<byte>();
			return false;
		}
		finally
		{
			if (native != IntPtr.Zero)
				Free(native, length);
		}
	}

	public static unsafe bool TryEncodeBgra(byte[] pixels, int width, int height, int format, int level, out byte[] encoded)
	{
		encoded = Array.Empty<byte>();
		if (pixels == null || width <= 0 || height <= 0 || pixels.LongLength != (long)width * height * 4 || !IsAvailable)
			return false;
		IntPtr native = IntPtr.Zero;
		nuint length = 0;
		try
		{
			int status;
			fixed (byte* data = pixels)
				status = EncodeBgra(data, (nuint)pixels.Length, (uint)width, (uint)height, (uint)format, (uint)level, out native, out length);
			if (status != 0 || native == IntPtr.Zero || length == 0 || length > int.MaxValue)
				return false;
			encoded = GC.AllocateUninitializedArray<byte>((int)length);
			Marshal.Copy(native, encoded, 0, encoded.Length);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("NativeImaging", "Native encode failed: " + ex.Message);
			encoded = Array.Empty<byte>();
			return false;
		}
		finally
		{
			if (native != IntPtr.Zero)
				Free(native, length);
		}
	}
}
