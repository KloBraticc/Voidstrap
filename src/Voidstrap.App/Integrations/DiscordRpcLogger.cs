using System;
using System.Threading;
using DiscordRPC.Logging;

namespace Voidstrap.Integrations
{
    public sealed class DiscordRpcLogger : ILogger
    {
        private const string KnownLibraryDefect = "Unhandled Exception while processing event";

        private static readonly string[] ExpectedConnectionMessages =
        {
            "Failed connection to ",
            "Tried to close a already closed pipe.",
            "Failed to connect for some reason."
        };

        private readonly string _identifier;

        private int _suppressedDefectLines;

        public DiscordRpcLogger(string identifier)
        {
            _identifier = identifier;
        }

        public LogLevel Level { get; set; } = LogLevel.Warning;

        public void Trace(string message, params object[] args) => Write(LogLevel.Trace, message, args);

        public void Info(string message, params object[] args) => Write(LogLevel.Info, message, args);

        public void Warning(string message, params object[] args) => Write(LogLevel.Warning, message, args);

        public void Error(string message, params object[] args) => Write(LogLevel.Error, message, args);

        private void Write(LogLevel level, string message, object[] args)
        {
            if (Level > level || string.IsNullOrEmpty(message))
                return;

            if (message.Contains(KnownLibraryDefect))
            {
                if (Voidstrap.Utility.Platform.IsLinux)
                    Volatile.Write(ref _suppressedDefectLines, 2);
                return;
            }

            foreach (string expected in ExpectedConnectionMessages)
            {
                if (message.StartsWith(expected, StringComparison.Ordinal))
                    return;
            }

            if (Voidstrap.Utility.Platform.IsLinux && level >= LogLevel.Warning)
            {
                while (true)
                {
                    int remaining = Volatile.Read(ref _suppressedDefectLines);
                    if (remaining <= 0)
                        break;
                    if (Interlocked.CompareExchange(ref _suppressedDefectLines, remaining - 1, remaining) == remaining)
                        return;
                }
            }

            string formatted;

            try
            {
                formatted = args != null && args.Length > 0 ? string.Format(message, args) : message;
            }
            catch (FormatException)
            {
                formatted = message;
            }

            App.Logger.WriteLine(_identifier, formatted);
        }
    }
}
