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
            _viewModel = new AvatarViewModel(account);
            DataContext = _viewModel;
            InitializeComponent();
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                _viewModel = new AvatarViewModel(_account);
                DataContext = _viewModel;
            }
            _ = _viewModel.LoadAsync();
        }

        private void OnItemMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: AvatarItemRow row, ContextMenu: System.Windows.Controls.ContextMenu menu } || _viewModel == null)
                return;
            foreach (object item in menu.Items)
                if (item is System.Windows.Controls.MenuItem entry)
                    entry.IsEnabled = (entry.Tag as string) switch
                    {
                        "adjust" => _viewModel.CanAdjust(row),
                        "view" => !row.IsOutfit,
                        _ => !row.Busy,
                    };
        }

        private void OnItemMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem { DataContext: AvatarItemRow row } entry || _viewModel == null)
                return;
            string id = row.Id.ToString(CultureInfo.InvariantCulture);
            switch (entry.Tag as string)
            {
                case "wear":
                    _viewModel.ToggleItemCommand.Execute(row);
                    break;
                case "adjust":
                    _viewModel.Adjust(row);
                    break;
                case "view":
                    Utilities.OpenWebLink("https://www.roblox.com/catalog/" + id);
                    break;
                case "copy":
                    ClipboardService.SetText(id);
                    break;
            }
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            _viewModel?.Dispose();
            _viewModel = null;
        }
    }
}
