using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Voidstrap.Enums;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Installer;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class LaunchMenuDialog : WpfUiWindow{
	private readonly LaunchMenuViewModel _viewModel;

	public NextAction CloseAction;

	public LaunchMenuDialog()
	{
		_viewModel = new LaunchMenuViewModel();
		_viewModel.CloseWindowRequest += OnCloseWindowRequest;
		base.DataContext = _viewModel;
		InitializeComponent();
		base.Closed += OnClosed;
	}

	private void OnCloseWindowRequest(object? sender, NextAction closeAction)
	{
		CloseAction = closeAction;
		Close();
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		_viewModel.CloseWindowRequest -= OnCloseWindowRequest;
		base.Closed -= OnClosed;
	}

	private void Hyperlink_Click(object sender, RoutedEventArgs e)
	{
	}

	private void Anchor_Click(object sender, RoutedEventArgs e)
	{
	}

	private void CardAction_Click(object sender, RoutedEventArgs e)
	{
	}

	private void Hyperlink_Click_1(object sender, RoutedEventArgs e)
	{
	}

	private void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
	{
	}
}
