using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Installer;

public class WelcomeViewModel : NotifyPropertyChangedViewModel
{
	public string MainText => string.Format(Strings.Installer_Welcome_MainText, "Thank you for downloading Voidstrap. This installation process will be quick and simple, and you will be able to configure any of Voidstrap's settings after installation.");

	public bool CanContinue { get; set; }
}
