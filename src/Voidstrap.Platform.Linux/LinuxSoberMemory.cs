using System.Globalization;

namespace Voidstrap.Platform.Linux;

public static class LinuxSoberMemory
{
    private const string MemoryCommand = """
        uid=
        while read -r key value rest; do
            if [ "$key" = Uid: ]; then uid=$value; break; fi
        done < /proc/self/status
        [ -n "$uid" ] || exit 1
        for directory in /proc/[0-9]*; do
            matched=0
            if [ -r "$directory/cgroup" ]; then
                while IFS= read -r group; do
                    case "$group" in
                        *app-flatpak-org.vinegarhq.Sober-*.scope*) matched=1; break ;;
                    esac
                done < "$directory/cgroup" 2>/dev/null
            fi
            if [ "$matched" = 0 ] && [ -r "$directory/comm" ]; then
                IFS= read -r name < "$directory/comm" 2>/dev/null || continue
                [ "$name" = sober ] || continue
                matched=1
            fi
            [ "$matched" = 1 ] || continue
            if [ ! -r "$directory/status" ]; then
                [ ! -d "$directory" ] && continue
                exit 1
            fi
            owner=
            resident=
            while read -r key value unit rest; do
                case "$key" in
                    Uid:) owner=$value ;;
                    VmRSS:) [ "$unit" = kB ] || exit 1; resident=$value ;;
                esac
            done < "$directory/status" 2>/dev/null || { [ ! -d "$directory" ] && continue; exit 1; }
            [ "$owner" = "$uid" ] || continue
            [ -n "$resident" ] || continue
            printf '%s\n' "$resident"
        done
        """;

    public static async Task<long?> ReadAsync(IProcessService processes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        cancellationToken.ThrowIfCancellationRequested();
        if (!LinuxFlatpakHost.IsSandboxed)
            return await Task.Run(() => ReadLocalMemory(cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!LinuxFlatpakHost.TryCreateHostCommand(processes, "sh", ["-c", MemoryCommand], out ProcessCommand command))
            return null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded || result.Value is not { ExitCode: 0 })
            return null;
        long total = 0;
        foreach (string line in result.Value.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!long.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out long kilobytes)
                || kilobytes > (long.MaxValue - total) / 1024)
                return null;
            total += kilobytes * 1024;
        }
        return total;
    }
    private static long? ReadLocalMemory(CancellationToken cancellationToken)
    {
        try
        {
            string? currentOwner = ReadOwner("/proc/self/status");
            if (currentOwner is null)
                return null;
            long total = 0;
            foreach (int processId in LinuxSoberProcessProbe.GetSandboxProcessIds())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string status = $"/proc/{processId}/status";
                try
                {
                    if (ReadOwner(status) != currentOwner)
                        continue;
                    foreach (string line in File.ReadLines(status))
                    {
                        if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                            continue;
                        string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length != 3 || fields[2] != "kB"
                            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kilobytes)
                            || kilobytes > (long.MaxValue - total) / 1024)
                            return null;
                        total += kilobytes * 1024;
                        break;
                    }
                }
                catch (IOException)
                {
                    if (Directory.Exists($"/proc/{processId}"))
                        return null;
                }
            }
            return total;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadOwner(string status)
    {
        foreach (string line in File.ReadLines(status))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                continue;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 ? fields[1] : null;
        }
        return null;
    }

}
