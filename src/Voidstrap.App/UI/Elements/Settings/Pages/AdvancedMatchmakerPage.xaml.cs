using System.Windows;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class AdvancedMatchmakerPage : UiPage
{
    public AdvancedMatchmakerPage()
        : this(new BehaviourViewModel()) { }

    public AdvancedMatchmakerPage(BehaviourViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void ResetDatacenters_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as BehaviourViewModel)?.ResetDatacenters();
    }
}
