using System.Windows.Media;

namespace Voidstrap.Models;

public class CustomIconChoice
{
    public string Path { get; init; } = "";

    public ImageSource? Image { get; init; }

    public bool IsSelected { get; init; }
}
