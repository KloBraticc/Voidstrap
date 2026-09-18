using System;
using System.Windows;

namespace Voidstrap.UI.ViewModels.Settings;

public class PluginModel
{
	public string Name { get; set; } = null!;

	public string Author { get; set; } = null!;

	public string Description { get; set; } = null!;

	public object Instance { get; set; } = null!;

	public string PluginXaml { get; set; } = null!;

	public void Run()
	{
		try
		{
			if (Instance is Window window)
			{
				Window? obj = (Window?)Activator.CreateInstance(((object)window).GetType());
				obj?.Show();
				obj?.Activate();
			}
			else
			{
				Frontend.ShowMessageBox("Plugin instance is not a Window.");
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Plugin run failed: " + ex.Message);
		}
	}
}
