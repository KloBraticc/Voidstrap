using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Enums;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class CursorPreviewDialog : WpfUiWindow{

	public Voidstrap.Enums.CursorType? SelectedCursor { get; private set; }

	public CursorPreviewDialog()
	{
		InitializeComponent();
		LoadCursorPreviews();
		base.Closed += OnClosed;
	}

	private void LoadCursorPreviews()
	{
		Voidstrap.Enums.CursorType[] array = new Voidstrap.Enums.CursorType[9]
		{
			Voidstrap.Enums.CursorType.Default,
			Voidstrap.Enums.CursorType.FPSCursor,
			Voidstrap.Enums.CursorType.CleanCursor,
			Voidstrap.Enums.CursorType.DotCursor,
			Voidstrap.Enums.CursorType.StoofsCursor,
			Voidstrap.Enums.CursorType.From2006,
			Voidstrap.Enums.CursorType.From2013,
			Voidstrap.Enums.CursorType.WhiteDotCursor,
			Voidstrap.Enums.CursorType.VerySmallWhiteDot
		};
		foreach (Voidstrap.Enums.CursorType cursor in array)
		{
			FrameworkElement element = CreateCursorPreviewItem(cursor);
			CursorStackPanel.Children.Add(element);
		}
	}

	private FrameworkElement CreateCursorPreviewItem(Voidstrap.Enums.CursorType cursor)
	{
		Border border = new Border
		{
			BorderBrush = new SolidColorBrush(Colors.Gray),
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(5.0),
			Padding = new Thickness(10.0),
			Background = new SolidColorBrush(Colors.Transparent),
			Cursor = Cursors.Hand
		};
		border.Tag = cursor;
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		Image image = new Image
		{
			Width = 32.0,
			Height = 32.0,
			Margin = new Thickness(0.0, 0.0, 10.0, 0.0)
		};
		try
		{
			string cursorImagePath = GetCursorImagePath(cursor);
			if (!string.IsNullOrEmpty(cursorImagePath))
			{
				Uri uriSource = new Uri("pack://application:,,,/Resources/Mods/" + cursorImagePath);
				image.Source = Voidstrap.Utility.SafeImaging.FromUri(uriSource);
			}
		}
		catch
		{
			image.Source = null;
		}
		TextBlock element = new TextBlock
		{
			Text = GetCursorDisplayName(cursor),
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 14.0
		};
		stackPanel.Children.Add(image);
		stackPanel.Children.Add(element);
		border.Child = stackPanel;
		border.MouseLeftButtonUp += OnCursorClick;
		border.MouseEnter += OnCursorMouseEnter;
		border.MouseLeave += OnCursorMouseLeave;
		return border;
	}

	private void OnCursorClick(object sender, MouseButtonEventArgs e)
	{
		if (sender is not Border { Tag: Voidstrap.Enums.CursorType cursor })
			return;
		SelectedCursor = cursor;
		base.DialogResult = true;
		Close();
	}

	private static void OnCursorMouseEnter(object sender, MouseEventArgs e)
	{
		if (sender is Border border)
			border.Background = new SolidColorBrush(Color.FromArgb(50, 100, 149, 237));
	}

	private static void OnCursorMouseLeave(object sender, MouseEventArgs e)
	{
		if (sender is Border border)
			border.Background = new SolidColorBrush(Colors.Transparent);
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		foreach (Border border in CursorStackPanel.Children.OfType<Border>())
		{
			border.MouseLeftButtonUp -= OnCursorClick;
			border.MouseEnter -= OnCursorMouseEnter;
			border.MouseLeave -= OnCursorMouseLeave;
		}
		base.Closed -= OnClosed;
	}

	private string GetCursorImagePath(Voidstrap.Enums.CursorType cursor)
	{
		return cursor switch
		{
			Voidstrap.Enums.CursorType.FPSCursor => "Cursor/FPSCursor/ArrowCursor.png", 
			Voidstrap.Enums.CursorType.CleanCursor => "Cursor/CleanCursor/ArrowCursor.png", 
			Voidstrap.Enums.CursorType.DotCursor => "Cursor/DotCursor/ArrowCursor.png", 
			Voidstrap.Enums.CursorType.StoofsCursor => "Cursor/StoofsCursor/ArrowCursor.png", 
			Voidstrap.Enums.CursorType.From2006 => "Cursor/From2006/ArrowCursor.png", 
			Voidstrap.Enums.CursorType.From2013 => "Cursor/From2013/ArrowCursor.png", 
			Voidstrap.Enums.CursorType.WhiteDotCursor => "Cursor/WhiteDotCursor/ArrowCursor.png", 
			Voidstrap.Enums.CursorType.VerySmallWhiteDot => "Cursor/VerySmallWhiteDot/ArrowCursor.png", 
			_ => string.Empty, 
		};
	}

	private string GetCursorDisplayName(Voidstrap.Enums.CursorType cursor)
	{
		return cursor switch
		{
			Voidstrap.Enums.CursorType.Default => "Default", 
			Voidstrap.Enums.CursorType.FPSCursor => "FPS Cursor (V1)", 
			Voidstrap.Enums.CursorType.CleanCursor => "Clean Cursor", 
			Voidstrap.Enums.CursorType.DotCursor => "Dot Cursor", 
			Voidstrap.Enums.CursorType.StoofsCursor => "Stoofs Cursor", 
			Voidstrap.Enums.CursorType.From2006 => "2006 Legacy Cursor", 
			Voidstrap.Enums.CursorType.From2013 => "2013 Legacy Cursor", 
			Voidstrap.Enums.CursorType.WhiteDotCursor => "White Dot Cursor", 
			Voidstrap.Enums.CursorType.VerySmallWhiteDot => "Very Small White Dot", 
			_ => cursor.ToString(), 
		};
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}
}
