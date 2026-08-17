using System;

namespace Voidstrap.Integrations.FrameGeneration
{
    public static class FrameGenSettings
    {
        public static readonly string[] ModeNames = ["Off", "Auto"];

        private static readonly int[] MaxGenerated = [0, 7];

        public static int ModeIndex => Math.Clamp(App.Settings.Prop.FrameGenModeIndex, 0, ModeNames.Length - 1);

        public static bool IsAuto(int modeIndex) => modeIndex == 1;

        public static int MaxGeneratedFor(int modeIndex)
        {
            return MaxGenerated[Math.Clamp(modeIndex, 0, MaxGenerated.Length - 1)];
        }

        internal static int MaxGeneratedForCadence(int modeIndex, double frameMs, double refreshHz, double consumeRatio = 1.0)
        {
            int maximum = MaxGeneratedFor(modeIndex);
            if (maximum <= 0 || consumeRatio >= 0.9)
                return maximum;
            return Math.Clamp((int)Math.Floor(maximum * consumeRatio), 0, maximum);
        }

        internal static int RequiredCadenceSamples(double frameMs) => frameMs >= 60.0 ? 1 : 2;

        internal static int NextTotal(double frameMs, double refreshHz, ref double phase)
        {
            double slots = frameMs * refreshHz / 1000.0;
            double nearest = Math.Round(slots);
            if (nearest >= 1.0 && Math.Abs(slots - nearest) <= 0.03)
                slots = nearest;
            phase += slots;
            int total = Math.Max(1, (int)phase);
            phase = Math.Clamp(phase - total, 0.0, 1.0);
            return total;
        }

        internal static int TargetTotal(double frameMs, double refreshHz, int modeIndex, bool uncap, ref double phase)
        {
            int maximum = MaxGeneratedForCadence(modeIndex, frameMs, refreshHz) + 1;
            if (modeIndex == 1 && !uncap)
            {
                double slots = frameMs * refreshHz / 1000.0;
                int stableTotal = slots < 1.25
                    ? 1
                    : (int)Math.Ceiling(slots - 0.02);
                phase = 0;
                return Math.Clamp(stableTotal, 1, maximum);
            }
            return uncap ? maximum : Math.Min(maximum, NextTotal(frameMs, refreshHz, ref phase));
        }

        internal static int FirstUnshownSlot(int filled, int total)
        {
            return Math.Clamp(filled + 1, 1, total);
        }

        internal static double NormalizeInterval(double measuredMs, double expectedMs)
        {
            if (expectedMs <= 4.0)
                return measuredMs;
            double multiple = Math.Round(measuredMs / expectedMs);
            if (multiple < 2.0 || multiple > 4.0)
                return measuredMs;
            double normalized = measuredMs / multiple;
            return Math.Abs(normalized - expectedMs) <= expectedMs * 0.15 ? normalized : measuredMs;
        }

        internal static double MedianInterval(ReadOnlySpan<double> samples)
        {
            if (samples.Length == 0)
                return 0;
            Span<double> sorted = stackalloc double[15];
            int count = Math.Min(samples.Length, sorted.Length);
            samples[..count].CopyTo(sorted);
            sorted[..count].Sort();
            return sorted[count / 2];
        }

        internal static bool IsSlotOverdue(double targetMs, double nowMs, double slotMs)
        {
            return targetMs < nowMs - Math.Max(slotMs, 2.0);
        }

        internal static bool IsSourceStutter(double elapsedMs, double expectedMs)
        {
            return expectedMs > 4.0 && elapsedMs >= Math.Max(80.0, expectedMs * 4.0);
        }

        internal static double OutputSlotMs(double sourceIntervalMs, double refreshHz, int total, bool uncap = false)
        {
            double refreshIntervalMs = 1000.0 / Math.Max(1.0, refreshHz);
            return sourceIntervalMs > 0.5 && total > 0
                ? uncap ? sourceIntervalMs / total : Math.Max(refreshIntervalMs, sourceIntervalMs / total)
                : refreshIntervalMs;
        }

    }
}
