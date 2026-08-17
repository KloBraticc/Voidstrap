using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Dialogs;

public class UninstallerViewModel
{
	public string Text => string.Format(Strings.Uninstaller_Text, "https://voidstrapp.pages.dev/documentation#issues", Paths.Base);

	public bool KeepData { get; set; } = true;

	public ICommand ConfirmUninstallCommand => new RelayCommand(ConfirmUninstall);

	public event EventHandler? ConfirmUninstallRequest;

	private void ConfirmUninstall()
	{
		this.ConfirmUninstallRequest?.Invoke(this, new EventArgs());
	}
}
