using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class EmoteManagerPage : UiPage
{
    public EmoteManagerPage()
    {
        DataContext = new ModsViewModel();
        InitializeComponent();
    }

    public EmoteManagerPage(ModsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
