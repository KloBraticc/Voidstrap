using System;
using System.Windows;
using System.Windows.Media;

namespace Voidstrap.Utility
{
    internal static class GradientProfileBorder
    {
        public static Brush? ParseBorder(string? css)
        {
            if (string.IsNullOrWhiteSpace(css))
                return null;

            Brush? gradient = GradientWebsite.Parse(css);
            if (gradient is not null)
                return gradient;

            if (GradientWebsite.TryParseColor(css!.Trim(), out Color color))
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }

            return null;
        }

        public static bool HasBorder(string? avatarBorder)
        {
            return ParseBorder(avatarBorder) is not null;
        }
    }
}
