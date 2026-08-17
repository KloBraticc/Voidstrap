using System;
using Voidstrap.UI.ViewModels;

namespace Voidstrap.UI.Elements.ContextMenu
{
    public partial class AccountManagerWindow
    {
        private readonly AccountSwitcherViewModel _viewModel;

        public AccountManagerWindow()
        {
            InitializeComponent();
            _viewModel = new AccountSwitcherViewModel();
            DataContext = _viewModel;
            Closed += AccountManagerWindow_Closed;
        }

        private void AccountManagerWindow_Closed(object? sender, EventArgs e)
        {
            Closed -= AccountManagerWindow_Closed;
            try
            {
                _viewModel.Dispose();
            }
            catch
            {
            }
        }
    }
}
