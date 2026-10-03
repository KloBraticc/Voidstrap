using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Controls;

public class RinColorPickerDialog : WpfUiWindow
{
	private readonly RinColorPicker _picker;

	private readonly Button _ok;

	public Color SelectedColor => _picker.SelectedColor;

	internal RinColorPicker Picker => _picker;

	public RinColorPickerDialog(Color? initial = null, bool alphaEnabled = false, string? title = null)
	{
		Title = title ?? "Pick a colour";
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
			Content = Voidstrap.Resources.Strings.Common_OK,
			MinWidth = 120,
			Margin = new Thickness(0, 0, 8, 0),
			IsDefault = true
		};
		ok.SetResourceReference(StyleProperty, typeof(Button));
		_ok = ok;
		ok.Click += OnOkClick;

		var cancel = new Button
		{
			Content = Voidstrap.Resources.Strings.Common_Cancel,
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

		Content = DialogChrome.Host(DialogChrome.TitleBar(Title), body);
		if (Voidstrap.Utility.Platform.IsLinux && Application.Current != null)
		{
			Window? active = Application.Current.Windows
				.OfType<Window>()
				.LastOrDefault(window => !ReferenceEquals(window, this) && window.IsVisible && window.IsActive);
			if (active != null)
				Owner = active;
		}
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

	protected override void OnClosed(EventArgs e)
	{
		_ok.Click -= OnOkClick;
		base.OnClosed(e);
	}
}
