using System;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Exceptions;
using Voidstrap.Properties;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Bootstrapper;
using Voidstrap.UI.Elements.Dialogs;

namespace Voidstrap.UI;

internal static class Frontend
{
	public static MessageBoxResult ShowMessageBox(string message, MessageBoxImage icon = MessageBoxImage.None, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxResult defaultResult = MessageBoxResult.None)
	{
		App.Logger.WriteLine("Frontend::ShowMessageBox", message);
		if (IsSilent)
		{
			return defaultResult;
		}
		return ShowFluentMessageBox(message, icon, buttons);
	}

	public static void ShowPlayerErrorDialog(bool _ = false)
	{
	}

	private static bool IsSilent
	{
		get
		{
			LaunchSettings? settings = App.LaunchSettings;
			if (settings == null)
			{
				return false;
			}
			return settings.QuietFlag.Active || settings.WindowAuditFlag.Active;
		}
	}

	private static Dispatcher? UiDispatcher => System.Windows.Application.Current?.Dispatcher;

	public static void ShowExceptionDialog(Exception exception)
	{
		if (!IsSilent)
		{
			UiDispatcher?.Invoke((Action)delegate
			{
				new ExceptionDialog(exception).ShowOwnedDialog();
			});
		}
	}

	public static void ShowConnectivityDialog(string title, string description, MessageBoxImage image, Exception exception)
	{
		if (!IsSilent)
		{
			UiDispatcher?.Invoke((Action)delegate
			{
				new ConnectivityDialog(title, description, image, exception).ShowOwnedDialog();
			});
		}
	}

	private static IBootstrapperDialog GetCustomBootstrapper()
	{
		Directory.CreateDirectory(Paths.CustomThemes);
		CustomDialog? customDialog = null;
		try
		{
			string? selected = App.Settings.Prop.SelectedCustomTheme;

			if (selected == null)
			{
				throw new CustomThemeException("CustomTheme.Errors.NoThemeSelected");
			}

			if (!Voidstrap.Utility.CaseInsensitivePath.Exists(Path.Combine(Paths.CustomThemes, selected, "Theme.xml")))
			{
				App.Logger.WriteLine("Frontend::GetCustomBootstrapper", "The selected theme " + selected + " is gone, falling back to Fluent");
				App.Settings.Prop.SelectedCustomTheme = null;
				App.Settings.Save();
				return GetBootstrapperDialog(BootstrapperStyle.FluentDialog);
			}

			customDialog = new CustomDialog();
			customDialog.ApplyCustomTheme(selected);
			return customDialog;
		}
		catch (Exception ex)
		{
			try { customDialog?.Close(); } catch { }
			App.Logger.WriteException("Frontend::GetCustomBootstrapper", ex);
			if (!IsSilent)
			{
				ShowMessageBox(string.Format(Strings.CustomTheme_Errors_SetupFailed, ex.Message), MessageBoxImage.Hand);
			}
			return GetBootstrapperDialog(BootstrapperStyle.FluentDialog);
		}
	}

	public static IBootstrapperDialog GetBootstrapperDialog(BootstrapperStyle style)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			switch (style)
			{
				case BootstrapperStyle.VistaDialog:
					return new LinuxVistaDialog();
				case BootstrapperStyle.LegacyDialog2008:
					return new LinuxLegacyDialog2008();
				case BootstrapperStyle.LegacyDialog2011:
					return new LinuxLegacyDialog2011();
				case BootstrapperStyle.ProgressDialog:
					return new LinuxProgressDialog();
			}
		}
		else if (!Voidstrap.Utility.Platform.IsWindows && style is BootstrapperStyle.VistaDialog
			or BootstrapperStyle.LegacyDialog2008
			or BootstrapperStyle.LegacyDialog2011
			or BootstrapperStyle.ProgressDialog)
		{
			style = BootstrapperStyle.FluentDialog;
		}

		return style switch
		{
			BootstrapperStyle.VistaDialog => new VistaDialog(), 
			BootstrapperStyle.LegacyDialog2008 => new LegacyDialog2008(), 
			BootstrapperStyle.LegacyDialog2011 => new LegacyDialog2011(), 
			BootstrapperStyle.ProgressDialog => new ProgressDialog(), 
			BootstrapperStyle.ClassicFluentDialog => new ClassicFluentDialog(), 
			BootstrapperStyle.ByfronDialog => new ByfronDialog(), 
			BootstrapperStyle.FluentDialog => new FluentDialog(aero: false), 
			BootstrapperStyle.FluentAeroDialog => new FluentDialog(aero: true), 
			BootstrapperStyle.CustomDialog => GetCustomBootstrapper(), 
			_ => new FluentDialog(aero: false), 
		};
	}

	private static MessageBoxResult ShowFluentMessageBox(string message, MessageBoxImage icon, MessageBoxButton buttons)
	{
		Dispatcher? dispatcher = UiDispatcher;
		if (dispatcher == null)
		{
			return MessageBoxResult.None;
		}
		return dispatcher.Invoke<MessageBoxResult>((Func<MessageBoxResult>)delegate
		{
			FluentMessageBox fluentMessageBox = new(message, icon, buttons);
			fluentMessageBox.ShowOwnedDialog();
			return fluentMessageBox.Result;
		});
	}

	public static void ShowBalloonTip(string title, string message, ToolTipIcon icon = ToolTipIcon.None, int timeout = 5)
	{
		Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
		if (dispatcher != null && !dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(new Action(() => ShowBalloonTip(title, message, icon, timeout)));
			return;
		}
		NotifyIcon notifyIcon = new()
		{
			Icon = Voidstrap.Properties.Resources.IconVoidstrap,
			Text = "Voidstrap",
			Visible = true
		};
		notifyIcon.BalloonTipClosed += BalloonTip_Finished;
		notifyIcon.BalloonTipClicked += BalloonTip_Finished;
		notifyIcon.ShowBalloonTip(timeout, title, message, icon);
	}

	private static void BalloonTip_Finished(object? sender, EventArgs e)
	{
		if (sender is not NotifyIcon notifyIcon)
		{
			return;
		}
		notifyIcon.BalloonTipClosed -= BalloonTip_Finished;
		notifyIcon.BalloonTipClicked -= BalloonTip_Finished;
		notifyIcon.Visible = false;
		notifyIcon.Dispose();
	}
}
