using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.ContextMenu;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class RPCWindow : WpfUiWindow{

	public RPCWindow()
	{
		InitializeComponent();
		base.DataContext = RPCCustomizerViewModel.Shared;
	}
}
