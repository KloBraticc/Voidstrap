using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using Voidstrap.UI.Elements.Overlay;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ExtensionPage : UiPage{
	private const double CardGap = 12.0;

	private const double MinCardWidth = 300.0;

	private ExtensionViewModel _vm;

	public ExtensionPage()
	{
		_vm = new ExtensionViewModel();
		base.DataContext = _vm;
		InitializeComponent();
		Loaded += ExtensionPage_Loaded;
		Unloaded += ExtensionPage_Unloaded;
	}

	private void ExtensionPage_Loaded(object sender, RoutedEventArgs e)
	{
		_vm.OnProgressChanged -= Vm_OnProgressChanged;
		_vm.OnProgressChanged += Vm_OnProgressChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged -= RiShadePanel_OpenChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged += RiShadePanel_OpenChanged;
		_vm.RiShadeOpenLabel = Voidstrap.Integrations.RiShade.RiShadePanel.IsOpen ? "Close" : "Open";
		Voidstrap.Utility.DynamicRenderSystem.Prefetch(new[]
		{
			"https://avatars.githubusercontent.com/u/186699266",
			"https://raw.githubusercontent.com/KloBraticc/RiShade/main/Images/RiShde.png",
			"https://raw.githubusercontent.com/MaximumADHD/Roblox-API-Dump-Tool/master/Resources/AppLogo.png",
			"https://github.com/rojo-rbx.png"
		}, 64);
		UpdateCardWidths();
	}

	private void ExtensionPage_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateCardWidths();
	}

	private void UpdateCardWidths()
	{
		if (ExtensionCardsPanel == null || ExtensionCardsPanel.ActualWidth <= 0.0)
		{
			return;
		}
		int columns = Math.Max(1, (int)((ExtensionCardsPanel.ActualWidth + CardGap) / (MinCardWidth + CardGap)));
		if (columns > 4)
		{
			columns = 4;
		}
		if (ExtensionCardsPanel.Columns != columns)
		{
			ExtensionCardsPanel.Columns = columns;
		}
	}

	private void ExtensionPage_Unloaded(object sender, RoutedEventArgs e)
	{
		_vm.OnProgressChanged -= Vm_OnProgressChanged;
		Voidstrap.Integrations.RiShade.RiShadePanel.OpenChanged -= RiShadePanel_OpenChanged;
	}

	private void RiShadePanel_OpenChanged(bool open)
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((Action)delegate
		{
			_vm.RiShadeOpenLabel = open ? "Close" : "Open";
		});
	}

	private void Vm_OnProgressChanged(string title, double fraction, bool show)
	{
		((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
		{
			ProgressTitle.Text = title;
			if (fraction < 0.0)
			{
				ProgressBar.IsIndeterminate = true;
				ProgressPercent.Text = "";
			}
			else
			{
				ProgressBar.IsIndeterminate = false;
				ProgressBar.Value = fraction * 100.0;
				ProgressPercent.Text = $"{fraction * 100.0:0}%";
			}
			ProgressOverlay.Visibility = ((!show) ? Visibility.Collapsed : Visibility.Visible);
		});
	}

	private void CancelDownload_Click(object sender, RoutedEventArgs e)
	{
		_vm?.CancelDownload();
	}

	private void UpdateCommunityContent_Click(object sender, RoutedEventArgs e)
	{
		_vm?.UpdateCommunityContent();
	}

	private void OpenFleasion_Click(object sender, RoutedEventArgs e)
	{
		string text = Paths.Fleasion;
		string text2 = Path.Combine(text, "Fleasion.exe");
		if (Directory.Exists(text) && File.Exists(text2))
		{
			try
			{
				if (Process.GetProcessesByName("Fleasion").Length == 0)
				{
					Process.Start(text2);
				}
				return;
			}
			catch (Exception ex)
			{
				Frontend.ShowMessageBox("Failed to open Fleasion: " + ex.Message);
				return;
			}
		}
		Frontend.ShowMessageBox("Fleasion Extension is not Enabled/Installed");
	}

	private void OpenApiDumpTool_Click(object sender, RoutedEventArgs e)
	{
		string dir = Paths.ApiDumpTool;
		string exe = Path.Combine(dir, "RobloxAPIDumpTool.exe");
		if (Directory.Exists(dir) && File.Exists(exe))
		{
			try
			{
				if (Process.GetProcessesByName("RobloxAPIDumpTool").Length == 0)
				{
					Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
				}
				return;
			}
			catch (Exception ex)
			{
				Frontend.ShowMessageBox("Failed to open the Roblox API Dump Tool: " + ex.Message);
				return;
			}
		}
		Frontend.ShowMessageBox("The Roblox API Dump Tool is not enabled or installed yet, turn its toggle on first.");
	}

	private void OpenRiShade_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Voidstrap.Integrations.RiShade.RiShadePanel.Toggle(fromUi: true);
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to open the RiShade panel: " + ex.Message);
		}
	}

	private void BrowseRojoFolder_Click(object sender, RoutedEventArgs e) => _vm?.PickRojoFolder();

	private async void RojoInit_Click(object sender, RoutedEventArgs e)
	{
		if (_vm != null)
			await _vm.RojoInitAsync();
	}

	private void RojoServe_Click(object sender, RoutedEventArgs e) => _vm?.RojoToggleServe();

	private async void RojoBuild_Click(object sender, RoutedEventArgs e)
	{
		if (_vm != null)
			await _vm.RojoBuildAsync();
	}

	private async void RojoInstallPlugin_Click(object sender, RoutedEventArgs e)
	{
		if (_vm != null)
			await _vm.RojoInstallPluginAsync();
	}

	private void OpenRojo_Click(object sender, RoutedEventArgs e) => _vm?.OpenRojoFolder();

}
