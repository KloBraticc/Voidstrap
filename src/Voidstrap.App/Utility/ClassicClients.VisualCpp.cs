using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using Voidstrap.Resources;

namespace Voidstrap.Utility;

public static partial class ClassicClients
{
    private const int ErrorSxsCantGenActCtx = 14001;
    private const int ErrorCancelled = 1223;
    private const int ExecutableManifestId = 1;
    private static readonly SemaphoreSlim VisualCppInstallLock = new(1, 1);

    private static async Task EnsureVisualCpp2005Async(string executablePath, Action<string>? status, CancellationToken token)
    {
        const string LOG_IDENT = "ClassicClients::EnsureVisualCpp2005Async";

        if (!Platform.IsWindows || GetActivationError(executablePath) != ErrorSxsCantGenActCtx || !RequiresVisualCpp2005(executablePath))
            return;

        await VisualCppInstallLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (GetActivationError(executablePath) != ErrorSxsCantGenActCtx)
                return;

            App.Logger?.WriteLine(LOG_IDENT, $"Windows could not activate {Path.GetFileName(executablePath)} with its x86 Visual C++ 2005 dependencies");
            if (Frontend.ShowMessageBox(Strings.ClassicClients_VisualCpp_InstallPrompt, MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();

            int exitCode;
            try
            {
                exitCode = await InstallVisualCpp2005Async(status, token).ConfigureAwait(false);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                throw new OperationCanceledException(ex.Message, ex, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(Strings.ClassicClients_VisualCpp_InstallFailed, ex);
            }

            token.ThrowIfCancellationRequested();
            int windowsError = GetActivationError(executablePath);
            App.Logger?.WriteLine(LOG_IDENT, $"Installer exit code {exitCode}, activation context error {windowsError}");
            if (windowsError != 0)
                throw new InvalidOperationException(Strings.ClassicClients_VisualCpp_InstallFailed, new Win32Exception(windowsError));
        }
        finally
        {
            VisualCppInstallLock.Release();
        }
    }

    private static async Task<int> InstallVisualCpp2005Async(Action<string>? status, CancellationToken token)
    {
        const int MaxInstallerBytes = 3 * 1024 * 1024;
        const string InstallerSha256 = "8648C5FC29C44B9112FE52F9A33F80E7FC42D10F3B5B42B2121542A13E44ADFD";

        status?.Invoke(Strings.ClassicClients_VisualCpp_Downloading);
        using HttpResponseMessage response = await Http.GetAsync(
            App.ClassicVisualCppDownloadUrl, HttpCompletionOption.ResponseHeadersRead, token
        ).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[] installer = await ReadBoundedAsync(response.Content, MaxInstallerBytes, token).ConfigureAwait(false);

        DirectoryInfo tempDirectory = Directory.CreateTempSubdirectory("VoidstrapVisualCpp");
        string installerPath = Path.Combine(tempDirectory.FullName, "vcredist_x86.exe");
        try
        {
            await File.WriteAllBytesAsync(installerPath, installer, token).ConfigureAwait(false);
            using FileStream verifiedInstaller = File.OpenRead(installerPath);
            if (Convert.ToHexString(SHA256.HashData(verifiedInstaller)) != InstallerSha256)
                throw new InvalidDataException("The Visual C++ installer failed its integrity check");

            token.ThrowIfCancellationRequested();
            status?.Invoke(Strings.ClassicClients_VisualCpp_Installing);
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                WorkingDirectory = tempDirectory.FullName,
                Arguments = "/q /r:n",
                UseShellExecute = true,
                Verb = "runas",
            }) ?? throw new InvalidOperationException("Windows did not start the Visual C++ installer");
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            try
            {
                File.Delete(installerPath);
                tempDirectory.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static unsafe int GetActivationError(string executablePath)
    {
        const uint ResourceNameValid = 0x8;

        fixed (char* path = executablePath)
        {
            ActivationContext context = new()
            {
                Size = (uint)sizeof(ActivationContext),
                Flags = ResourceNameValid,
                Source = (nint)path,
                ResourceName = ExecutableManifestId,
            };
            nint handle = CreateActCtx(in context);
            if (handle == -1)
                return Marshal.GetLastPInvokeError();

            ReleaseActCtx(handle);
            return 0;
        }
    }

    private static unsafe bool RequiresVisualCpp2005(string executablePath)
    {
        const uint LoadLibraryAsDataFile = 0x2;
        const int ManifestResourceType = 24;
        const int MaxManifestBytes = 65536;

        nint module = LoadLibraryEx(executablePath, 0, LoadLibraryAsDataFile);
        if (module == 0)
            return false;

        try
        {
            nint resource = FindResource(module, ExecutableManifestId, ManifestResourceType);
            if (resource == 0)
                return false;
            uint length = SizeofResource(module, resource);
            if (length == 0 || length > MaxManifestBytes)
                return false;
            nint data = LockResource(LoadResource(module, resource));
            if (data == 0)
                return false;

            using UnmanagedMemoryStream manifest = new((byte*)data, length);
            using XmlReader reader = XmlReader.Create(manifest, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxManifestBytes,
            });
            XNamespace ns = "urn:schemas-microsoft-com:asm.v1";
            return XElement.Load(reader).Elements(ns + "dependency")
                .Elements(ns + "dependentAssembly")
                .Elements(ns + "assemblyIdentity")
                .Any(assembly =>
                    string.Equals((string?)assembly.Attribute("processorArchitecture"), "x86", StringComparison.OrdinalIgnoreCase)
                    && string.Equals((string?)assembly.Attribute("publicKeyToken"), "1fc8b3b9a1e18e3b", StringComparison.OrdinalIgnoreCase)
                    && (string?)assembly.Attribute("name") is "Microsoft.VC80.CRT" or "Microsoft.VC80.MFC"
                );
        }
        catch (XmlException)
        {
            return false;
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationContext
    {
        public uint Size;
        public uint Flags;
        public nint Source;
        public ushort ProcessorArchitecture;
        public ushort LanguageId;
        public nint AssemblyDirectory;
        public nint ResourceName;
        public nint ApplicationName;
        public nint Module;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateActCtxW", SetLastError = true)]
    private static partial nint CreateActCtx(in ActivationContext context);

    [LibraryImport("kernel32.dll")]
    private static partial void ReleaseActCtx(nint activationContext);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindResourceW")]
    private static partial nint FindResource(nint module, nint name, nint type);

    [LibraryImport("kernel32.dll")]
    private static partial uint SizeofResource(nint module, nint resource);

    [LibraryImport("kernel32.dll")]
    private static partial nint LoadResource(nint module, nint resource);

    [LibraryImport("kernel32.dll")]
    private static partial nint LockResource(nint resource);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);
}
