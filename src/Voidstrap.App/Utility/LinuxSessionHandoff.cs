using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class LinuxSessionHandoff
{
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(15);

    private static string Root
    {
        get
        {
            string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string parent = !string.IsNullOrWhiteSpace(runtime) && Path.IsPathRooted(runtime) ? runtime : Paths.Temp;
            return Path.Combine(parent, "voidstrap-session");
        }
    }

    private static string ResidentFile => Path.Combine(Root, "resident-player");

    private static string RequestFile => Path.Combine(Root, "handoff-request");

    public static void RegisterResident()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Write(ResidentFile, Describe(Environment.ProcessId));
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LinuxSessionHandoff::RegisterResident", "The session marker could not be written: " + ex.Message);
        }
    }

    public static void ClearResident()
    {
        string? self = Describe(Environment.ProcessId);
        if (self is null)
            return;
        foreach (string file in new[] { ResidentFile, RequestFile })
        {
            try
            {
                if (string.Equals(Read(file), self, StringComparison.Ordinal))
                    File.Delete(file);
            }
            catch (Exception)
            {
            }
        }
    }

    public static bool IsHandoffRequested()
    {
        string? request = Read(RequestFile);
        return request is not null && string.Equals(request, Describe(Environment.ProcessId), StringComparison.Ordinal);
    }

    public static async Task<bool> WaitForHandoffAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (true)
        {
            if (IsHandoffRequested())
                return true;
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
                return false;
            await Task.Delay((int)Math.Min(remaining, 250), cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<bool> RequestHandoffAsync(CancellationToken cancellationToken)
    {
        string? resident = Read(ResidentFile);
        if (resident is null || !TryParse(resident, out int processId) || processId == Environment.ProcessId)
            return false;
        if (!string.Equals(Describe(processId), resident, StringComparison.Ordinal) || !IsVoidstrapProcess(processId))
        {
            TryDelete(ResidentFile, resident);
            return false;
        }

        App.Logger.WriteLine("LinuxSessionHandoff::RequestHandoff", "Asking the Voidstrap session " + processId + " to hand the Roblox session over");
        try
        {
            Write(RequestFile, resident);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LinuxSessionHandoff::RequestHandoff", "The handoff request could not be written: " + ex.Message);
            return false;
        }

        long deadline = Environment.TickCount64 + (long)HandoffTimeout.TotalMilliseconds;
        while (string.Equals(Describe(processId), resident, StringComparison.Ordinal))
        {
            if (Environment.TickCount64 >= deadline)
            {
                App.Logger.WriteLine("LinuxSessionHandoff::RequestHandoff", "The previous Voidstrap session did not finish in time, continuing anyway");
                TryDelete(RequestFile, resident);
                return false;
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        TryDelete(RequestFile, resident);
        App.Logger.WriteLine("LinuxSessionHandoff::RequestHandoff", "The previous Voidstrap session handed the Roblox session over");
        return true;
    }

    private static string? Describe(int processId)
    {
        try
        {
            string stat = File.ReadAllText("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/stat");
            int close = stat.LastIndexOf(')');
            if (close < 0)
                return null;
            string[] fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 20 || fields[0] == "Z" || fields[0] == "X")
                return null;
            return processId.ToString(CultureInfo.InvariantCulture) + " " + fields[19];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsVoidstrapProcess(int processId)
    {
        try
        {
            string own = File.ReadAllText("/proc/self/comm").Trim();
            string other = File.ReadAllText("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/comm").Trim();
            return own.Length > 0 && string.Equals(own, other, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryParse(string value, out int processId)
    {
        int space = value.IndexOf(' ');
        return int.TryParse(space < 0 ? value : value[..space], NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0;
    }

    private static string? Read(string file)
    {
        try
        {
            string text = File.ReadAllText(file).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Write(string file, string? value)
    {
        if (value is null)
            return;
        Directory.CreateDirectory(Root);
        string temporary = file + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temporary, value);
        File.Move(temporary, file, true);
    }

    private static void TryDelete(string file, string expected)
    {
        try
        {
            if (string.Equals(Read(file), expected, StringComparison.Ordinal))
                File.Delete(file);
        }
        catch (Exception)
        {
        }
    }
}
