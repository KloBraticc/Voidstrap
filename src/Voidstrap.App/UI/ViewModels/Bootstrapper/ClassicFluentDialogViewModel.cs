using System;

namespace Voidstrap.UI.ViewModels.Bootstrapper;

public class ClassicFluentDialogViewModel : BootstrapperDialogViewModel
{
	public double FooterOpacity
	{
		get
		{
			if (Environment.OSVersion.Version.Build < 22000)
			{
				return 1.0;
			}
			return 0.4;
		}
	}

	public ClassicFluentDialogViewModel(IBootstrapperDialog dialog)
		: base(dialog)
	{
	}
}
