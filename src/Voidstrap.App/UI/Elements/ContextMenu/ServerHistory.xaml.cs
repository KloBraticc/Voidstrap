using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Linq;
using System.Windows.Markup;
using Voidstrap.Integrations;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.ContextMenu;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class ServerHistory : WpfUiWindow{
	private readonly ServerHistoryViewModel _viewModel;

	private readonly UIElement? _root;

	public ServerHistory(ActivityWatcher watcher)
	{
		_viewModel = new ServerHistoryViewModel(watcher);
		_viewModel.RequestCloseEvent += ViewModel_RequestClose;
		base.DataContext = _viewModel;
		InitializeComponent();
		_root = Content as UIElement;
		_viewModel.IsShown = IsHistoryShown;
		if (_root != null)
			_root.IsVisibleChanged += Root_IsVisibleChanged;
		base.Closed += ServerHistory_Closed;
		Window? owner = Application.Current.Windows.Cast<Window>().Where(window => window != this && window.IsVisible && window.ShowInTaskbar).OrderByDescending(window => window.IsActive).FirstOrDefault();
		if (owner != null)
		{
			Owner = owner;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;
		}
	}

	private bool IsHistoryShown()
	{
		return _root?.IsVisible == true && (!IsVisible || WindowState != System.Windows.WindowState.Minimized);
	}

	private void Root_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
	{
		if (e.NewValue is true)
			_viewModel.OnShown();
	}

	private void ViewModel_RequestClose(object? sender, EventArgs e)
	{
		Close();
	}

	private void ServerHistory_Closed(object? sender, EventArgs e)
	{
		base.Closed -= ServerHistory_Closed;
		if (_root != null)
			_root.IsVisibleChanged -= Root_IsVisibleChanged;
		_viewModel.IsShown = null;
		_viewModel.RequestCloseEvent -= ViewModel_RequestClose;
		_viewModel.Dispose();
		base.DataContext = null;
	}
}
