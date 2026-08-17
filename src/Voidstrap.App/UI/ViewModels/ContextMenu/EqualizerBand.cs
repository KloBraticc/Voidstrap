using System;
using System.ComponentModel;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public sealed class EqualizerBand : INotifyPropertyChanged
{
    private double _gain;
    private readonly Action<EqualizerBand>? _onChanged;

    public EqualizerBand(int index, string label, Action<EqualizerBand>? onChanged)
    {
        Index = index;
        Label = label;
        _onChanged = onChanged;
    }

    public int Index { get; }

    public string Label { get; }

    public double Gain
    {
        get => _gain;
        set
        {
            double clamped = Math.Clamp(value, -12.0, 12.0);
            if (Math.Abs(_gain - clamped) < 0.001)
                return;
            _gain = clamped;
            OnPropertyChanged(nameof(Gain));
            OnPropertyChanged(nameof(GainLabel));
            _onChanged?.Invoke(this);
        }
    }

    public string GainLabel => $"{(_gain >= 0 ? "+" : string.Empty)}{_gain:0} dB";

    public void SetGainSilent(double value)
    {
        _gain = Math.Clamp(value, -12.0, 12.0);
        OnPropertyChanged(nameof(Gain));
        OnPropertyChanged(nameof(GainLabel));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
