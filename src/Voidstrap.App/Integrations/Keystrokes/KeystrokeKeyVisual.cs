using System.Windows.Controls;
using System.Windows.Media;

namespace Voidstrap.Integrations.Keystrokes;

internal sealed class KeystrokeKeyVisual
{
    public required string Input { get; init; }

    public required Border Key { get; init; }

    public required Border Highlight { get; init; }

    public required TextBlock Label { get; init; }

    public TextBlock? Cps { get; init; }

    public string CpsFormat { get; init; } = "{0} CPS";

    public required Brush Foreground { get; init; }

    public required Brush PressedForeground { get; init; }

    public required Brush CpsForeground { get; init; }

    public required Brush PressedCpsForeground { get; init; }

    public double PressedScale { get; init; } = 1.0;
}
