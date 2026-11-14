using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class NewsPage : UiPage
{
    private readonly NewsViewModel _viewModel = new();

    public NewsPage()
    {
        DataContext = _viewModel;
        InitializeComponent();
    }
}
