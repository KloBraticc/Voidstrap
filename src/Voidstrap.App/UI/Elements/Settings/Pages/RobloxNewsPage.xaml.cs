using System.Windows;
using System.Windows.Controls;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class RobloxNewsPage : Page
    {
        private readonly RobloxNewsViewModel _viewModel;

        public RobloxNewsPage()
        {
            _viewModel = new RobloxNewsViewModel();
            DataContext = _viewModel;
            InitializeComponent();
            Loaded += OnPageLoaded;
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (Window.GetWindow(this) is MainWindow window)
                window.NavigateBack();
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = _viewModel.LoadAsync(false);
        }
    }
}
