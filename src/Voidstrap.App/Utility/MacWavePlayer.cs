using System;
using System.Threading;
using NAudio.Wave;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.Utility;

public sealed class MacWavePlayer : IWavePlayer
{
	private const int PumpMilliseconds = 40;
	private const double BufferSeconds = 0.3;

	private readonly SynchronizationContext? _context = SynchronizationContext.Current;
	private readonly object _gate = new();
	private ISampleProvider? _source;
	private MacOSPcmOutput? _output;
	private Timer? _pump;
	private float[] _block = [];
	private bool _sourceDrained;
	private float _volume = 1f;

	public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;

	public WaveFormat OutputWaveFormat => _source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

	public float Volume
	{
		get => _volume;
		set
		{
			_volume = Math.Clamp(value, 0f, 1f);
			if (_output != null)
				_output.Volume = _volume;
		}
	}

	public event EventHandler<StoppedEventArgs>? PlaybackStopped;

	public void Init(IWaveProvider waveProvider)
	{
		_source = waveProvider.ToSampleProvider();
		WaveFormat format = _source.WaveFormat;
		_output = MacOSPcmOutput.Create(format.SampleRate, format.Channels, out string? error)
			?? throw new InvalidOperationException(error ?? "The audio output could not be created");
		_output.Volume = _volume;
		_block = new float[format.SampleRate / 10 * format.Channels];
	}

	public void Play()
	{
		if (_output == null)
			return;
		lock (_gate)
		{
			if (PlaybackState == PlaybackState.Stopped)
			{
				_output.Flush();
				_sourceDrained = false;
			}
			Fill();
			_output.Play();
			PlaybackState = PlaybackState.Playing;
			_pump ??= new Timer(OnPump, null, PumpMilliseconds, PumpMilliseconds);
		}
	}

	public void Pause()
	{
		lock (_gate)
		{
			if (PlaybackState != PlaybackState.Playing)
				return;
			_output?.Pause();
			PlaybackState = PlaybackState.Paused;
		}
	}

	public void Stop()
	{
		bool wasActive;
		lock (_gate)
		{
			wasActive = PlaybackState != PlaybackState.Stopped;
			StopCore();
		}
		if (wasActive)
			RaiseStopped(null);
	}

	public void Dispose()
	{
		lock (_gate)
		{
			StopCore();
			_output?.Dispose();
			_output = null;
		}
	}

	private void StopCore()
	{
		Timer? pump = _pump;
		_pump = null;
		pump?.Dispose();
		_output?.Flush();
		PlaybackState = PlaybackState.Stopped;
	}

	private void OnPump(object? state)
	{
		bool finished = false;
		Exception? failure = null;
		lock (_gate)
		{
			if (PlaybackState != PlaybackState.Playing || _output == null)
				return;
			try
			{
				Fill();
				if (_sourceDrained && _output.QueuedFrames <= 0)
				{
					StopCore();
					finished = true;
				}
			}
			catch (Exception ex)
			{
				StopCore();
				failure = ex;
				finished = true;
			}
		}
		if (finished)
			RaiseStopped(failure);
	}

	private void Fill()
	{
		if (_source == null || _output == null)
			return;
		_sourceDrained = false;
		WaveFormat format = _source.WaveFormat;
		long target = (long)(format.SampleRate * BufferSeconds);
		while (_output.QueuedFrames < target)
		{
			int read = _source.Read(_block.AsSpan());
			if (read <= 0)
			{
				_sourceDrained = true;
				return;
			}
			_output.Enqueue(_block.AsSpan(0, read));
		}
	}

	private void RaiseStopped(Exception? failure)
	{
		StoppedEventArgs args = new(failure);
		if (_context != null)
			_context.Post(_ => PlaybackStopped?.Invoke(this, args), null);
		else
			PlaybackStopped?.Invoke(this, args);
	}
}
