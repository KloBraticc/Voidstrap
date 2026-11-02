using System.Windows;
using System.Windows.Controls.Primitives;

namespace Voidstrap.UI;

internal static class OverlayToolTipPlacement
{
    public static readonly CustomPopupPlacementCallback Above = Place;

    private static CustomPopupPlacement[] Place(Size popupSize, Size targetSize, Point offset)
    {
        double left = (targetSize.Width - popupSize.Width) / 2;
        return
        [
            new CustomPopupPlacement(new Point(left, -popupSize.Height - 6), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(left, targetSize.Height + 6), PopupPrimaryAxis.Horizontal),
        ];
    }
}
