using System.Collections.Generic;
using System.ComponentModel;

namespace Voidstrap.Models.Entities;

public class StoreSort : INotifyPropertyChanged
{
    private bool _isCurrent;

    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public List<StoreGameEntry> Games { get; set; } = new();

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
                return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
