using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class AdvancedGeneralPage : UiPage
{
    public AdvancedGeneralPage(BehaviourViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
