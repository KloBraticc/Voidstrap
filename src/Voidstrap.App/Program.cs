using System;
using System.ComponentModel;

namespace Voidstrap;

public static class Program
{
	[STAThread]
	public static void Main()
	{
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
	}
}
