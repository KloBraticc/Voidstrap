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
	private static unsafe partial int DecodeBgra(
		byte* data,
		nuint length,
		uint maxWidth,
		ulong maxBytes,
		out uint width,
		out uint height,
		out IntPtr pixels,
		out nuint pixelLength
	);

	[LibraryImport(Library, EntryPoint = "vi_resize_bgra")]
	private static unsafe partial int ResizeBgra(
		byte* pixels,
		nuint length,
		uint width,
		uint height,
		uint newWidth,
		uint newHeight,
		uint filter,
		out IntPtr result,
		out nuint resultLength
	);

	[LibraryImport(Library, EntryPoint = "vi_encode_bgra")]
	private static unsafe partial int EncodeBgra(
		byte* pixels,
		nuint length,
		uint width,
		uint height,
		uint format,
		uint level,
		out IntPtr result,
		out nuint resultLength
	);

	[LibraryImport(Library, EntryPoint = "vi_identify")]
	private static unsafe partial int Identify(byte* data, nuint length, out uint width, out uint height);

	[LibraryImport(Library, EntryPoint = "vi_decode_animation_bgra")]
	private static unsafe partial int DecodeAnimationBgra(
		byte* data,
		nuint length,
		uint maxWidth,
		uint maxHeight,
		uint maxFrames,
		ulong maxBytes,
		out uint width,
		out uint height,
		out IntPtr pixels,
		out nuint pixelLength,
		out IntPtr delays,
		out nuint delayLength
	);

	[LibraryImport(Library, EntryPoint = "vi_encode_gif_frames")]
	private static unsafe partial int EncodeGifFrames(
		byte* pixels,
		nuint length,
		uint width,
		uint height,
		uint count,
		uint delayMs,
		out IntPtr result,
		out nuint resultLength
	);

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
				resolved = Version() >= 5 ? StateReady : StateMissing;
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
				status = DecodeBgra(
					data,
					(nuint)bytes.Length,
					(uint)Math.Max(0, maxWidth),
					(ulong)maxBytes,
					out decodedWidth,
					out decodedHeight,
					out native,
					out length
				);
			if (status != 0 || native == IntPtr.Zero)
				return false;
			ulong expected = (ulong)decodedWidth * decodedHeight * 4;
			if (
				decodedWidth == 0
				|| decodedHeight == 0
				|| decodedWidth > int.MaxValue
				|| decodedHeight > int.MaxValue
				|| expected != length
				|| expected > (ulong)maxBytes
				|| expected > int.MaxValue
			)
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

	public static unsafe bool TryResizeBgra(
		byte[] pixels,
		int width,
		int height,
		int newWidth,
		int newHeight,
		int filter,
		out byte[] result
	)
	{
		result = Array.Empty<byte>();
		if (
			pixels == null
			|| width <= 0
			|| height <= 0
			|| newWidth <= 0
			|| newHeight <= 0
			|| pixels.LongLength != (long)width * height * 4
			|| !IsAvailable
		)
			return false;
		IntPtr native = IntPtr.Zero;
		nuint length = 0;
		try
		{
			int status;
			fixed (byte* data = pixels)
				status = ResizeBgra(
					data,
					(nuint)pixels.Length,
					(uint)width,
					(uint)height,
					(uint)newWidth,
					(uint)newHeight,
					(uint)filter,
					out native,
					out length
				);
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
				status = EncodeBgra(
					data,
					(nuint)pixels.Length,
					(uint)width,
					(uint)height,
					(uint)format,
					(uint)level,
					out native,
					out length
				);
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

	public static unsafe bool TryIdentify(byte[] bytes, out int width, out int height)
	{
		width = 0;
		height = 0;
		if (bytes == null || bytes.Length == 0 || !IsAvailable)
			return false;
		try
		{
			int status;
			uint identifiedWidth;
			uint identifiedHeight;
			fixed (byte* data = bytes)
				status = Identify(data, (nuint)bytes.Length, out identifiedWidth, out identifiedHeight);
			if (
				status != 0
				|| identifiedWidth == 0
				|| identifiedHeight == 0
				|| identifiedWidth > int.MaxValue
				|| identifiedHeight > int.MaxValue
			)
				return false;
			width = (int)identifiedWidth;
			height = (int)identifiedHeight;
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("NativeImaging", "Native identify failed: " + ex.Message);
			return false;
		}
	}

	public static unsafe bool TryDecodeAnimationBgra(
		byte[] bytes,
		int maxWidth,
		int maxHeight,
		int maxFrames,
		long maxBytes,
		out int width,
		out int height,
		out byte[][] frames,
		out int[] delays
	)
	{
		width = 0;
		height = 0;
		frames = Array.Empty<byte[]>();
		delays = Array.Empty<int>();
		if (bytes == null || bytes.Length == 0 || maxBytes <= 0 || maxFrames <= 0 || !IsAvailable)
			return false;
		IntPtr native = IntPtr.Zero;
		nuint length = 0;
		IntPtr nativeDelays = IntPtr.Zero;
		nuint delayLength = 0;
		try
		{
			int status;
			uint decodedWidth;
			uint decodedHeight;
			fixed (byte* data = bytes)
				status = DecodeAnimationBgra(
					data,
					(nuint)bytes.Length,
					(uint)Math.Max(0, maxWidth),
					(uint)Math.Max(0, maxHeight),
					(uint)maxFrames,
					(ulong)maxBytes,
					out decodedWidth,
					out decodedHeight,
					out native,
					out length,
					out nativeDelays,
					out delayLength
				);
			if (
				status != 0
				|| native == IntPtr.Zero
				|| nativeDelays == IntPtr.Zero
				|| decodedWidth == 0
				|| decodedHeight == 0
				|| decodedWidth > int.MaxValue
				|| decodedHeight > int.MaxValue
			)
				return false;
			ulong frameBytes = (ulong)decodedWidth * decodedHeight * 4;
			ulong count = (ulong)delayLength / 4;
			if (
				count == 0
				|| count > (ulong)maxFrames
				|| (ulong)delayLength != count * 4
				|| (ulong)length != frameBytes * count
				|| (ulong)length > (ulong)maxBytes
				|| frameBytes > int.MaxValue
			)
				return false;
			byte[][] decodedFrames = new byte[count][];
			int[] decodedDelays = new int[count];
			for (int index = 0; index < (int)count; index++)
			{
				byte[] frame = GC.AllocateUninitializedArray<byte>((int)frameBytes);
				Marshal.Copy(IntPtr.Add(native, checked((int)(frameBytes * (ulong)index))), frame, 0, frame.Length);
				decodedFrames[index] = frame;
				decodedDelays[index] = Marshal.ReadInt32(nativeDelays, index * 4);
			}
			width = (int)decodedWidth;
			height = (int)decodedHeight;
			frames = decodedFrames;
			delays = decodedDelays;
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("NativeImaging", "Native animation decode failed: " + ex.Message);
			return false;
		}
		finally
		{
			if (native != IntPtr.Zero)
				Free(native, length);
			if (nativeDelays != IntPtr.Zero)
				Free(nativeDelays, delayLength);
		}
	}

	public static unsafe bool TryEncodeGifFrames(byte[] pixels, int width, int height, int count, int delayMs, out byte[] encoded)
	{
		encoded = Array.Empty<byte>();
		if (
			pixels == null
			|| width <= 0
			|| height <= 0
			|| count <= 0
			|| pixels.LongLength != (long)width * height * 4 * count
			|| !IsAvailable
		)
			return false;
		IntPtr native = IntPtr.Zero;
		nuint length = 0;
		try
		{
			int status;
			fixed (byte* data = pixels)
				status = EncodeGifFrames(
					data,
					(nuint)pixels.Length,
					(uint)width,
					(uint)height,
					(uint)count,
					(uint)Math.Max(0, delayMs),
					out native,
					out length
				);
			if (status != 0 || native == IntPtr.Zero || length == 0 || length > int.MaxValue)
				return false;
			encoded = GC.AllocateUninitializedArray<byte>((int)length);
			Marshal.Copy(native, encoded, 0, encoded.Length);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("NativeImaging", "Native GIF encode failed: " + ex.Message);
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
