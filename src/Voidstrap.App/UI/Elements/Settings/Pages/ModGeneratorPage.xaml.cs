using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class ModGeneratorPage : UiPage
{
    public ModGeneratorPage()
    {
        DataContext = new ModsViewModel();
        InitializeComponent();
    }

    public ModGeneratorPage(ModsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
