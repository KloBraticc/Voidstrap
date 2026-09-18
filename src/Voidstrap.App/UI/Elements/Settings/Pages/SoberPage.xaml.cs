using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public sealed class RuntimeSettingsViewModel
{
	public SoberViewModel Sober { get; } = new();

	public VinegarViewModel Vinegar { get; } = new();
}

public partial class SoberPage : UiPage
{
	public SoberPage()
	{
		DataContext = new RuntimeSettingsViewModel();
		InitializeComponent();
	}
}
