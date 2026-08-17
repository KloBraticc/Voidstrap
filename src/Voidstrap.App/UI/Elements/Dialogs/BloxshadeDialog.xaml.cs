using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Voidstrap.Enums;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class BloxshadeDialog : WpfUiWindow{
	public NextAction CloseAction;

	public BloxshadeDialog()
	{
		InitializeComponent();
	}

	public void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
