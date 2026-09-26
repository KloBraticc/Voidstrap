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
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			Grid.SetRow(SettingText, 1);
			Grid.SetRowSpan(SettingText, 1);
			SettingText.Margin = new Thickness(73, 0, 0, 12);
		}
	}

	public void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
