using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public sealed class EqualizerSampleProvider : ISampleProvider
{
    private const float BandQ = 1.1f;

    private const float NyquistMargin = 0.45f;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float _sampleRate;
    private readonly float[] _frequencies;
    private readonly float[] _gains;
    private readonly float[] _appliedGains;
    private readonly BiQuadFilter[][] _filters;
    private volatile bool _enabled;
    private volatile bool _dirty;
    private bool _flat = true;

    public EqualizerSampleProvider(ISampleProvider source, float[] frequencies)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _sampleRate = source.WaveFormat.SampleRate;
        float highest = _sampleRate * NyquistMargin;
        _frequencies = new float[frequencies.Length];
        for (int b = 0; b < frequencies.Length; b++)
            _frequencies[b] = Math.Min(frequencies[b], highest);
        _gains = new float[frequencies.Length];
        _appliedGains = new float[frequencies.Length];
        _filters = new BiQuadFilter[_channels][];
        for (int c = 0; c < _channels; c++)
        {
            _filters[c] = new BiQuadFilter[_frequencies.Length];
            for (int b = 0; b < _frequencies.Length; b++)
                _filters[c][b] = BiQuadFilter.PeakingEQ(_sampleRate, _frequencies[b], BandQ, 0f);
        }
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int BandCount => _frequencies.Length;

    public IReadOnlyList<float> Frequencies => _frequencies;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void SetBandGain(int band, float gainDb)
    {
        if (band < 0 || band >= _gains.Length)
            return;
        _gains[band] = Math.Clamp(gainDb, -18f, 18f);
        _dirty = true;
    }

    public float GetBandGain(int band) => (band >= 0 && band < _gains.Length) ? _gains[band] : 0f;

    private void ApplyGains()
    {
        _dirty = false;
        bool flat = true;
        for (int b = 0; b < _gains.Length; b++)
        {
            float gain = _gains[b];
            if (gain != 0f)
                flat = false;
            if (gain == _appliedGains[b])
                continue;
            _appliedGains[b] = gain;
            for (int c = 0; c < _channels; c++)
                _filters[c][b].SetPeakingEq(_sampleRate, _frequencies[b], BandQ, gain);
        }
        _flat = flat;
    }

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        if (!_enabled)
            return read;
        if (_dirty)
            ApplyGains();
        if (_flat)
            return read;

        int channels = _channels;
        for (int c = 0; c < channels; c++)
        {
            BiQuadFilter[] chain = _filters[c];
            for (int n = c; n < read; n += channels)
            {
                float sample = buffer[n];
                for (int b = 0; b < chain.Length; b++)
                    sample = chain[b].Transform(sample);
                buffer[n] = sample;
            }
        }
        return read;
    }
}
