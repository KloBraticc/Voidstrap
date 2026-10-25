using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Voidstrap.Integrations.RiShade
{
    internal sealed class RiShadeDlssDiagnostics : IDisposable
    {
        private const string LOG_IDENT = "RiShadeDlss";
        private const string AddonTag = "[RenoDX DLSS]";
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan LoadDeadline = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan FrameDeadline = TimeSpan.FromSeconds(40);
        private static readonly TimeSpan WatchLength = TimeSpan.FromMinutes(3);
        private const int MaxForwardedIssues = 25;

        private readonly string _logPath;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
        private readonly Timer _timer;
        private readonly object _gate = new();
        private long _position;
        private string _partial = string.Empty;
        private bool _registered;
        private bool _deviceReady;
        private bool _framesSeen;
        private bool _evaluating;
        private bool _loadVerdictLogged;
        private bool _frameVerdictLogged;
        private int _forwardedIssues;
        private bool _disposed;

        private RiShadeDlssDiagnostics(string logPath)
        {
            _logPath = logPath;
            _timer = new Timer(OnTick, null, PollInterval, PollInterval);
        }

        public static RiShadeDlssDiagnostics Start(string riShadeDirectory)
        {
            App.Logger.WriteLine(LOG_IDENT, "Watching ReShade.log to confirm DLSS loads, attaches and evaluates frames");
            return new RiShadeDlssDiagnostics(Path.Combine(riShadeDirectory, "ReShade.log"));
        }

        private void OnTick(object? state)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                try
                {
                    ReadNewLines();
                    CheckDeadlines();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report("read", "ReShade.log could not be read yet: " + ex.Message);
                }
                if (DateTime.UtcNow - _startedUtc >= WatchLength || _evaluating)
                {
                    if (!_evaluating && _framesSeen)
                        App.Logger.WriteLine(LOG_IDENT, "DLSS received frames but its log never confirmed an evaluated frame, it may be running in passthrough");
                    StopTimer();
                }
            }
        }

        private void ReadNewLines()
        {
            if (!File.Exists(_logPath))
                return;
            using FileStream stream = new(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                _position = 0;
                _partial = string.Empty;
            }
            if (stream.Length == _position)
                return;
            stream.Seek(_position, SeekOrigin.Begin);
            using StreamReader reader = new(stream, Encoding.UTF8, true, 65536, leaveOpen: true);
            string text = _partial + reader.ReadToEnd();
            _position = stream.Length;
            int last = text.LastIndexOf('\n');
            _partial = last < 0 ? text : text[(last + 1)..];
            if (last < 0)
                return;
            foreach (string raw in text[..last].Split('\n'))
                Inspect(raw.TrimEnd('\r'));
        }

        private void Inspect(string line)
        {
            if (line.Contains("Registered add-on \"RenoDX DLSS\"", StringComparison.Ordinal))
            {
                _registered = true;
                Report("registered", "DLSS addon loaded into ReShade");
                return;
            }
            if (line.Contains("Loading add-on from", StringComparison.Ordinal) && line.Contains("renodx-dlss", StringComparison.OrdinalIgnoreCase))
                return;
            if (line.Contains("Failed to load add-on", StringComparison.Ordinal) && line.Contains("renodx", StringComparison.OrdinalIgnoreCase))
            {
                Report("loadfail", "ReShade could not load the DLSS addon: " + Message(line));
                return;
            }
            if (!line.Contains(AddonTag, StringComparison.Ordinal))
                return;
            string message = Message(line);
            if (message.Contains("init_device end", StringComparison.Ordinal))
            {
                _deviceReady = true;
                Report("device", "DLSS attached to the compositor graphics device");
            }
            else if (message.Contains("first present", StringComparison.Ordinal))
            {
                _framesSeen = true;
                Report("present", "DLSS is receiving frames from the compositor");
            }
            else if (message.Contains("owned-evaluated=true", StringComparison.Ordinal) || message.Contains("common-active=true", StringComparison.Ordinal))
            {
                _evaluating = true;
                Report("evaluating", "DLSS is evaluating frames, it is working");
            }
            else if (message.Contains("not loaded yet", StringComparison.Ordinal) || message.Contains("not found", StringComparison.Ordinal))
            {
                Report("info:" + message, "DLSS note: " + message);
            }
            bool failedCall = message.Contains(" hr=0x", StringComparison.Ordinal) && !message.Contains(" hr=0x00000000", StringComparison.Ordinal);
            if (line.Contains("| ERROR", StringComparison.Ordinal) || line.Contains("| WARN", StringComparison.Ordinal) || failedCall)
                ForwardIssue(line.Contains("| ERROR", StringComparison.Ordinal) || failedCall ? "error" : "warning", message);
        }

        private void ForwardIssue(string kind, string message)
        {
            if (_forwardedIssues >= MaxForwardedIssues || !_reported.Add(kind + ":" + message))
                return;
            _forwardedIssues++;
            App.Logger.WriteLine(LOG_IDENT, "DLSS " + kind + ": " + message);
            if (_forwardedIssues == MaxForwardedIssues)
                App.Logger.WriteLine(LOG_IDENT, "Further DLSS warnings are only in ReShade.log");
        }

        private void CheckDeadlines()
        {
            TimeSpan elapsed = DateTime.UtcNow - _startedUtc;
            if (!_registered && !_loadVerdictLogged && elapsed >= LoadDeadline)
            {
                _loadVerdictLogged = true;
                App.Logger.WriteLine(LOG_IDENT, "DLSS addon did not load: " + DescribeMissingAddon());
            }
            if (_registered && !_framesSeen && !_frameVerdictLogged && elapsed >= FrameDeadline)
            {
                _frameVerdictLogged = true;
                App.Logger.WriteLine(LOG_IDENT, _deviceReady
                    ? "DLSS attached to the device but never received a frame, the compositor may not be presenting"
                    : "DLSS loaded but never attached to the compositor device, the device was created without ReShade's wrapper");
            }
        }

        private string DescribeMissingAddon()
        {
            string directory = Path.GetDirectoryName(_logPath) ?? string.Empty;
            if (!File.Exists(Path.Combine(directory, "renodx-dlss.addon64")) || !File.Exists(Path.Combine(directory, "nvngx_dlssnr.dll")))
                return "its files are missing, turn DLSS off and on again in the RiShade settings";
            try
            {
                foreach (string line in File.ReadLines(Path.Combine(directory, "ReShade.ini")))
                {
                    if (line.StartsWith("DisabledAddons=", StringComparison.OrdinalIgnoreCase) && line.Contains("RenoDX DLSS", StringComparison.OrdinalIgnoreCase))
                        return "it is turned off in ReShade's addon list";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "ReShade.ini could not be read: " + ex.Message;
            }
            return "ReShade did not report it, check ReShade.log in the RiShade folder";
        }

        private void Report(string key, string message)
        {
            if (_reported.Add(key))
                App.Logger.WriteLine(LOG_IDENT, message);
        }

        private static string Message(string line)
        {
            int tag = line.IndexOf(AddonTag, StringComparison.Ordinal);
            if (tag >= 0)
                return line[(tag + AddonTag.Length)..].Trim();
            int bar = line.LastIndexOf("| ", StringComparison.Ordinal);
            return bar >= 0 ? line[(bar + 2)..].Trim() : line.Trim();
        }

        private void StopTimer()
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _timer.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
