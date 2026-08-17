using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.Integrations;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.ContextMenu;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ServerHistory : WpfUiWindow{
	private readonly ServerHistoryViewModel _viewModel;

	public ServerHistory(ActivityWatcher watcher)
	{
		_viewModel = new ServerHistoryViewModel(watcher);
		_viewModel.RequestCloseEvent += ViewModel_RequestClose;
		base.DataContext = _viewModel;
		InitializeComponent();
		base.Closed += ServerHistory_Closed;
	}

	private void ViewModel_RequestClose(object? sender, EventArgs e)
	{
		Close();
	}

	private void ServerHistory_Closed(object? sender, EventArgs e)
	{
		base.Closed -= ServerHistory_Closed;
		_viewModel.RequestCloseEvent -= ViewModel_RequestClose;
		_viewModel.Dispose();
		base.DataContext = null;
	}
}
