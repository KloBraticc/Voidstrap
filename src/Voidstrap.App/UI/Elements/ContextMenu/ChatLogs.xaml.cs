using System;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.ContextMenu;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ChatLogs : WpfUiWindow{
	private readonly ChatLogsViewModel _viewModel;

	public ChatLogs()
	{
		_viewModel = new ChatLogsViewModel();
		_viewModel.RequestCloseEvent += ViewModel_RequestClose;
		base.DataContext = _viewModel;
		InitializeComponent();
		base.Closed += ChatLogs_Closed;
	}

	private void ViewModel_RequestClose(object? sender, EventArgs e)
	{
		Close();
	}

	private void ChatLogs_Closed(object? sender, EventArgs e)
	{
		base.Closed -= ChatLogs_Closed;
		_viewModel.RequestCloseEvent -= ViewModel_RequestClose;
		_viewModel.Dispose();
		base.DataContext = null;
	}
}
