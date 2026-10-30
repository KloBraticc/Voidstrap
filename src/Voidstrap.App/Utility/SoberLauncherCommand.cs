using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class SoberLauncherCommand
{
    private const string LogIdent = "SoberLauncherCommand";

    public static async Task<LinuxSoberLauncher> ResolveAsync(string? text, CancellationToken token)
    {
        List<string> words = Split(text);
        words.RemoveAll(static word => string.Equals(word, "%command%", StringComparison.Ordinal));
        LinuxSoberLauncher launcher = LinuxSoberLauncher.Create(words);
        Voidstrap.Core.SystemProcessService processes = new();
        if (launcher.MangoHud)
        {
            App.Logger.WriteLine(LogIdent, "Preparing MangoHud for Sober's Flatpak runtime");
            OperationResult installed = await LinuxEffectLayers
                .InstallAsync(processes, LinuxEffectLayers.MangoHudLayerId, token)
                .ConfigureAwait(false);
            if (!installed.Succeeded)
                throw new InvalidOperationException("MangoHud could not be prepared for Sober. " + installed.Failure?.Message);
            List<string> arguments = [.. launcher.FlatpakArguments, "--filesystem=xdg-config/MangoHud:ro"];
            if (!arguments.Any(static argument => argument.StartsWith("--env=MANGOHUD_CONFIGFILE=", StringComparison.Ordinal)))
            {
                if (
                    LinuxFlatpakHost.TryCreateHostCommand(
                        processes,
                        "sh",
                        [
                            "-c",
                            "file=\"${XDG_CONFIG_HOME:-$HOME/.config}/MangoHud/MangoHud.conf\"; if [ -f \"$file\" ]; then printf '%s' \"$file\"; fi",
                        ],
                        out ProcessCommand configCommand
                    )
                )
                {
                    OperationResult<ProcessExecution> config = await processes.ExecuteAsync(configCommand, token).ConfigureAwait(false);
                    if (config.Succeeded && config.Value is { ExitCode: 0 } result && !string.IsNullOrWhiteSpace(result.StandardOutput))
                        arguments.Add("--env=MANGOHUD_CONFIGFILE=" + result.StandardOutput);
                }
            }
            foreach (string argument in arguments.ToArray())
            {
                if (
                    argument.StartsWith("--env=MANGOHUD_CONFIGFILE=/", StringComparison.Ordinal)
                    || argument.StartsWith("--env=MANGOHUD_PRESETSFILE=/", StringComparison.Ordinal)
                )
                {
                    string path = argument[(argument.IndexOf('=') + 1)..];
                    path = path[(path.IndexOf('=') + 1)..];
                    arguments.Add("--filesystem=" + Path.GetDirectoryName(path) + ":ro");
                }
            }
            launcher = launcher with { FlatpakArguments = arguments };
        }
        if (launcher.HostPrefix.Count > 0 && !await ExistsOnHostAsync(launcher.HostPrefix[0], token).ConfigureAwait(false))
        {
            App.Logger.WriteLine(
                LogIdent,
                "The Sober launcher '" + launcher.HostPrefix[0] + "' was not found, starting Sober without that command"
            );
            launcher = launcher with { HostPrefix = [] };
        }
        token.ThrowIfCancellationRequested();
        return launcher;
    }

    internal static List<string> Split(string? text)
    {
        List<string> words = [];
        if (string.IsNullOrWhiteSpace(text))
            return words;

        StringBuilder word = new();
        bool started = false;
        char quote = '\0';
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else if (character == '\\' && quote == '"' && index + 1 < text.Length && text[index + 1] is '"' or '\\')
                    word.Append(text[++index]);
                else
                    word.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                started = true;
                continue;
            }

            if (character == '\\' && index + 1 < text.Length)
            {
                word.Append(text[++index]);
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (started)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    started = false;
                }
                continue;
            }

            word.Append(character);
            started = true;
        }

        if (quote != '\0')
            throw new FormatException("The Sober launcher contains an unclosed quote.");
        if (started)
            words.Add(word.ToString());
        return words;
    }

    private static async Task<bool> ExistsOnHostAsync(string program, CancellationToken token)
    {
        Voidstrap.Core.SystemProcessService processes = new();
        if (
            !LinuxFlatpakHost.TryCreateHostCommand(
                processes,
                "sh",
                ["-c", "command -v \"$1\" >/dev/null 2>&1", "sh", program],
                out ProcessCommand command
            )
        )
            return false;
        OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result.Succeeded && result.Value is { ExitCode: 0 };
    }
}
