using System;
using System.ComponentModel;
using System.Diagnostics;

namespace Voidstrap;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        string[] cmdLineArgs = Environment.GetCommandLineArgs();
        if (Voidstrap.Integrations.Mcp.McpStdioServer.IsRequested(cmdLineArgs))
        {
            Environment.Exit(Voidstrap.Integrations.Mcp.McpStdioServer.Run());
            return;
        }
        if (Cli.TryRunMcpShortcut(cmdLineArgs, out int mcpExitCode))
        {
            Environment.Exit(mcpExitCode);
            return;
        }

        long startupStart = Stopwatch.GetTimestamp();
        App.StartJitProfile(cmdLineArgs);
        App.NoteMainEntered();
        App app;
        try
        {
            app = new App();
            app.InitializeComponent();
            App.NoteAppBuilt();
        }
        catch (Exception ex)
        {
            try
            {
                App.Logger.WriteLine("Program::Main", "App construction threw: " + ex.GetType().Name + " - " + ex.Message);
                App.Logger.WriteException("Program::Main", ex);
                App.Logger.Flush();
            }
            catch
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Voidstrap.startup.crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + " App construction: "
                        + ex.GetType().Name
                        + " - "
                        + ex.Message
                        + Environment.NewLine
                        + ex.StackTrace
                        + Environment.NewLine
                );
            }
            throw;
        }
        int exitCode = 0;
        try
        {
            exitCode = app.Run();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1400 && app.Dispatcher.HasShutdownStarted)
        {
            App.Logger.WriteLine("Program::Main", "Ignored a stale window handle during shutdown");
            App.Logger.Flush();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Program::Main", "App.Run threw: " + ex.GetType().Name + " - " + ex.Message);
            App.Logger.Flush();
            throw;
        }
        App.Logger.WriteLine(
            "Program::Main",
            "App shut down, total elapsed " + (int)Stopwatch.GetElapsedTime(startupStart).TotalMilliseconds + " ms"
        );
        App.Logger.Flush();
        Environment.Exit(exitCode);
    }
}
