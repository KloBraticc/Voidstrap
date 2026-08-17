using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.ContextMenu;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class GamePassConsole : WpfUiWindow
{
	private readonly GamePassConsoleViewModel _viewModel;

	public GamePassConsole(long userId)
	{
		InitializeComponent();
		_viewModel = new GamePassConsoleViewModel();
		DataContext = _viewModel;
		_viewModel.LoadGamePassesCommand.Execute(userId);
	}

	protected override void OnClosed(EventArgs e)
	{
		_viewModel.Dispose();
		base.OnClosed(e);
	}
}
