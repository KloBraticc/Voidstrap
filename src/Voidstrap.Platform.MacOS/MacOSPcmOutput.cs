using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public sealed unsafe partial class MacOSPcmOutput : IDisposable
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private static readonly Lazy<bool> Loaded = new(LoadFramework, LazyThreadSafetyMode.ExecutionAndPublication);

	private readonly object _gate = new();
	private readonly int _channels;
	private nint _engine;
	private nint _player;
	private nint _format;
	private long _scheduledFrames;
	private float _volume = 1f;

	private MacOSPcmOutput(int channels)
	{
		_channels = channels;
	}

	public long QueuedFrames
	{
		get
		{
			lock (_gate)
				return Math.Max(0, _scheduledFrames - PlayedFrames());
		}
	}

	public float Volume
	{
		get => _volume;
		set
		{
			_volume = Math.Clamp(value, 0f, 1f);
			lock (_gate)
			{
				if (_player != 0)
					SetFloat(_player, sel_registerName("setVolume:"), _volume);
			}
		}
	}

	public static MacOSPcmOutput? Create(int sampleRate, int channels, out string? error)
	{
		error = null;
		if (!OperatingSystem.IsMacOS() || !Loaded.Value)
		{
			error = "AVFoundation is not available";
			return null;
		}
		MacOSPcmOutput output = new(channels);
		nint pool = objc_autoreleasePoolPush();
		try
		{
			output._format = InitFormat(Send(objc_getClass("AVAudioFormat"), sel_registerName("alloc")), sel_registerName("initStandardFormatWithSampleRate:channels:"), sampleRate, (uint)channels);
			output._engine = Send(Send(objc_getClass("AVAudioEngine"), sel_registerName("alloc")), sel_registerName("init"));
			output._player = Send(Send(objc_getClass("AVAudioPlayerNode"), sel_registerName("alloc")), sel_registerName("init"));
			if (output._format == 0 || output._engine == 0 || output._player == 0)
			{
				error = "The audio output could not be created";
				output.Dispose();
				return null;
			}
			SendObject(output._engine, sel_registerName("attachNode:"), output._player);
			Connect(output._engine, sel_registerName("connect:to:format:"), output._player, Send(output._engine, sel_registerName("mainMixerNode")), output._format);
			if (!StartEngine(output._engine, sel_registerName("startAndReturnError:"), out _))
			{
				error = "The audio output could not be started";
				output.Dispose();
				return null;
			}
			return output;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public void Enqueue(ReadOnlySpan<float> interleaved)
	{
		int frames = interleaved.Length / _channels;
		if (frames <= 0)
			return;
		lock (_gate)
		{
			if (_player == 0)
				return;
			nint pool = objc_autoreleasePoolPush();
			try
			{
				nint buffer = InitBuffer(Send(objc_getClass("AVAudioPCMBuffer"), sel_registerName("alloc")), sel_registerName("initWithPCMFormat:frameCapacity:"), _format, (uint)frames);
				if (buffer == 0)
					return;
				float** channels = (float**)Send(buffer, sel_registerName("floatChannelData"));
				for (int channel = 0; channel < _channels; channel++)
				{
					float* target = channels[channel];
					for (int frame = 0; frame < frames; frame++)
						target[frame] = interleaved[frame * _channels + channel];
				}
				SetUInt(buffer, sel_registerName("setFrameLength:"), (uint)frames);
				ScheduleBuffer(_player, sel_registerName("scheduleBuffer:completionHandler:"), buffer, 0);
				Send(buffer, sel_registerName("release"));
				_scheduledFrames += frames;
			}
			finally
			{
				objc_autoreleasePoolPop(pool);
			}
		}
	}

	public void Play()
	{
		lock (_gate)
		{
			if (_player != 0)
			{
				SetFloat(_player, sel_registerName("setVolume:"), _volume);
				Send(_player, sel_registerName("play"));
			}
		}
	}

	public void Pause()
	{
		lock (_gate)
		{
			if (_player != 0)
				Send(_player, sel_registerName("pause"));
		}
	}

	public void Flush()
	{
		lock (_gate)
		{
			if (_player == 0)
				return;
			Send(_player, sel_registerName("stop"));
			_scheduledFrames = 0;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			nint pool = objc_autoreleasePoolPush();
			try
			{
				if (_player != 0)
					Send(_player, sel_registerName("stop"));
				if (_engine != 0)
					Send(_engine, sel_registerName("stop"));
				foreach (nint handle in new[] { _player, _engine, _format })
				{
					if (handle != 0)
						Send(handle, sel_registerName("release"));
				}
			}
			finally
			{
				objc_autoreleasePoolPop(pool);
			}
			_player = _engine = _format = 0;
			_scheduledFrames = 0;
		}
	}

	private long PlayedFrames()
	{
		if (_player == 0)
			return 0;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint nodeTime = Send(_player, sel_registerName("lastRenderTime"));
			nint playerTime = nodeTime == 0 ? 0 : SendObject(_player, sel_registerName("playerTimeForNodeTime:"), nodeTime);
			return playerTime == 0 || !SendReturnsBool(playerTime, sel_registerName("isSampleTimeValid"))
				? 0
				: SendLong(playerTime, sel_registerName("sampleTime"));
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	private static bool LoadFramework()
	{
		try
		{
			NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
			return true;
		}
		catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
		{
			return false;
		}
	}

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

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetFloat(nint receiver, nint selector, float value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetUInt(nint receiver, nint selector, uint value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial long SendLong(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendReturnsBool(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool StartEngine(nint receiver, nint selector, out nint error);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint InitFormat(nint receiver, nint selector, double sampleRate, uint channels);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint InitBuffer(nint receiver, nint selector, nint format, uint frameCapacity);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void Connect(nint receiver, nint selector, nint source, nint destination, nint format);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void ScheduleBuffer(nint receiver, nint selector, nint buffer, nint completion);
}
