using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Extensions;

public partial class RobloxFullscreen
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);


    private const byte VK_MENU = 0x12;   // Alt key
    private const byte VK_RETURN = 0x0D; // Enter key
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static async Task WaitAndTriggerFullscreenAsync(CancellationToken cancellationToken)
    {
        const string LOG_IDENT = "RobloxFullscreen::WaitAndTriggerAltEnter";

        string processName = Voidstrap.App.RobloxPlayerAppName.Split('.')[0];
        Voidstrap.App.Logger.WriteLine(LOG_IDENT, $"Waiting for {processName} to start and become visible...");

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 60 && !cancellationToken.IsCancellationRequested)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length > 0)
                {
                    Process roblox = processes[0];
                    roblox.Refresh();

                    if (roblox.MainWindowHandle != IntPtr.Zero && IsWindowVisible(roblox.MainWindowHandle))
                    {
                        Voidstrap.App.Logger.WriteLine(LOG_IDENT, "Found visible Roblox window, triggering Alt+Enter");

                        SetForegroundWindow(roblox.MainWindowHandle);
                        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                        SendAltEnter();
                        Voidstrap.App.Logger.WriteLine(LOG_IDENT, "Alt+Enter triggered");
                        return;
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (!cancellationToken.IsCancellationRequested)
            Voidstrap.App.Logger.WriteLine(LOG_IDENT, "Timed out waiting for Roblox window");
    }

    private static void SendAltEnter()
    {
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_RETURN, 0, 0, 0);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
    }
}
