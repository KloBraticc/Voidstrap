using Wpf.Ui.Controls;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class OverlaysPage : UiPage
{
	public OverlaysPage()
	{
		InitializeComponent();
		Loaded += (s, e) => (DataContext as ModsViewModel)?.PopulateKeystrokeThemes();
	}
}
