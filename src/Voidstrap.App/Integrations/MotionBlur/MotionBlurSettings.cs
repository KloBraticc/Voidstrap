using System;

namespace Voidstrap.Integrations.MotionBlur
{
    public static class MotionBlurSettings
    {
        public static readonly string[] StrengthNames = new string[] { "Off", "Subtle", "Normal", "Strong", "Extreme", "Custom" };

        public const int CustomIndex = 5;

        public const int MinCustomAmount = 10;

        public const int MaxCustomAmount = 500;

        private static readonly int[] PresetAmount = new int[] { 0, 50, 100, 150, 200 };

        public static int StrengthIndex => Math.Clamp(App.Settings.Prop.MotionBlurStrengthIndex, 0, StrengthNames.Length - 1);

        public static int CustomAmount => Math.Clamp(App.Settings.Prop.MotionBlurCustomAmount, MinCustomAmount, MaxCustomAmount);

        public static int AmountFor(int strengthIndex) => strengthIndex == CustomIndex ? CustomAmount : PresetAmount[Math.Clamp(strengthIndex, 0, PresetAmount.Length - 1)];

        public static float ShutterFor(int strengthIndex) => AmountFor(strengthIndex) * 0.006f;

        public static float MaxBlurPixelsFor(int strengthIndex) => AmountFor(strengthIndex) * 0.48f;

        public static string Describe() => StrengthIndex == CustomIndex ? "Custom at " + CustomAmount : StrengthNames[StrengthIndex];
    }
}
