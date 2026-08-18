using System;
using System.Collections.Generic;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public sealed class PlaybackService : IDisposable
{
    public static readonly float[] Bands = { 31f, 62f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f };

    private IWavePlayer? _output;
    private MediaFoundationReader? _reader;
    private EqualizerSampleProvider? _equalizer;
    private VolumeSampleProvider? _volumeProvider;

    private float _volume = 1f;
    private bool _eqEnabled;
    private readonly float[] _eqGains = new float[Bands.Length];
    private bool _disposed;

    public bool IsPlaying { get; private set; }

    public bool HasTrack => _reader != null;

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public int BandCount => Bands.Length;

    public IReadOnlyList<float> BandFrequencies => Bands;

    public event EventHandler? PlaybackEnded;

    public event EventHandler? PlayStateChanged;

    public TimeSpan Position
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set
        {
            if (_reader == null)
                return;
            try
            {
                TimeSpan clamped = value < TimeSpan.Zero ? TimeSpan.Zero : (value > _reader.TotalTime ? _reader.TotalTime : value);
                _reader.CurrentTime = clamped;
            }
            catch
            {
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = (float)Math.Clamp(value, 0.0, 1.0);
            if (_volumeProvider != null)
                _volumeProvider.Volume = _volume;
        }
    }

    public bool EqualizerEnabled
    {
        get => _eqEnabled;
        set
        {
            _eqEnabled = value;
            if (_equalizer != null)
                _equalizer.Enabled = value;
        }
    }

    public void SetBandGain(int band, float gainDb)
    {
        if (band < 0 || band >= _eqGains.Length)
            return;
        _eqGains[band] = gainDb;
        _equalizer?.SetBandGain(band, gainDb);
    }

    public float GetBandGain(int band) => (band >= 0 && band < _eqGains.Length) ? _eqGains[band] : 0f;

    public bool Load(string path)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            _reader = new MediaFoundationReader(path);
            ISampleProvider source = _reader.ToSampleProvider();
            _equalizer = new EqualizerSampleProvider(source, Bands) { Enabled = _eqEnabled };
            for (int i = 0; i < _eqGains.Length; i++)
                _equalizer.SetBandGain(i, _eqGains[i]);
            _volumeProvider = new VolumeSampleProvider(_equalizer) { Volume = _volume };
#pragma warning disable CS0618
            _output = new WasapiOut(AudioClientShareMode.Shared, false, 200);
#pragma warning restore CS0618
            _output.PlaybackStopped += Output_PlaybackStopped;
            _output.Init(_volumeProvider);
            return true;
        }
        catch
        {
            TeardownOutput();
            return false;
        }
    }

    public void Play()
    {
        if (_output == null)
            return;
        _output.Play();
        IsPlaying = true;
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (_output == null)
            return;
        _output.Pause();
        IsPlaying = false;
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        bool wasPlaying = IsPlaying || _output != null;
        TeardownOutput();
        if (wasPlaying)
            PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        IsPlaying = false;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void TeardownOutput()
    {
        if (_output != null)
        {
            _output.PlaybackStopped -= Output_PlaybackStopped;
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            _output = null;
        }
        if (_reader != null)
        {
            try { _reader.Dispose(); } catch { }
            _reader = null;
        }
        _equalizer = null;
        _volumeProvider = null;
        IsPlaying = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        TeardownOutput();
        GC.SuppressFinalize(this);
    }
}
