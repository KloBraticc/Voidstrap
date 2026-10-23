using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public static unsafe partial class MacOSColorSampler
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const int BlockHasCopyDispose = 1 << 25;

	[StructLayout(LayoutKind.Sequential)]
	private struct BlockDescriptor
	{
		public nuint Reserved;
		public nuint Size;
		public nint Copy;
		public nint Dispose;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BlockLiteral
	{
		public nint Isa;
		public int Flags;
		public int Reserved;
		public nint Invoke;
		public BlockDescriptor* Descriptor;
		public nint Context;
	}

	private sealed class SampleRequest
	{
		public readonly TaskCompletionSource<(byte R, byte G, byte B)?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public nint Sampler;
	}

	private static readonly object Gate = new();
	private static BlockDescriptor* _descriptor;
	private static nint _stackBlock;
	private static bool? _available;

	public static bool IsAvailable
	{
		get
		{
			if (!OperatingSystem.IsMacOS())
				return false;
			lock (Gate)
			{
				_available ??= EnsureLoaded() && objc_getClass("NSColorSampler") != 0;
				return _available.Value;
			}
		}
	}

	public static Task<(byte R, byte G, byte B)?> PickAsync()
	{
		if (!IsAvailable)
			return Task.FromResult<(byte, byte, byte)?>(null);
		SampleRequest request = new();
		nint sampler = Send(Send(objc_getClass("NSColorSampler"), sel_registerName("alloc")), sel_registerName("init"));
		if (sampler == 0)
			return Task.FromResult<(byte, byte, byte)?>(null);
		request.Sampler = sampler;
		nint block;
		try
		{
			block = CreateBlock(request);
		}
		catch (InvalidOperationException)
		{
			objc_release(sampler);
			return Task.FromResult<(byte, byte, byte)?>(null);
		}
		try
		{
			SendObject(sampler, sel_registerName("showSamplerWithSelectionHandler:"), block);
		}
		finally
		{
			BlockRelease(block);
		}
		return request.Result.Task;
	}

	[UnmanagedCallersOnly]
	private static void OnColor(nint block, nint color)
	{
		SampleRequest request = (SampleRequest)GCHandle.FromIntPtr(((BlockLiteral*)block)->Context).Target!;
		(byte, byte, byte)? result = null;
		try
		{
			if (color != 0)
			{
				nint space = Send(objc_getClass("NSColorSpace"), sel_registerName("sRGBColorSpace"));
				nint converted = space == 0 ? 0 : SendObject(color, sel_registerName("colorUsingColorSpace:"), space);
				if (converted != 0)
				{
					result = (
						ToByte(SendReturnsDouble(converted, sel_registerName("redComponent"))),
						ToByte(SendReturnsDouble(converted, sel_registerName("greenComponent"))),
						ToByte(SendReturnsDouble(converted, sel_registerName("blueComponent"))));
				}
			}
		}
		finally
		{
			nint sampler = request.Sampler;
			request.Sampler = 0;
			if (sampler != 0)
				objc_release(sampler);
			request.Result.TrySetResult(result);
		}
	}

	private static byte ToByte(double component)
	{
		return (byte)Math.Round(Math.Clamp(component, 0, 1) * 255);
	}

	private static bool EnsureLoaded()
	{
		if (_descriptor != null)
			return true;
		try
		{
			NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
			nint system = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
			_stackBlock = NativeLibrary.GetExport(system, "_NSConcreteStackBlock");
			_descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
			_descriptor->Size = (nuint)sizeof(BlockLiteral);
			_descriptor->Copy = (nint)(delegate* unmanaged<nint, nint, void>)&CopyBlock;
			_descriptor->Dispose = (nint)(delegate* unmanaged<nint, void>)&DisposeBlock;
			return true;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			return false;
		}
	}

	private static nint CreateBlock(SampleRequest request)
	{
		GCHandle handle = GCHandle.Alloc(request);
		BlockLiteral block = new()
		{
			Isa = _stackBlock,
			Flags = BlockHasCopyDispose,
			Invoke = (nint)(delegate* unmanaged<nint, nint, void>)&OnColor,
			Descriptor = _descriptor,
			Context = GCHandle.ToIntPtr(handle)
		};
		nint copied = BlockCopy((nint)(&block));
		if (copied != 0)
			return copied;
		handle.Free();
		throw new InvalidOperationException("The colour sampler callback could not be allocated");
	}

	[UnmanagedCallersOnly]
	private static void CopyBlock(nint destination, nint source)
	{
	}

	[UnmanagedCallersOnly]
	private static void DisposeBlock(nint block) => GCHandle.FromIntPtr(((BlockLiteral*)block)->Context).Free();

	[LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_copy")]
	private static partial nint BlockCopy(nint block);

	[LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_release")]
	private static partial void BlockRelease(nint block);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint objc_getClass(string name);

	[LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint sel_registerName(string name);

	[LibraryImport(ObjectiveC)]
	private static partial void objc_release(nint value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint Send(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendObject(nint receiver, nint selector, nint argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial double SendReturnsDouble(nint receiver, nint selector);
}
