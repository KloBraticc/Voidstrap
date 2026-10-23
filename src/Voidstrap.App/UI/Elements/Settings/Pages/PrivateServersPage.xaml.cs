using System.Windows;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class PrivateServersPage : UiPage
    {
        private readonly PrivateServersViewModel _viewModel;

        public PrivateServersPage()
        {
            _viewModel = new PrivateServersViewModel();
            DataContext = _viewModel;
            InitializeComponent();
            Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = _viewModel.LoadAsync(false);
        }
    }
}
