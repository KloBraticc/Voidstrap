using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Voidstrap.Utility
{
    public static partial class RenderAcceleration
    {
        private static readonly object Sync = new();

        private static RenderMode? _appliedProcessMode;

        private static int _windowHandlerInstalled;

        private static int _sentinelArmed;

        private static System.Windows.Threading.DispatcherTimer? _settleTimer;

        private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

        private const int RenderThreadFailure = unchecked((int)0x88980406);

        private static int _recovering;

        private static bool _sessionFallback;

        private static string SentinelPath => Path.Combine(Paths.Base, "gpurender.pending");

        public static bool SoftwareOnly
        {
            get
            {
                try
                {
                    return _sessionFallback || App.Settings?.Prop?.WPFSoftwareRender == true || App.LaunchSettings.NoGPUFlag.Active;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool? _dlssWrapsGraphics;

        public static bool DlssWrapsGraphics
        {
            get
            {
                if (_dlssWrapsGraphics is bool known)
                    return known;
                bool wraps = false;
                try
                {
                    wraps = Platform.IsWindows && App.Settings?.Prop?.RiShadeEnabled == true && Voidstrap.Integrations.RiShade.RiShadeManager.DlssInstalled;
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteException("RenderAcceleration::DlssWrapsGraphics", ex);
                }
                _dlssWrapsGraphics = wraps;
                return wraps;
            }
        }

        private static bool WindowsSoftwareOnly => SoftwareOnly || DlssWrapsGraphics;

        public static bool RendersInSoftware => Platform.IsWindows && WindowsSoftwareOnly;

        public static void ApplyProcess()
        {
            if (!Platform.IsWindows)
                return;

            TripSentinel();
            InstallWindowHandler();
            RenderMode mode = WindowsSoftwareOnly ? RenderMode.SoftwareOnly : RenderMode.Default;
            if (DlssWrapsGraphics && !SoftwareOnly)
                App.Logger?.WriteLine("RenderAcceleration::ApplyProcess", "RiShade DLSS is on, Voidstrap windows render in software so ReShade only wraps the game compositor");
            lock (Sync)
            {
                if (_appliedProcessMode == mode)
                    return;

                try
                {
                    RenderOptions.ProcessRenderMode = mode;
                    _appliedProcessMode = mode;
                    App.Logger?.WriteLine("RenderAcceleration::ApplyProcess", mode == RenderMode.SoftwareOnly ? "Software rendering enabled" : "Hardware rendering enabled");
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteException("RenderAcceleration::ApplyProcess", ex);
                }
            }
        }

        public static bool TryRecoverFromRenderFailure(Exception ex)
        {
            if (!Platform.IsWindows || !IsRenderFailure(ex))
                return false;

            if (Interlocked.Exchange(ref _recovering, 1) != 0)
                return true;

            if (SoftwareOnly)
            {
                App.Logger?.WriteLine("RenderAcceleration::Recover", "The renderer failed while already in software mode, nothing left to fall back to");
                return false;
            }

            App.Logger?.WriteLine("RenderAcceleration::Recover", "The GPU renderer stopped, most likely after a display driver reset, restarting with software rendering");
            StopSettleTimer();
            ClearSentinel();

            List<string> arguments = [.. App.LaunchSettings.Args];
            if (!App.LaunchSettings.NoGPUFlag.Active)
                arguments.Add("-nogpu");
            if (App.RestartApplication(arguments, closeRuntime: false))
                return true;

            App.Logger?.WriteLine("RenderAcceleration::Recover", "The restart failed, Voidstrap closes so it can be opened again");
            try
            {
                _ = MessageBoxW(0, "Your display driver reset while Voidstrap was drawing its window. Please open Voidstrap again.", "Voidstrap", MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST);
                if (!App.LaunchSettings.WatcherFlag.Active)
                    Application.Current?.Shutdown();
            }
            catch (Exception notifyEx)
            {
                App.Logger?.WriteException("RenderAcceleration::Recover", notifyEx);
            }
            return true;
        }

        private const uint MB_ICONWARNING = 0x30;

        private const uint MB_SETFOREGROUND = 0x10000;

        private const uint MB_TOPMOST = 0x40000;

        [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
        private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);

        private static bool IsRenderFailure(Exception? ex)
        {
            for (int depth = 0; ex != null && depth < 8; depth++, ex = ex.InnerException)
            {
                if (ex.HResult == RenderThreadFailure)
                    return true;

                if (ex.TargetSite is { Name: "NotifyPartitionIsZombie" } site && site.DeclaringType?.FullName == "System.Windows.Media.MediaContext")
                    return true;
            }
            return false;
        }

        public static string ApplyToBrowserArguments(string arguments)
        {
            string normalized = arguments?.Trim() ?? string.Empty;
            if (!SoftwareOnly || normalized.Contains("--disable-gpu", StringComparison.OrdinalIgnoreCase))
                return normalized;

            return string.IsNullOrEmpty(normalized)
                ? "--disable-gpu --disable-gpu-compositing"
                : normalized + " --disable-gpu --disable-gpu-compositing";
        }

        private static void InstallWindowHandler()
        {
            if (Interlocked.Exchange(ref _windowHandlerInstalled, 1) != 0)
                return;

            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window)
                ApplyWindow(window);

            ArmSentinel();
        }

        private static void TripSentinel()
        {
            try
            {
                if (!File.Exists(SentinelPath))
                    return;

                File.Delete(SentinelPath);

                if (App.Settings?.Prop == null || App.Settings.Prop.WPFSoftwareRender)
                    return;

                _sessionFallback = true;
                App.Logger?.WriteLine("RenderAcceleration::TripSentinel", "The last hardware rendered session stopped before it rendered stably, using software rendering for this session only");
            }
            catch (Exception ex)
            {
                App.Logger?.WriteException("RenderAcceleration::TripSentinel", ex);
            }
        }

        private static void ArmSentinel()
        {
            if (WindowsSoftwareOnly || Interlocked.Exchange(ref _sentinelArmed, 1) != 0)
                return;

            try
            {
                File.WriteAllText(SentinelPath, string.Empty);
                CompositionTarget.Rendering += OnFirstFrame;
                if (Application.Current != null)
                    Application.Current.Exit += OnApplicationExit;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteException("RenderAcceleration::ArmSentinel", ex);
            }
        }

        private static void OnFirstFrame(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnFirstFrame;
            _settleTimer = new System.Windows.Threading.DispatcherTimer { Interval = SettleDelay };
            _settleTimer.Tick += OnRenderingSettled;
            _settleTimer.Start();
        }

        private static void OnRenderingSettled(object? sender, EventArgs e)
        {
            StopSettleTimer();
            ClearSentinel();
        }

        private static void OnApplicationExit(object sender, ExitEventArgs e)
        {
            if (Application.Current != null)
                Application.Current.Exit -= OnApplicationExit;
            StopSettleTimer();
            ClearSentinel();
        }

        private static void StopSettleTimer()
        {
            System.Windows.Threading.DispatcherTimer? timer = _settleTimer;
            _settleTimer = null;
            if (timer == null)
                return;
            timer.Stop();
            timer.Tick -= OnRenderingSettled;
        }

        private static void ClearSentinel()
        {
            try
            {
                File.Delete(SentinelPath);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteException("RenderAcceleration::ClearSentinel", ex);
            }
        }

        private static void ApplyWindow(Window window)
        {
            try
            {
                if (PresentationSource.FromVisual(window) is not HwndSource source || source.CompositionTarget == null)
                    return;

                RenderMode mode = WindowsSoftwareOnly ? RenderMode.SoftwareOnly : RenderMode.Default;
                if (source.CompositionTarget.RenderMode != mode)
                    source.CompositionTarget.RenderMode = mode;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteException("RenderAcceleration::ApplyWindow", ex);
            }
        }
    }
}
