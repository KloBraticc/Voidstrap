using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Extensions;

namespace Voidstrap.UI.ViewModels.Bootstrapper;

public class BootstrapperDialogViewModel : NotifyPropertyChangedViewModel
{
	private readonly IBootstrapperDialog _dialog;

	public ICommand CancelInstallCommand => new RelayCommand(CancelInstall);

	public string Title => App.Settings.Prop.BootstrapperTitle;

	public ImageSource Icon { get; set; } = Voidstrap.Extensions.IconEx.GetBootstrapperWindowIcon();

	public string Message { get; set; } = "Please wait..";

	public bool ProgressIndeterminate { get; set; } = true;

	public int ProgressMaximum { get; set; }

	public int ProgressValue { get; set; }

	public TaskbarItemProgressState TaskbarProgressState { get; set; } = TaskbarItemProgressState.Indeterminate;

	public double TaskbarProgressValue { get; set; }

	public bool CancelEnabled { get; set; }

	public Visibility CancelButtonVisibility
	{
		get
		{
			if (!CancelEnabled)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	[Obsolete("Do not use this! This is for the designer only.", true)]
	public BootstrapperDialogViewModel()
	{
		_dialog = null;
	}

	public BootstrapperDialogViewModel(IBootstrapperDialog dialog)
	{
		_dialog = dialog;
	}

	private void CancelInstall()
	{
		try { _dialog.CancelCallback?.Invoke(); } catch { }
		_dialog.Bootstrapper?.Cancel();
		_dialog.CloseBootstrapper();
	}
}
