using System;

namespace Voidstrap.Integrations.FrameGeneration
{
    public static class FrameGenSettings
    {
        public static readonly string[] ModeNames = ["Off", "Auto"];

        public static int ModeIndex => Math.Clamp(App.Settings.Prop.FrameGenModeIndex, 0, ModeNames.Length - 1);

        public static bool IsAuto(int modeIndex) => modeIndex == 1;

        internal static int MaxGeneratedForCadence(int modeIndex, double frameMs, double refreshHz, double consumeRatio = 1.0) =>
            FramePacer.vfp_max_generated_for_cadence(modeIndex, consumeRatio);

        internal static int RequiredCadenceSamples(double frameMs) => FramePacer.vfp_required_cadence_samples(frameMs);

        internal static int TargetTotal(double frameMs, double refreshHz, int modeIndex, bool uncap, ref double phase) =>
            FramePacer.TargetTotal(frameMs, refreshHz, modeIndex, uncap, ref phase);

        internal static int FirstUnshownSlot(int filled, int total) => FramePacer.vfp_first_unshown_slot(filled, total);

        internal static bool IsSlotOverdue(double targetMs, double nowMs, double slotMs) =>
            FramePacer.vfp_is_slot_overdue(targetMs, nowMs, slotMs) != 0;

        internal static bool IsSourceStutter(double elapsedMs, double expectedMs) =>
            FramePacer.vfp_is_source_stutter(elapsedMs, expectedMs) != 0;

        internal static double OutputSlotMs(double sourceIntervalMs, double refreshHz, int total, bool uncap = false) =>
            FramePacer.vfp_output_slot_ms(sourceIntervalMs, refreshHz, total, uncap ? 1 : 0);
    }
}
