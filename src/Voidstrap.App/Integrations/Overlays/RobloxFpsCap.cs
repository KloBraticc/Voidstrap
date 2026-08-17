using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Voidstrap.Integrations.Overlays
{
    public static class RobloxFpsCap
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox", "GlobalBasicSettings_13.xml");

        private static readonly Regex CapRegex = new Regex(
            "<int name=\"FramerateCap\">\\s*(\\d+)\\s*</int>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static int _cap;
        private static bool _started;
        private static FileSystemWatcher? _watcher;
        private static System.Threading.Timer? _debounce;
        private static System.Threading.Timer? _poll;
        private static DateTime _lastWriteUtc;
        private static readonly object _lock = new object();

        public static int Cap => Volatile.Read(ref _cap);

        public static bool IsUnlimited
        {
            get
            {
                int c = Cap;
                return c <= 0 || c >= 1000;
            }
        }

        public static string Describe()
        {
            int c = Cap;
            if (c <= 0)
                return "not set";
            if (c >= 1000)
                return $"{c} (unlimited)";
            return c.ToString();
        }

        public static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_started)
                    return;
                _started = true;
                Reload();
				_poll = new System.Threading.Timer(OnPoll, null, 5000, 5000);
                try
                {
                    string? dir = Path.GetDirectoryName(FilePath);
                    if (dir != null && Directory.Exists(dir))
                    {
                        _debounce = new System.Threading.Timer(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);
                        _watcher = new FileSystemWatcher(dir, "GlobalBasicSettings_13.xml")
                        {
                            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        };
                        _watcher.Changed += OnChanged;
                        _watcher.Created += OnChanged;
                        _watcher.Renamed += OnRenamed;
                        _watcher.EnableRaisingEvents = true;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("RobloxFpsCap::EnsureStarted", ex);
                }
            }
        }

        private static void OnChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                _debounce?.Change(250, Timeout.Infinite);
            }
            catch
            {
            }
        }

        private static void OnRenamed(object sender, RenamedEventArgs e)
        {
            try
            {
                _debounce?.Change(250, Timeout.Infinite);
            }
            catch
            {
            }
        }

        private static void OnDebounce(object? state)
        {
            try
            {
                Reload();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RobloxFpsCap::OnDebounce", ex);
            }
        }

        private static void OnPoll(object? state)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;
                if (File.GetLastWriteTimeUtc(FilePath) != _lastWriteUtc)
                    Reload();
            }
            catch
            {
            }
        }

        public static void ReloadNow()
        {
            Reload();
        }

        private static string BaseFpsPath()
        {
            string root = !string.IsNullOrEmpty(Paths.Config)
                ? Paths.Config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voidstrap", "Config");
            return Path.Combine(root, "FrameGenBase.txt");
        }

        private static long _lastBaseWriteMs;
        private static long _lastBaseReadMs;
        private static double _cachedBaseFps;

        public static void ReportMeasuredBase(double fps)
        {
            if (fps < 5 || fps > 1000)
                return;
            long now = Environment.TickCount64;
            if (now - _lastBaseWriteMs < 5000)
                return;
            _lastBaseWriteMs = now;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string path = BaseFpsPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, fps.ToString("0.0", CultureInfo.InvariantCulture));
                }
                catch
                {
                }
            });
        }

        public static double RecentMeasuredBase()
        {
            long now = Environment.TickCount64;
            if (now - _lastBaseReadMs < 10000)
                return _cachedBaseFps;
            _lastBaseReadMs = now;
            _cachedBaseFps = 0;
            try
            {
                string path = BaseFpsPath();
                if (File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalMinutes <= 3)
                {
                    if (double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= 5 && v <= 1000)
                        _cachedBaseFps = v;
                }
            }
            catch
            {
            }
            return _cachedBaseFps;
        }

        public const int BestBelowFps = 120;

        private static int ClampForFrameGen(int refreshHz, int cap)
        {
            if (cap < BestBelowFps)
                return cap;
            for (int div = 2; div <= 8; div++)
            {
                int candidate = refreshHz / div;
                if (candidate < BestBelowFps && candidate >= 30)
                    return candidate;
            }
            return Math.Max(30, BestBelowFps - 1);
        }

        public static int PickBestCap(int refreshHz, double measuredBaseFps)
        {
            return ClampForFrameGen(refreshHz < 60 ? 60 : refreshHz, PickBestCapCore(refreshHz, measuredBaseFps));
        }

        private static int PickBestCapCore(int refreshHz, double measuredBaseFps)
        {
            if (refreshHz < 60)
                refreshHz = 60;
            if (measuredBaseFps < 20)
                return Math.Max(30, refreshHz / 2);
            int current = Cap;
            bool capLimited = current > 0 && current < 1000 && Math.Abs(measuredBaseFps - current) <= 3.0;
            if (capLimited)
            {
                for (int div = 6; div >= 2; div--)
                {
                    int candidate = refreshHz / div;
                    if (candidate >= 30 && candidate > current + 2)
                        return candidate;
                }
                return Math.Max(30, refreshHz / 2);
            }
            for (int div = 2; div <= 6; div++)
            {
                int candidate = refreshHz / div;
                if (candidate < 30)
                    break;
                if (candidate <= measuredBaseFps + 5)
                    return candidate;
            }
            return Math.Max(30, (int)measuredBaseFps);
        }

        private static void Reload()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;
                _lastWriteUtc = File.GetLastWriteTimeUtc(FilePath);
                string text;
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    text = sr.ReadToEnd();
                var m = CapRegex.Match(text);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int v))
                {
                    int preferred = App.Settings.Prop.FrameGenTargetFps;
                    if (Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0 && preferred >= 5 && preferred < 1000)
                        v = preferred;
                    if (v >= 5 && v < 15)
                        v = 15;
                    Volatile.Write(ref _cap, v);
                }
            }
            catch
            {
            }
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                try
                {
                    if (_watcher != null)
                    {
                        _watcher.EnableRaisingEvents = false;
                        _watcher.Changed -= OnChanged;
                        _watcher.Created -= OnChanged;
                        _watcher.Renamed -= OnRenamed;
                        _watcher.Dispose();
                        _watcher = null;
                    }
                    _debounce?.Dispose();
                    _debounce = null;
                    _poll?.Dispose();
                    _poll = null;
                }
                catch
                {
                }
                _started = false;
            }
        }
    }
}
