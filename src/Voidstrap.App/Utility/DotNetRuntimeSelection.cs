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

    public static async Task DownloadNet11Async(CancellationToken token)
    {
        const string LOG_IDENT = "DotNetRuntimeSelection::DownloadNet11Async";
        string target = Net11Path ?? throw new InvalidOperationException("VOIDSTRAP_NET11_PATH is not set");
        string url = $"{App.ProjectDownloadLink}/download/v{App.Version}/{Net11AssetName}";
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, "Voidstrap." + Guid.NewGuid().ToString("N") + ".tmp");
        App.Logger.WriteLine(LOG_IDENT, "Downloading " + url);
        try
        {
            using (HttpResponseMessage response = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(token);
                await using FileStream destination = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await source.CopyToAsync(destination, token);
            }
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(pending);
            if (info.ProductName != App.ProjectName || info.ProductVersion != App.Version)
                throw new InvalidDataException($"Unexpected download metadata: {info.ProductName} {info.ProductVersion}");
            File.Move(pending, target, overwrite: true);
            App.Logger.WriteLine(LOG_IDENT, "Saved " + target);
        }
        finally
        {
            File.Delete(pending);
        }
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
