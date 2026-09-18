using System;
using System.Windows.Media;

namespace Wpf.Ui.Appearance;

public static class RinColor
{
    private const int Max = ushort.MaxValue;

    public static Color LighterThenDarker(Color color, int lighterFactor, int darkerFactor)
    {
        int r = color.R * 257;
        int g = color.G * 257;
        int b = color.B * 257;
        Lighter(ref r, ref g, ref b, lighterFactor);
        Darker(ref r, ref g, ref b, darkerFactor);
        return Color.FromArgb(color.A, Div257(r), Div257(g), Div257(b));
    }

    private static void Lighter(ref int r, ref int g, ref int b, int factor)
    {
        if (factor <= 0)
            return;
        if (factor < 100)
        {
            Darker(ref r, ref g, ref b, 10000 / factor);
            return;
        }

        ToHsv(r, g, b, out int hue, out int saturation, out int value);
        long scaled = (long)factor * value / 100;
        long s = saturation;
        if (scaled > Max)
        {
            s -= scaled - Max;
            if (s < 0)
                s = 0;
            scaled = Max;
        }

        FromHsv(hue, (int)s, (int)scaled, out r, out g, out b);
    }

    private static void Darker(ref int r, ref int g, ref int b, int factor)
    {
        if (factor <= 0)
            return;
        if (factor < 100)
        {
            Lighter(ref r, ref g, ref b, 10000 / factor);
            return;
        }

        ToHsv(r, g, b, out int hue, out int saturation, out int value);
        FromHsv(hue, saturation, value * 100 / factor, out r, out g, out b);
    }

    private static void ToHsv(int red, int green, int blue, out int hue, out int saturation, out int value)
    {
        float r = red / (float)Max;
        float g = green / (float)Max;
        float b = blue / (float)Max;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;
        value = QRound(max * Max);

        if (Math.Abs(delta) <= 0.00001f)
        {
            hue = Max;
            saturation = 0;
            return;
        }

        saturation = QRound(delta / max * Max);
        float h = 0f;
        if (FuzzyEquals(r, max))
            h = (g - b) / delta;
        else if (FuzzyEquals(g, max))
            h = 2.0f + (b - r) / delta;
        else if (FuzzyEquals(b, max))
            h = 4.0f + (r - g) / delta;
        h *= 60.0f;
        if (h < 0.0f)
            h += 360.0f;
        hue = QRound(h * 100.0f);
    }

    private static void FromHsv(int hue, int saturation, int value, out int red, out int green, out int blue)
    {
        if (saturation == 0 || hue == Max)
        {
            red = green = blue = value;
            return;
        }

        float h = hue == 36000 ? 0.0f : hue / 6000.0f;
        float s = saturation / (float)Max;
        float v = value / (float)Max;
        int i = (int)h;
        float f = h - i;
        float p = v * (1.0f - s);
        float r = 0f, g = 0f, b = 0f;

        if ((i & 1) != 0)
        {
            float q = v * (1.0f - s * f);
            switch (i)
            {
                case 1: r = q; g = v; b = p; break;
                case 3: r = p; g = q; b = v; break;
                case 5: r = v; g = p; b = q; break;
            }
        }
        else
        {
            float t = v * (1.0f - s * (1.0f - f));
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 4: r = t; g = p; b = v; break;
            }
        }

        red = QRound(r * Max);
        green = QRound(g * Max);
        blue = QRound(b * Max);
    }

    private static int QRound(float value)
    {
        return value >= 0.0f ? (int)(value + 0.5f) : (int)(value - 0.5f);
    }

    private static bool FuzzyEquals(float first, float second)
    {
        return Math.Abs(first - second) * 100000.0f <= Math.Min(Math.Abs(first), Math.Abs(second));
    }

    private static byte Div257(int value)
    {
        return (byte)((value + 128 - ((value + 128) >> 8)) >> 8);
    }
}
