using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Markup;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.About.Pages;

public partial class TranslatorsPage : UiPage{

	public TranslatorsPage()
	{
		InitializeComponent();
		ApplyReadableHeadings();
	}

	private void ApplyReadableHeadings()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		ThaiHeading.Text = "Thai";
		KoreanHeading.Text = "Korean";
		ChineseSimplifiedHeading.Text = "Chinese (Simplified)";
		ChineseTraditionalHeading.Text = "Chinese (Traditional)";
		JapaneseHeading.Text = "Japanese";
	}
}
