using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Voidstrap.UI.Elements.ContextMenu;

public sealed class ThemeColorItem : INotifyPropertyChanged
{
    private Color _color;

    private bool _isSet;

    public string Key { get; }

    public string Label { get; }

    public string Group { get; }

    public bool IsBrush { get; }

    public bool Optional { get; }

    public bool IsSet => !Optional || _isSet;

    public Color Color => _color;

    public Brush Swatch => IsSet ? new SolidColorBrush(_color) : Brushes.Transparent;

    public double RowOpacity => IsSet ? 1.0 : 0.6;

    public Visibility ResetVisibility => Optional && _isSet ? Visibility.Visible : Visibility.Collapsed;

    public string Placeholder => Optional ? "Default" : string.Empty;

    public string DisplayHex => IsSet ? Voidstrap.Utility.CustomTheme.ToHex(_color) : "Default";

    public string Hex
    {
        get => IsSet ? Voidstrap.Utility.CustomTheme.ToHex(_color) : string.Empty;
        set
        {
            if (Optional && string.IsNullOrWhiteSpace(value))
            {
                Clear();
                return;
            }
            if (Voidstrap.Utility.CustomTheme.TryParseColor(value, out Color c) && (c != _color || !IsSet))
            {
                _color = c;
                _isSet = true;
                NotifyVisuals(includeHex: false);
                Changed?.Invoke();
            }
        }
    }

    public event Action? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ThemeColorItem(string key, string label, bool isBrush, Color color, string group = "", bool optional = false, bool isSet = true)
    {
        Key = key;
        Label = label;
        IsBrush = isBrush;
        _color = color;
        Group = group;
        Optional = optional;
        _isSet = !optional || isSet;
    }

    public void SetColor(Color c)
    {
        if (c == _color && IsSet)
            return;
        _color = c;
        _isSet = true;
        NotifyVisuals(includeHex: true);
        Changed?.Invoke();
    }

    public void Load(Color c, bool isSet)
    {
        _color = c;
        _isSet = !Optional || isSet;
        NotifyVisuals(includeHex: true);
    }

    public void Clear()
    {
        if (!Optional || !_isSet)
            return;
        _isSet = false;
        NotifyVisuals(includeHex: true);
        Changed?.Invoke();
    }

    public void Detach()
    {
        Changed = null;
        PropertyChanged = null;
    }

    private void NotifyVisuals(bool includeHex)
    {
        if (includeHex)
            OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(Swatch));
        OnPropertyChanged(nameof(DisplayHex));
        OnPropertyChanged(nameof(IsSet));
        OnPropertyChanged(nameof(RowOpacity));
        OnPropertyChanged(nameof(ResetVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class GradientStopItem : INotifyPropertyChanged
{
    private Color _color;

    private double _position;

    public Color Color => _color;

    public Brush Swatch => new SolidColorBrush(_color);

    public string Hex
    {
        get => Voidstrap.Utility.CustomTheme.ToHex(_color);
        set
        {
            if (Voidstrap.Utility.CustomTheme.TryParseColor(value, out Color c) && c != _color)
            {
                _color = c;
                OnPropertyChanged(nameof(Swatch));
                Changed?.Invoke();
            }
        }
    }

    public double Position
    {
        get => _position;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (Math.Abs(clamped - _position) < 0.001)
                return;
            _position = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionText));
            Changed?.Invoke();
        }
    }

    public string PositionText => _position.ToString("0") + "%";

    public event Action? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GradientStopItem(Color color, double position)
    {
        _color = color;
        _position = Math.Round(Math.Clamp(position, 0, 100));
    }

    public void SetColor(Color c)
    {
        if (c == _color)
            return;
        _color = c;
        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(Swatch));
        Changed?.Invoke();
    }

    public void Detach()
    {
        Changed = null;
        PropertyChanged = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
