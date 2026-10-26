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
		App app = new App();
		app.InitializeComponent();
		try
		{
			app.Run();
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
		App.Logger.WriteLine("Program::Main", "App shut down, total elapsed " + (int)Stopwatch.GetElapsedTime(startupStart).TotalMilliseconds + " ms");
		App.Logger.Flush();
	}
}
