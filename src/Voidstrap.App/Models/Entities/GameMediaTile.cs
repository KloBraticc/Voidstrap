using System.Windows;

namespace Voidstrap.Models.Entities;

public class GameMediaTile
{
    public string Url { get; set; } = "";

    public bool IsVideo { get; set; }

    public bool IsSelected { get; set; }

    public Visibility VideoIconVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
}
