using System.ComponentModel;

public class DisplayMode
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
    public string DisplayName => $"{Width}x{Height} @ {RefreshRate}Hz";

    public override string ToString() => DisplayName;
}

public class MonitorTile : INotifyPropertyChanged
{
    private bool _isSelected;

    public string DeviceName { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    public int Number { get; set; }

    public bool IsPrimary { get; set; }

    public double CanvasX { get; set; }

    public double CanvasY { get; set; }

    public double BoxWidth { get; set; }

    public double BoxHeight { get; set; }

    public bool IsSelected
    {
        get
        {
            return _isSelected;
        }
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
