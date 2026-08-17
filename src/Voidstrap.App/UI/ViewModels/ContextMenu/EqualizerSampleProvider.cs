using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public sealed class EqualizerSampleProvider : ISampleProvider
{
    private const float BandQ = 1.1f;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float[] _frequencies;
    private readonly float[] _gains;
    private BiQuadFilter[,] _filters;
    private volatile bool _enabled;
    private volatile bool _dirty;

    public EqualizerSampleProvider(ISampleProvider source, float[] frequencies)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = source.WaveFormat.Channels;
        _frequencies = frequencies;
        _gains = new float[frequencies.Length];
        BuildFilters();
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

    private void BuildFilters()
    {
        float sampleRate = _source.WaveFormat.SampleRate;
        var filters = new BiQuadFilter[_channels, _frequencies.Length];
        for (int c = 0; c < _channels; c++)
        {
            for (int b = 0; b < _frequencies.Length; b++)
                filters[c, b] = BiQuadFilter.PeakingEQ(sampleRate, _frequencies[b], BandQ, _gains[b]);
        }
        _filters = filters;
        _dirty = false;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (!_enabled)
            return read;
        if (_dirty)
            BuildFilters();

        BiQuadFilter[,] filters = _filters;
        int channels = _channels;
        int bands = _frequencies.Length;

        for (int n = 0; n < read; n++)
        {
            int channel = n % channels;
            float sample = buffer[offset + n];
            for (int b = 0; b < bands; b++)
                sample = filters[channel, b].Transform(sample);
            buffer[offset + n] = sample;
        }
        return read;
    }
}
