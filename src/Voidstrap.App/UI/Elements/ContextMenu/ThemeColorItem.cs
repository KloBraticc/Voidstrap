using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Voidstrap.UI.Elements.ContextMenu;

public sealed class ThemeColorItem : INotifyPropertyChanged
{
    private Color _color;

    public string Key { get; }

    public string Label { get; }

    public string Group { get; }

    public bool IsBrush { get; }

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

    public event Action? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ThemeColorItem(string key, string label, bool isBrush, Color color, string group = "")
    {
        Key = key;
        Label = label;
        IsBrush = isBrush;
        _color = color;
        Group = group;
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
