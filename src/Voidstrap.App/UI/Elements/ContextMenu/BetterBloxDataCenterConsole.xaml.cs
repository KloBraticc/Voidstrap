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

public partial class BetterBloxDataCenterConsole : WpfUiWindow{

	public BetterBloxDataCenterConsole()
	{
		InitializeComponent();
		BetterBloxDataCenterConsoleViewModel dataContext = new BetterBloxDataCenterConsoleViewModel();
		base.DataContext = dataContext;
	}
}
