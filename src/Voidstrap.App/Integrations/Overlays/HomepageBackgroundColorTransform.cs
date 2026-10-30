using System;
using System.Numerics;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Overlays;

internal readonly struct HomepageBackgroundColorTransform
{
    private static readonly object CacheGate = new();
    private static TransformSettings _cachedSettings;
    private static HomepageBackgroundColorTransform _cachedTransform;
    private static bool _cacheReady;
    private readonly float[]? _matrix;
    private readonly record struct TransformSettings(
        double Saturation,
        double Contrast,
        double Temperature,
        bool ColorBlindnessEnabled,
        int ColorBlindnessType,
        double ColorBlindnessSeverity,
        bool ColorBlindnessSimulate);

    private HomepageBackgroundColorTransform(float[]? matrix)
    {
        _matrix = matrix;
    }

    public static HomepageBackgroundColorTransform Create(
        double saturation,
        double contrast,
        double temperature,
        bool colorBlindnessEnabled,
        int colorBlindnessType,
        double colorBlindnessSeverity,
        bool colorBlindnessSimulate)
    {
        TransformSettings settings = new(
            saturation,
            contrast,
            temperature,
            colorBlindnessEnabled,
            colorBlindnessType,
            colorBlindnessSeverity,
            colorBlindnessSimulate);
        lock (CacheGate)
        {
            if (_cacheReady && settings == _cachedSettings)
                return _cachedTransform;
        }
        float[]? matrix = ScreenColorEffect.BuildMatrix(
            saturation,
            contrast,
            temperature,
            colorBlindnessEnabled,
            (ScreenColorEffect.ColorBlindnessType)Math.Clamp(colorBlindnessType, 0, 2),
            Math.Clamp(colorBlindnessSeverity / 100d, 0d, 1d),
            colorBlindnessSimulate);
        HomepageBackgroundColorTransform transform = new(matrix);
        lock (CacheGate)
        {
            _cachedSettings = settings;
            _cachedTransform = transform;
            _cacheReady = true;
        }
        return transform;
    }

    public static HomepageBackgroundColorTransform ReadConfigured()
    {
        if (Voidstrap.Utility.Platform.IsLinux)
            return Create(100d, 100d, 0d, false, 1, 100d, false);
        var settings = App.Settings.Prop;
        return Create(
            settings.Saturation,
            settings.Contrast,
            settings.ColorTemperature,
            settings.ColorBlindnessEnabled,
            settings.ColorBlindnessType,
            settings.ColorBlindnessSeverity,
            settings.ColorBlindnessSimulate);
    }

    public Vector3 Apply(Vector3 color)
    {
        if (_matrix == null)
            return color;
        float red = color.X;
        float green = color.Y;
        float blue = color.Z;
        return Vector3.Clamp(new Vector3(
            red * _matrix[0] + green * _matrix[5] + blue * _matrix[10] + _matrix[20],
            red * _matrix[1] + green * _matrix[6] + blue * _matrix[11] + _matrix[21],
            red * _matrix[2] + green * _matrix[7] + blue * _matrix[12] + _matrix[22]), Vector3.Zero, Vector3.One);
    }

    public void Apply(byte red, byte green, byte blue, out byte transformedRed, out byte transformedGreen, out byte transformedBlue)
    {
        Vector3 transformed = Apply(new Vector3(red / 255f, green / 255f, blue / 255f));
        transformedRed = ToByte(transformed.X);
        transformedGreen = ToByte(transformed.Y);
        transformedBlue = ToByte(transformed.Z);
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }
}

internal readonly record struct HomepageBackgroundSourceKey(byte Red, byte Green, byte Blue)
{
    public Vector3 Normalized => new(Red / 255f, Green / 255f, Blue / 255f);
}

internal readonly record struct HomepageBackgroundSourceKeys(
    HomepageBackgroundSourceKey Original,
    HomepageBackgroundSourceKey Sober,
    HomepageBackgroundSourceKey AdjustedOriginal,
    HomepageBackgroundSourceKey AdjustedSober)
{
    private static int _calibrated = -1;

    public static void Calibrate(byte red, byte green, byte blue)
    {
        int packed = red << 16 | green << 8 | blue;
        if (Interlocked.Exchange(ref _calibrated, packed) != packed)
            App.Logger?.WriteLine("HomepageBackground", $"The Roblox home color was measured as {red},{green},{blue}");
    }

    public static HomepageBackgroundSourceKeys ReadConfigured()
    {
        HomepageBackgroundColorTransform transform = HomepageBackgroundColorTransform.ReadConfigured();
        int calibrated = Volatile.Read(ref _calibrated);
        HomepageBackgroundSourceKey secondary = calibrated >= 0
            ? new HomepageBackgroundSourceKey((byte)(calibrated >> 16), (byte)(calibrated >> 8), (byte)calibrated)
            : new HomepageBackgroundSourceKey(18, 18, 24);
        transform.Apply(18, 18, 21, out byte originalRed, out byte originalGreen, out byte originalBlue);
        transform.Apply(secondary.Red, secondary.Green, secondary.Blue, out byte soberRed, out byte soberGreen, out byte soberBlue);
        return new HomepageBackgroundSourceKeys(
            new HomepageBackgroundSourceKey(18, 18, 21),
            secondary,
            new HomepageBackgroundSourceKey(originalRed, originalGreen, originalBlue),
            new HomepageBackgroundSourceKey(soberRed, soberGreen, soberBlue));
    }
}
