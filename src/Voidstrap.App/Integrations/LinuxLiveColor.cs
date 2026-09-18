using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;
using Voidstrap.Utility;

namespace Voidstrap.Integrations
{
    public static class LinuxLiveColor
    {
        private const int DebounceMilliseconds = 500;

        private static readonly object Sync = new();
        private static readonly Voidstrap.Core.SystemProcessService Processes = new();

        private static CancellationTokenSource? _pending;
        private static string? _lastPushed;

        public static void Schedule()
        {
            if (!Voidstrap.Utility.Platform.IsLinux)
                return;

            CancellationTokenSource source = new();
            CancellationTokenSource? previous;

            lock (Sync)
            {
                previous = _pending;
                _pending = source;
            }

            if (previous is not null)
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                previous.Dispose();
            }

            _ = RunAsync(source);
        }

        private static async Task RunAsync(CancellationTokenSource source)
        {
            try
            {
                await Task.Delay(DebounceMilliseconds, source.Token).ConfigureAwait(false);
                await ApplyAsync(source.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger?.WriteException("LinuxLiveColor::RunAsync", ex);
            }
            finally
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_pending, source))
                        _pending = null;
                }

                source.Dispose();
            }
        }

        private const int TrackingIntervalMilliseconds = 1000;
        private const int TrackingAttempts = 90;

        private static CancellationTokenSource? _tracking;

        public static void BeginTracking()
        {
            if (!Voidstrap.Utility.Platform.IsLinux)
                return;

            StopTracking();

            CancellationTokenSource source = new();
            lock (Sync)
            {
                _tracking = source;
            }

            _ = TrackAsync(source);
        }

        public static void StopTracking()
        {
            CancellationTokenSource? previous;
            lock (Sync)
            {
                previous = _tracking;
                _tracking = null;
            }

            if (previous is null)
                return;

            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            previous.Dispose();
        }

        private static async Task TrackAsync(CancellationTokenSource source)
        {
            try
            {
                for (int attempt = 0; attempt < TrackingAttempts; attempt++)
                {
                    if (source.Token.IsCancellationRequested)
                        return;

                    OperationResult applied = await LinuxGamescope.ApplyLookAsync(
                        Processes,
                        LinuxColorLut.LutFile,
                        source.Token).ConfigureAwait(false);

                    if (applied.Succeeded)
                    {
                        App.Logger?.WriteLine("LinuxLiveColor::TrackAsync", "Colour effects attached to the running game");
                        return;
                    }

                    await Task.Delay(TrackingIntervalMilliseconds, source.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger?.WriteException("LinuxLiveColor::TrackAsync", ex);
            }
            finally
            {
                lock (Sync)
                {
                    if (ReferenceEquals(_tracking, source))
                        _tracking = null;
                }

                source.Dispose();
            }
        }

        public static Task WriteAsync()
        {
            if (!Voidstrap.Utility.Platform.IsLinux)
                return Task.CompletedTask;

            float[]? matrix = BuildCurrentMatrix();
            OperationResult written = LinuxColorLut.Write(LinuxColorLut.LutFile, matrix);

            if (!written.Succeeded)
                App.Logger?.WriteLine("LinuxLiveColor::WriteAsync", written.Failure?.Message ?? "The colour table could not be written");

            return Task.CompletedTask;
        }

        private static float[]? BuildCurrentMatrix()
        {
            return ScreenColorEffect.BuildMatrix(
                App.Settings.Prop.Saturation,
                App.Settings.Prop.Contrast,
                App.Settings.Prop.ColorTemperature,
                App.Settings.Prop.ColorBlindnessEnabled,
                (ScreenColorEffect.ColorBlindnessType)App.Settings.Prop.ColorBlindnessType,
                App.Settings.Prop.ColorBlindnessSeverity / 100.0,
                App.Settings.Prop.ColorBlindnessSimulate);
        }

        public static async Task ApplyAsync(CancellationToken cancellationToken = default)
        {
            if (!Voidstrap.Utility.Platform.IsLinux)
                return;

            float[]? matrix = BuildCurrentMatrix();

            string file = LinuxColorLut.LutFile;

            OperationResult written = LinuxColorLut.Write(file, matrix);
            if (!written.Succeeded)
            {
                App.Logger?.WriteLine("LinuxLiveColor::ApplyAsync", written.Failure?.Message ?? "The colour table could not be written");
                return;
            }

            string signature = BuildSignature(matrix);
            OperationResult applied = await LinuxGamescope.ApplyLookAsync(Processes, file, cancellationToken).ConfigureAwait(false);

            if (!applied.Succeeded)
            {
                if (!string.Equals(applied.Failure?.Code, "GamescopeNotRunning", StringComparison.Ordinal))
                    App.Logger?.WriteLine("LinuxLiveColor::ApplyAsync", applied.Failure?.Message ?? "The colour effect could not be applied");

                return;
            }

            if (!string.Equals(_lastPushed, signature, StringComparison.Ordinal))
            {
                _lastPushed = signature;
                App.Logger?.WriteLine("LinuxLiveColor::ApplyAsync", "Colour effects applied live");
            }
        }

        private static string BuildSignature(float[]? matrix)
        {
            if (matrix is null)
                return "neutral";

            System.Text.StringBuilder builder = new(160);

            foreach (float value in matrix)
            {
                builder.Append(':');
                builder.Append(value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
