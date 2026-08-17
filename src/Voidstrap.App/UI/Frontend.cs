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
				new ExceptionDialog(exception).ShowDialog();
			});
		}
	}

	public static void ShowConnectivityDialog(string title, string description, MessageBoxImage image, Exception exception)
	{
		if (!IsSilent)
		{
			UiDispatcher?.Invoke((Action)delegate
			{
				new ConnectivityDialog(title, description, image, exception).ShowDialog();
			});
		}
	}

	private static IBootstrapperDialog GetCustomBootstrapper()
	{
		Directory.CreateDirectory(Paths.CustomThemes);
		CustomDialog? customDialog = null;
		try
		{
			if (App.Settings.Prop.SelectedCustomTheme == null)
			{
				throw new CustomThemeException("CustomTheme.Errors.NoThemeSelected");
			}
			customDialog = new CustomDialog();
			customDialog.ApplyCustomTheme(App.Settings.Prop.SelectedCustomTheme);
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
			fluentMessageBox.ShowDialog();
			return fluentMessageBox.Result;
		});
	}

	public static void ShowBalloonTip(string title, string message, ToolTipIcon icon = ToolTipIcon.None, int timeout = 5)
	{
		NotifyIcon notifyIcon = new()
		{
			Icon = Voidstrap.Properties.Resources.IconVoidstrap,
			Text = "Voidstrap",
			Visible = true
		};
		notifyIcon.ShowBalloonTip(timeout, title, message, icon);
	}
}
