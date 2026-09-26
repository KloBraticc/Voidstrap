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
		if (Voidstrap.Utility.Platform.IsLinux && Content is System.Windows.Controls.Grid root)
		{
			root.RowDefinitions.Insert(2, new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
			System.Windows.Controls.Grid.SetRow(ErrorText, 2);
			System.Windows.Controls.Grid.SetRow(LoadingIndicator, 3);
			System.Windows.Controls.Grid.SetRow(DatacentersDataGrid, 3);
		}
		BetterBloxDataCenterConsoleViewModel dataContext = new BetterBloxDataCenterConsoleViewModel();
		base.DataContext = dataContext;
	}
}
