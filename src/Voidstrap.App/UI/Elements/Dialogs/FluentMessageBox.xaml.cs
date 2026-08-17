using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.Utility;
using Windows.Win32;
using Windows.Win32.Foundation;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class FluentMessageBox : WpfUiWindow{
	public MessageBoxResult Result;

	public FluentMessageBox(string message, MessageBoxImage image, MessageBoxButton buttons)
	{
		InitializeComponent();
		base.Title = "Voidstrap";
		RootTitleBar.Title = base.Title;
		string text = null;
		SystemSound systemSound = null;
		switch (image)
		{
		case MessageBoxImage.Hand:
			text = "Error";
			systemSound = Voidstrap.Utility.SafeSystemSounds.Get(MessageBoxImage.Hand);
			break;
		case MessageBoxImage.Question:
			text = "Question";
			systemSound = Voidstrap.Utility.SafeSystemSounds.Get(MessageBoxImage.Question);
			break;
		case MessageBoxImage.Exclamation:
			text = "Warning";
			systemSound = Voidstrap.Utility.SafeSystemSounds.Get(MessageBoxImage.Exclamation);
			break;
		case MessageBoxImage.Asterisk:
			text = "Information";
			systemSound = Voidstrap.Utility.SafeSystemSounds.Get(MessageBoxImage.Asterisk);
			break;
		}
		if (text == null)
		{
			IconImage.Visibility = Visibility.Collapsed;
		}
		else
		{
			IconImage.Source = Voidstrap.Utility.SafeImaging.FromUri(new Uri("pack://application:,,,/Resources/MessageBox/" + text + ".png"));
		}
		base.Title = "Voidstrap";
		MessageTextBlock.Text = message;
		MessageTextBlock.MarkdownText = message;
		ButtonOne.Visibility = Visibility.Collapsed;
		ButtonTwo.Visibility = Visibility.Collapsed;
		ButtonThree.Visibility = Visibility.Collapsed;
		switch (buttons)
		{
		case MessageBoxButton.YesNo:
			SetButton(ButtonOne, MessageBoxResult.Yes);
			SetButton(ButtonTwo, MessageBoxResult.No);
			break;
		case MessageBoxButton.YesNoCancel:
			SetButton(ButtonOne, MessageBoxResult.Yes);
			SetButton(ButtonTwo, MessageBoxResult.No);
			SetButton(ButtonThree, MessageBoxResult.Cancel);
			break;
		case MessageBoxButton.OKCancel:
			SetButton(ButtonOne, MessageBoxResult.OK);
			SetButton(ButtonTwo, MessageBoxResult.Cancel);
			break;
		default:
			SetButton(ButtonOne, MessageBoxResult.OK);
			break;
		}
		if (ButtonThree.Visibility == Visibility.Visible)
		{
			base.Width = 356.0;
		}
		else if (ButtonTwo.Visibility == Visibility.Visible)
		{
			base.Width = 245.0;
		}
		double num = Math.Ceiling(Rendering.GetTextWidth(MessageTextBlock));
		num += 40.0;
		if (image != MessageBoxImage.None)
		{
			num += 50.0;
		}
		if (num > base.MaxWidth)
		{
			base.Width = base.MaxWidth;
		}
		else if (num > base.Width)
		{
			base.Width = num;
		}
		Voidstrap.Utility.SafeSystemSounds.Play(systemSound);
		base.Loaded += OnLoaded;
		base.Closed += OnClosed;
	}

	private static string GetTextForResult(MessageBoxResult result)
	{
		return result switch
		{
			MessageBoxResult.OK => Strings.Common_OK, 
			MessageBoxResult.Cancel => Strings.Common_Cancel, 
			MessageBoxResult.Yes => Strings.Common_Yes, 
			MessageBoxResult.No => Strings.Common_No, 
			_ => result.ToString(), 
		};
	}

	public void SetButton(System.Windows.Controls.Button button, MessageBoxResult result)
	{
		button.Visibility = Visibility.Visible;
		button.Content = GetTextForResult(result);
		button.Tag = result;
		button.Click -= OnButtonClick;
		button.Click += OnButtonClick;
	}

	private void OnButtonClick(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button button && button.Tag is MessageBoxResult result)
		{
			Result = result;
			Close();
		}
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsWindows) { Windows.Win32.PInvoke.FlashWindow((HWND)new WindowInteropHelper(this).Handle, true); }
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		ButtonOne.Click -= OnButtonClick;
		ButtonTwo.Click -= OnButtonClick;
		ButtonThree.Click -= OnButtonClick;
		base.Loaded -= OnLoaded;
		base.Closed -= OnClosed;
	}
}
