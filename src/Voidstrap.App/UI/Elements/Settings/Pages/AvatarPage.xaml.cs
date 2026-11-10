using System.Windows;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public partial class AvatarPage : UiPage
    {
        private readonly SavedAccount _account;

        private AvatarViewModel? _viewModel;

        public AvatarPage(SavedAccount account)
        {
            _account = account;
            InitializeComponent();
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
                return;
            _viewModel = new AvatarViewModel(_account);
            DataContext = _viewModel;
            _ = _viewModel.LoadAsync();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _viewModel?.Dispose();
            _viewModel = null;
            DataContext = null;
        }
    }
}
