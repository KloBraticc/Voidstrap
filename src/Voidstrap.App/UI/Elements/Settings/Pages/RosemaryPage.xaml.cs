using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class RosemaryPage : UiPage
{
	public RosemaryPage()
	{
		DataContext = new RosemaryViewModel();
		InitializeComponent();
	}
}
