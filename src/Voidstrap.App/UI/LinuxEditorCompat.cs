using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Voidstrap.UI
{
    public static class LinuxEditorCompat
    {
        private const string StubFileName = "libvoidstrapeditorcompat.so";

        private static readonly string[] RedirectedLibraries = { "imm32.dll", "user32.dll", "msctf.dll" };

        private static IntPtr _stub;

        private static bool _installed;

        public static void Install()
        {
            if (_installed || !Voidstrap.Utility.Platform.IsLinux)
                return;

            try
            {
                AssemblyLoadContext.Default.Resolving -= OnAssemblyResolving;
                AssemblyLoadContext.Default.Resolving += OnAssemblyResolving;

                Assembly editor = typeof(ICSharpCode.AvalonEdit.TextEditor).Assembly;
                NativeLibrary.SetDllImportResolver(editor, Resolve);
                _installed = true;
                App.Logger.WriteLine("LinuxEditorCompat::Install", "Editor native calls are redirected to the portable stub");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LinuxEditorCompat::Install", $"Could not redirect editor native calls: {ex.Message}");
            }
        }

        private static Assembly? OnAssemblyResolving(AssemblyLoadContext context, AssemblyName name)
        {
            if (!string.Equals(name.Name, "System.Windows.Forms", StringComparison.OrdinalIgnoreCase))
                return null;

            string candidate = Path.Combine(AppContext.BaseDirectory, "System.Windows.Forms.dll");
            if (!File.Exists(candidate))
                return null;

            try
            {
                return context.LoadFromAssemblyPath(candidate);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LinuxEditorCompat::OnAssemblyResolving", $"Could not load the editor compatibility assembly: {ex.Message}");
                return null;
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            foreach (string redirected in RedirectedLibraries)
            {
                if (string.Equals(libraryName, redirected, StringComparison.OrdinalIgnoreCase))
                    return LoadStub();
            }

            return IntPtr.Zero;
        }

        private static IntPtr LoadStub()
        {
            if (_stub != IntPtr.Zero)
                return _stub;

            string candidate = Path.Combine(AppContext.BaseDirectory, StubFileName);
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle) || NativeLibrary.TryLoad(StubFileName, out handle))
            {
                _stub = handle;
                return _stub;
            }

            App.Logger.WriteLine("LinuxEditorCompat::LoadStub", "The portable editor stub could not be loaded");
            return IntPtr.Zero;
        }
    }
}
