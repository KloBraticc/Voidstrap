using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public sealed partial class MacOSAudioPlayer : IDisposable
{
	private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
	private const string AVFoundationPath = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
	private static readonly float[] BandFrequencies = [31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];
	private static readonly Lazy<string?> LoadError = new(LoadFramework, LazyThreadSafetyMode.ExecutionAndPublication);

	private readonly object _gate = new();
	private readonly float[] _gains = new float[BandFrequencies.Length];
	private nint _engine;
	private nint _player;
	private nint _equalizer;
	private nint _file;
	private double _sampleRate;
	private long _frames;
	private long _segmentStart;
	private long _pausedFrame;
	private bool _playing;
	private bool _equalizerEnabled;
	private float _volume = 1f;

	public static int BandCount => BandFrequencies.Length;

	public bool IsLoaded => _player != 0;

	public bool IsPlaying => _playing;

	public TimeSpan Duration => _sampleRate > 0 ? TimeSpan.FromSeconds(_frames / _sampleRate) : TimeSpan.Zero;

	public TimeSpan Position
	{
		get
		{
			lock (_gate)
				return _sampleRate > 0 ? TimeSpan.FromSeconds(CurrentFrame() / _sampleRate) : TimeSpan.Zero;
		}
	}

	public bool EqualizerEnabled
	{
		get => _equalizerEnabled;
		set
		{
			_equalizerEnabled = value;
			ApplyEqualizer();
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

	public static TimeSpan ProbeDuration(string path)
	{
		if (!OperatingSystem.IsMacOS() || LoadError.Value != null || !File.Exists(path))
			return TimeSpan.Zero;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint file = OpenFile(path, out _);
			if (file == 0)
				return TimeSpan.Zero;
			double rate = SendDouble(Send(file, sel_registerName("processingFormat")), sel_registerName("sampleRate"));
			long frames = SendLong(file, sel_registerName("length"));
			Send(file, sel_registerName("release"));
			return rate > 0 ? TimeSpan.FromSeconds(frames / rate) : TimeSpan.Zero;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public string? Load(string path)
	{
		Unload();
		if (!OperatingSystem.IsMacOS())
			return "Native audio is only available on macOS";
		if (LoadError.Value is string loadError)
			return loadError;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint file = OpenFile(path, out string? openError);
			if (file == 0)
				return openError ?? "This file could not be opened";
			nint format = Send(file, sel_registerName("processingFormat"));
			double rate = SendDouble(format, sel_registerName("sampleRate"));
			long frames = SendLong(file, sel_registerName("length"));

			nint engine = Send(Send(objc_getClass("AVAudioEngine"), sel_registerName("alloc")), sel_registerName("init"));
			nint player = Send(Send(objc_getClass("AVAudioPlayerNode"), sel_registerName("alloc")), sel_registerName("init"));
			nint equalizer = SendIndex(Send(objc_getClass("AVAudioUnitEQ"), sel_registerName("alloc")), sel_registerName("initWithNumberOfBands:"), (nuint)BandFrequencies.Length);
			SendObject(engine, sel_registerName("attachNode:"), player);
			SendObject(engine, sel_registerName("attachNode:"), equalizer);
			Connect(engine, sel_registerName("connect:to:format:"), player, equalizer, format);
			Connect(engine, sel_registerName("connect:to:format:"), equalizer, Send(engine, sel_registerName("mainMixerNode")), format);

			nint bands = Send(equalizer, sel_registerName("bands"));
			for (int index = 0; index < BandFrequencies.Length; index++)
			{
				nint band = SendIndex(bands, sel_registerName("objectAtIndex:"), (nuint)index);
				SetIndex(band, sel_registerName("setFilterType:"), 0);
				SetFloat(band, sel_registerName("setFrequency:"), BandFrequencies[index]);
				SetFloat(band, sel_registerName("setBandwidth:"), 1f);
				SetBool(band, sel_registerName("setBypass:"), false);
			}

			if (!SendReturnsBoolError(engine, sel_registerName("startAndReturnError:"), out nint error))
			{
				string message = Describe(error) ?? "The audio output could not be started";
				Send(engine, sel_registerName("release"));
				Send(player, sel_registerName("release"));
				Send(equalizer, sel_registerName("release"));
				Send(file, sel_registerName("release"));
				return message;
			}

			lock (_gate)
			{
				_engine = engine;
				_player = player;
				_equalizer = equalizer;
				_file = file;
				_sampleRate = rate;
				_frames = frames;
				_segmentStart = 0;
				_pausedFrame = 0;
				_playing = false;
				SetFloat(_player, sel_registerName("setVolume:"), _volume);
				Schedule(0);
			}
			ApplyEqualizer();
			return null;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return "AVFoundation could not be used: " + ex.Message;
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	public void Play()
	{
		lock (_gate)
		{
			if (_player == 0)
				return;
			if (_pausedFrame >= _frames)
				Seek(0);
			Send(_player, sel_registerName("play"));
			_playing = true;
		}
	}

	public void Pause()
	{
		lock (_gate)
		{
			if (_player == 0)
				return;
			_pausedFrame = CurrentFrame();
			Send(_player, sel_registerName("pause"));
			_playing = false;
		}
	}

	public void SeekTo(TimeSpan position)
	{
		lock (_gate)
		{
			if (_player == 0 || _sampleRate <= 0)
				return;
			Seek((long)Math.Clamp(position.TotalSeconds * _sampleRate, 0, _frames));
		}
	}

	public bool HasFinished()
	{
		lock (_gate)
		{
			if (_player == 0 || !_playing || CurrentFrame() < _frames)
				return false;
			_playing = false;
			_pausedFrame = _frames;
			Send(_player, sel_registerName("pause"));
			return true;
		}
	}

	public void SetBandGain(int band, float gainDb)
	{
		if (band < 0 || band >= _gains.Length)
			return;
		_gains[band] = gainDb;
		ApplyEqualizer();
	}

	public void Unload()
	{
		lock (_gate)
		{
			if (_engine == 0)
				return;
			nint pool = objc_autoreleasePoolPush();
			try
			{
				Send(_player, sel_registerName("stop"));
				Send(_engine, sel_registerName("stop"));
				Send(_player, sel_registerName("release"));
				Send(_equalizer, sel_registerName("release"));
				Send(_engine, sel_registerName("release"));
				Send(_file, sel_registerName("release"));
			}
			finally
			{
				objc_autoreleasePoolPop(pool);
			}
			_engine = _player = _equalizer = _file = 0;
			_sampleRate = 0;
			_frames = 0;
			_playing = false;
		}
	}

	public void Dispose() => Unload();

	private void Seek(long frame)
	{
		bool resume = _playing;
		Send(_player, sel_registerName("stop"));
		Schedule(frame);
		_pausedFrame = frame;
		if (resume)
			Send(_player, sel_registerName("play"));
	}

	private void Schedule(long frame)
	{
		_segmentStart = frame;
		long remaining = _frames - frame;
		if (remaining <= 0)
			return;
		ScheduleSegment(_player, sel_registerName("scheduleSegment:startingFrame:frameCount:atTime:completionHandler:"), _file, frame, (uint)Math.Min(remaining, uint.MaxValue), 0, 0);
	}

	private long CurrentFrame()
	{
		if (_player == 0)
			return 0;
		if (!_playing)
			return _pausedFrame;
		nint pool = objc_autoreleasePoolPush();
		try
		{
			nint nodeTime = Send(_player, sel_registerName("lastRenderTime"));
			nint playerTime = nodeTime == 0 ? 0 : SendObject(_player, sel_registerName("playerTimeForNodeTime:"), nodeTime);
			if (playerTime == 0 || !SendReturnsBool(playerTime, sel_registerName("isSampleTimeValid")))
				return _pausedFrame;
			return Math.Clamp(_segmentStart + SendLong(playerTime, sel_registerName("sampleTime")), 0, _frames);
		}
		finally
		{
			objc_autoreleasePoolPop(pool);
		}
	}

	private void ApplyEqualizer()
	{
		lock (_gate)
		{
			if (_equalizer == 0)
				return;
			nint pool = objc_autoreleasePoolPush();
			try
			{
				nint bands = Send(_equalizer, sel_registerName("bands"));
				for (int index = 0; index < _gains.Length; index++)
				{
					nint band = SendIndex(bands, sel_registerName("objectAtIndex:"), (nuint)index);
					SetFloat(band, sel_registerName("setGain:"), _equalizerEnabled ? Math.Clamp(_gains[index], -24f, 12f) : 0f);
				}
				SetBool(_equalizer, sel_registerName("setBypass:"), !_equalizerEnabled);
			}
			finally
			{
				objc_autoreleasePoolPop(pool);
			}
		}
	}

	private static nint OpenFile(string path, out string? error)
	{
		error = null;
		nint url = SendObject(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), NSString(path));
		nint allocated = Send(objc_getClass("AVAudioFile"), sel_registerName("alloc"));
		nint file = InitForReading(allocated, sel_registerName("initForReading:error:"), url, out nint nsError);
		if (file == 0)
			error = Describe(nsError) ?? "This audio format is not supported";
		return file;
	}

	private static string? Describe(nint error)
	{
		if (error == 0)
			return null;
		nint text = Send(error, sel_registerName("localizedDescription"));
		nint utf8 = text == 0 ? 0 : Send(text, sel_registerName("UTF8String"));
		return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
	}

	private static nint NSString(string value) => SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), value);

	private static string? LoadFramework()
	{
		if (!OperatingSystem.IsMacOS())
			return "Native audio is only available on macOS";
		try
		{
			NativeLibrary.Load(AVFoundationPath);
			return null;
		}
		catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
		{
			return "AVFoundation could not be loaded: " + ex.Message;
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

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint SendString(nint receiver, nint selector, string argument);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint SendIndex(nint receiver, nint selector, nuint index);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetIndex(nint receiver, nint selector, nint value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetFloat(nint receiver, nint selector, float value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void SetBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool value);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial double SendDouble(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial long SendLong(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendReturnsBool(nint receiver, nint selector);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static partial bool SendReturnsBoolError(nint receiver, nint selector, out nint error);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial nint InitForReading(nint receiver, nint selector, nint url, out nint error);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void Connect(nint receiver, nint selector, nint source, nint destination, nint format);

	[LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")]
	private static partial void ScheduleSegment(nint receiver, nint selector, nint file, long startFrame, uint frameCount, nint when, nint completion);
}
