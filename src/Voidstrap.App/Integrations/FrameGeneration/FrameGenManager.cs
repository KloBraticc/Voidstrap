using System;
using System.Threading;
using System.Windows;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.Integrations.FrameGeneration
{
    public static class FrameGenManager
    {
        private static bool _installed;
		private static int _prepareStarted;
		private static int _gameTransitionActive;

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;
			int normalizedMode = App.Settings.Prop.FrameGenModeIndex > 0 ? 1 : 0;
			bool settingsChanged = App.Settings.Prop.FrameGenModeIndex != normalizedMode
				|| App.Settings.Prop.FrameGenResumeIndex > 1
				|| App.Settings.Prop.FrameGenUncap;
			App.Settings.Prop.FrameGenModeIndex = normalizedMode;
			App.Settings.Prop.FrameGenResumeIndex = normalizedMode > 0 ? 1 : 0;
			App.Settings.Prop.FrameGenUncap = false;
			if (settingsChanged)
				App.Settings.SaveDeferred();
            int savedTarget = App.Settings.Prop.FrameGenTargetFps;
            int target = NormalizeTargetCap(savedTarget);
            if (target != savedTarget)
            {
                App.Settings.Prop.FrameGenTargetFps = target;
                App.Settings.SaveDeferred();
            }
            if (FrameGenSettings.ModeIndex > 0)
				PreparePipeline();
            if (FrameGenSettings.ModeIndex > 0 && target > 0)
                ApplyTargetCap(target);
            App.Logger.WriteLine("FrameGen", "Installed, mode is " + FrameGenSettings.ModeNames[FrameGenSettings.ModeIndex]);
            OverlayHub.Refresh();
        }

        public static void SetTargetCap(int fps)
        {
            Install();
            int target = NormalizeTargetCap(fps);
            App.Settings.Prop.FrameGenTargetFps = target;
            App.Settings.SaveDeferred();
            ApplyTargetCap(target);
        }

        private static void ApplyTargetCap(int fps)
        {
            RobloxSettings.SetCap(fps);
            App.FastFlags.SetPreset("Rendering.Framerate", fps >= 5 && fps < 1000 ? fps : null);
            App.FastFlags.SaveDeferred();
            RobloxFpsCap.ReloadNow();
        }

        private static int NormalizeTargetCap(int fps) => fps >= 5 && fps <= 15 ? 15 : fps > 15 && fps < 1000 ? fps : 0;

		private static void PreparePipeline()
		{
			if (Interlocked.Exchange(ref _prepareStarted, 1) != 0)
				return;
			System.Threading.Tasks.Task.Run(FrameGenPipeline.Prepare);
		}

        public static void SetMode(int modeIndex)
        {
            Install();
            int nextMode = modeIndex > 0 ? 1 : 0;
            if (FrameGenSettings.ModeIndex == 0 && nextMode > 0)
            {
                MessageBoxResult result = Frontend.ShowMessageBox(
                    "Frame generation does not improve performance or actual FPS. It is intended only to make motion appear smoother on low end PCs. It will not help mid range or high end systems, so never enable it on those systems.\n\nEnable frame generation anyway?",
                    MessageBoxImage.Warning,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.No);
                if (result != MessageBoxResult.Yes)
                    return;
            }
            App.Settings.Prop.FrameGenModeIndex = nextMode;
            if (FrameGenSettings.ModeIndex > 0)
				PreparePipeline();
            if (FrameGenSettings.ModeIndex > 0 && App.Settings.Prop.FrameGenTargetFps > 0)
                ApplyTargetCap(App.Settings.Prop.FrameGenTargetFps);
            App.Settings.SaveDeferred();
            App.Logger.WriteLine("FrameGen", "Mode set to " + FrameGenSettings.ModeNames[FrameGenSettings.ModeIndex]);
            OverlayHub.Refresh();
        }

        public static void OnGameJoin()
        {
			OnGameJoinStarting();
		}

		public static void OnGameJoinStarting()
		{
			if (Interlocked.Exchange(ref _gameTransitionActive, 1) != 0)
				return;
			if (FrameGenSettings.ModeIndex > 0)
				App.Logger.WriteLine("FrameGen", "Game transition started, destroying frame generation until the server is ready");
			OverlayHub.OnGameTransitionStarted();
		}

		public static void OnGameJoinConfirmed()
		{
			if (Interlocked.Exchange(ref _gameTransitionActive, 0) != 0)
			{
				App.Logger.WriteLine("FrameGen", "Server join confirmed, rebuilding frame generation with fresh capture history");
				OverlayHub.OnGameTransitionCompleted();
			}
			else
			{
				OverlayHub.OnGameJoin();
			}
        }

        public static void OnGameLeave()
        {
			Interlocked.Exchange(ref _gameTransitionActive, 0);
            OverlayHub.OnGameLeave();
        }

        public static void Shutdown()
        {
			Interlocked.Exchange(ref _gameTransitionActive, 0);
			_installed = false;
            OverlayHub.Shutdown();
        }
    }
}
