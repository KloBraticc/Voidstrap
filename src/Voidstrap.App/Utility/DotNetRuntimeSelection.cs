using System.Runtime.InteropServices;

namespace Voidstrap.Utility;

internal static partial class DotNetRuntimeSelection
{
    private static readonly bool WindowsLauncherSupportsSelection = Environment.GetEnvironmentVariable("VOIDSTRAP_RUNTIME_CHOOSER") == "1";
    private static readonly string? Executable = Environment.ProcessPath;
    private static readonly string? Companion = Executable == null ? null : Path.Combine(Path.GetDirectoryName(Executable)!, "Voidstrap.net11");

    public static bool IsAvailable => OperatingSystem.IsWindows()
        ? Paths.StartedByLauncher && WindowsLauncherSupportsSelection
        : File.Exists(Companion);

    public static bool CanUseNet11 => !OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VOIDSTRAP_NET11_AVAILABLE") == "1";

    private static string? Net11Path => Environment.GetEnvironmentVariable("VOIDSTRAP_NET11_PATH");

    public static bool IsNet11Downloaded => !OperatingSystem.IsWindows() || (Net11Path is { } path && File.Exists(path));

    public static async Task<string?> DownloadNet11Async(CancellationToken token)
    {
        const string LOG_IDENT = "DotNetRuntimeSelection::DownloadNet11Async";
        if (string.IsNullOrWhiteSpace(Net11Path))
            return "VOIDSTRAP_NET11_PATH is not set";

        string target = Net11Path;
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, "Voidstrap." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            string? launcherDirectory = Paths.StartedByLauncher ? Path.GetDirectoryName(Paths.Process) : null;
            string? localAsset = launcherDirectory == null ? null : Path.Combine(launcherDirectory, Net11AssetName);
            if (localAsset != null && File.Exists(localAsset) && HasExpectedVersion(localAsset))
            {
                File.Copy(localAsset, pending, overwrite: true);
                File.Move(pending, target, overwrite: true);
                App.Logger.WriteLine(LOG_IDENT, "Copied local " + localAsset);
                return null;
            }

            string url = $"{App.ProjectDownloadLink}/download/v{App.Version}/{Net11AssetName}";
            App.Logger.WriteLine(LOG_IDENT, "Downloading " + url);
            using (HttpResponseMessage response = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    string status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                    App.Logger.WriteLine(LOG_IDENT, "Download returned " + status);
                    return status;
                }

                await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using FileStream destination = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await source.CopyToAsync(destination, token).ConfigureAwait(false);
            }

            if (!HasExpectedVersion(pending))
                return "Downloaded file version mismatch";

            File.Move(pending, target, overwrite: true);
            App.Logger.WriteLine(LOG_IDENT, "Saved " + target);
            return null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
            return ex.Message;
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }

    private static bool HasExpectedVersion(string path)
    {
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName == App.ProjectName && info.ProductVersion == App.Version;
    }

    private const string Net11AssetName = "Voidstrap-net11.exe";

    private static string PreferencePath => Path.Combine(
        OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Paths.LocalAppData : Paths.LocalAppData,
        "Voidstrap",
        "DotNetRuntime");

    public static int Preferred
    {
        get
        {
            if (!File.Exists(PreferencePath))
                return 10;
            try
            {
                using FileStream stream = File.OpenRead(PreferencePath);
                return stream.Length == 2 && stream.ReadByte() == '1' && stream.ReadByte() == '1' ? 11 : 10;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 10;
            }
        }
    }

    public static void Save(int major)
    {
        string path = PreferencePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(pending, major == 11 ? "11" : "10");
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    public static string? UnixPrimaryExecutable
    {
        get
        {
            if (OperatingSystem.IsWindows() || Companion == null || Executable != Companion)
                return null;
            string directory = Path.GetDirectoryName(Companion)!;
            string primary = Path.Combine(directory, "Voidstrap");
            return File.Exists(primary) ? primary : Path.Combine(directory, "voidstrap");
        }
    }

    public static void LaunchPreferred(string[] args)
    {
        if (OperatingSystem.IsWindows() || Environment.Version.Major != 10 || Companion == null || !File.Exists(Companion) || Preferred != 11)
            return;

        nint[] arguments = new nint[args.Length + 1];
        nint argv = Marshal.AllocHGlobal(arguments.Length * IntPtr.Size);
        try
        {
            arguments[0] = Marshal.StringToCoTaskMemUTF8(Companion);
            for (int index = 1; index < args.Length; index++)
                arguments[index] = Marshal.StringToCoTaskMemUTF8(args[index]);
            Marshal.Copy(arguments, 0, argv, arguments.Length);
            Exec(Companion, argv);
        }
        finally
        {
            foreach (nint argument in arguments)
                Marshal.FreeCoTaskMem(argument);
            Marshal.FreeHGlobal(argv);
        }
    }

    [LibraryImport("libc", EntryPoint = "execv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Exec(string path, nint arguments);
}
