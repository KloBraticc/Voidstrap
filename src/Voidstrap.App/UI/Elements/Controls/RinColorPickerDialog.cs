using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Controls;

public class RinColorPickerDialog : WpfUiWindow
{
	private readonly RinColorPicker _picker;

	public Color SelectedColor => _picker.SelectedColor;

	public RinColorPickerDialog(Color? initial = null, bool alphaEnabled = false)
	{
		Title = "Pick a colour";
		SizeToContent = SizeToContent.WidthAndHeight;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		ExtendsContentIntoTitleBar = true;
		SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

		_picker = new RinColorPicker
		{
			AlphaEnabled = alphaEnabled,
			SelectedColor = initial ?? Colors.White
		};

		var ok = new Button
		{
			Content = "OK",
			MinWidth = 120,
			Margin = new Thickness(0, 0, 8, 0),
			IsDefault = true
		};
		ok.SetResourceReference(StyleProperty, typeof(Button));
		ok.Click += OnOkClick;

		var cancel = new Button
		{
			Content = "Cancel",
			MinWidth = 120,
			IsCancel = true
		};
		cancel.SetResourceReference(StyleProperty, typeof(Button));

		var buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 20, 0, 0)
		};
		buttons.Children.Add(ok);
		buttons.Children.Add(cancel);

		var body = new StackPanel { Margin = new Thickness(24, 12, 24, 20) };
		body.Children.Add(_picker);
		body.Children.Add(buttons);

		Content = DialogChrome.Host(DialogChrome.TitleBar("Pick a colour"), body);
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		if (Owner == null)
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
		base.OnSourceInitialized(e);
	}

	private void OnOkClick(object sender, RoutedEventArgs e)
	{
		DialogResult = true;
		Close();
	}
}
