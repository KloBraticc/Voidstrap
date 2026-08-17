using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Dialogs;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class ChannelListsDialog : WpfUiWindow{

	public ChannelListsDialog()
	{
		InitializeComponent();
		base.DataContext = new ChannelListsViewModel();
	}

	private void ChannelDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Invalid comparison between Unknown and I4
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		if ((int)e.Key != 46 || !((Enum)Keyboard.Modifiers).HasFlag((Enum)(object)(ModifierKeys)2))
		{
			return;
		}
		List<DeployInfoDisplay> list = ((System.Windows.Controls.DataGrid)sender).SelectedItems.Cast<DeployInfoDisplay>().ToList();
		if (list.Count > 0)
		{
			Clipboard.SetText(string.Join(Environment.NewLine, list.Select((DeployInfoDisplay i) => i.ChannelName)));
			e.Handled = true;
		}
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
