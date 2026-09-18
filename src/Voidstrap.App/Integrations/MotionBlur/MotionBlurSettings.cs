using System;

namespace Voidstrap.Integrations.MotionBlur
{
    public static class MotionBlurSettings
    {
        public static readonly string[] StrengthNames = new string[] { "Off", "Subtle", "Normal", "Strong", "Extreme" };

        private static readonly float[] Shutter = new float[] { 0f, 0.25f, 0.5f, 0.75f, 1.0f };

        private static readonly float[] MaxBlurPixels = new float[] { 0f, 24f, 40f, 56f, 72f };

        public static int StrengthIndex => Math.Clamp(App.Settings.Prop.MotionBlurStrengthIndex, 0, StrengthNames.Length - 1);

        public static float ShutterFor(int strengthIndex) => Shutter[Math.Clamp(strengthIndex, 0, Shutter.Length - 1)];

        public static float MaxBlurPixelsFor(int strengthIndex) => MaxBlurPixels[Math.Clamp(strengthIndex, 0, MaxBlurPixels.Length - 1)];
    }
}
