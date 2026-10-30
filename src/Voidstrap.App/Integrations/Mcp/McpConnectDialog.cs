using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;

namespace Voidstrap.Integrations.Mcp;

public sealed class McpConnectDialog : WpfUiWindow
{
	private const string LogIdent = "McpConnectDialog";

	private const string DesktopIconPath =
		"F1 M 4.709 15.955 l 4.72 -2.647 .08 -.23 -.08 -.128 H 9.2 l -.79 -.048 -2.698 -.073 -2.339 -.097 -2.266 -.122 -.571 -.121 L 0 11.784 l .055 -.352 .48 -.321 .686 .06 1.52 .103 2.278 .158 1.652 .097 2.449 .255 h .389 l .055 -.157 -.134 -.098 -.103 -.097 -2.358 -1.596 -2.552 -1.688 -1.336 -.972 -.724 -.491 -.364 -.462 -.158 -1.008 .656 -.722 .881 .06 .225 .061 .893 .686 1.908 1.476 2.491 1.833 .365 .304 .145 -.103 .019 -.073 -.164 -.274 -1.355 -2.446 -1.446 -2.49 -.644 -1.032 -.17 -.619 a 2.97 2.97 0 0 1 -.104 -.729 L 6.283 .134 6.696 0 l .996 .134 .42 .364 .62 1.414 1.002 2.229 1.555 3.03 .456 .898 .243 .832 .091 .255 h .158 V 9.01 l .128 -1.706 .237 -2.095 .23 -2.695 .08 -.76 .376 -.91 .747 -.492 .584 .28 .48 .685 -.067 .444 -.286 1.851 -.559 2.903 -.364 1.942 h .212 l .243 -.242 .985 -1.306 1.652 -2.064 .73 -.82 .85 -.904 .547 -.431 h 1.033 l .76 1.129 -.34 1.166 -1.064 1.347 -.881 1.142 -1.264 1.7 -.79 1.36 .073 .11 .188 -.02 2.856 -.606 1.543 -.28 1.841 -.315 .833 .388 .091 .395 -.328 .807 -1.969 .486 -2.309 .462 -3.439 .813 -.042 .03 .049 .061 1.549 .146 .662 .036 h 1.622 l 3.02 .225 .79 .522 .474 .638 -.079 .485 -1.215 .62 -1.64 -.389 -3.829 -.91 -1.312 -.329 h -.182 v .11 l 1.093 1.068 2.006 1.81 2.509 2.33 .127 .578 -.322 .455 -.34 -.049 -2.205 -1.657 -.851 -.747 -1.926 -1.62 h -.128 v .17 l .444 .649 2.345 3.521 .122 1.08 -.17 .353 -.608 .213 -.668 -.122 -1.374 -1.925 -1.415 -2.167 -1.143 -1.943 -.14 .08 -.674 7.254 -.316 .37 -.729 .28 -.607 -.461 -.322 -.747 .322 -1.476 .389 -1.924 .315 -1.53 .286 -1.9 .17 -.632 -.012 -.042 -.14 .018 -1.434 1.967 -2.18 2.945 -1.726 1.845 -.414 .164 -.717 -.37 .067 -.662 .401 -.589 2.388 -3.036 1.44 -1.882 .93 -1.086 -.006 -.158 h -.055 L 4.132 18.56 l -1.13 .146 -.487 -.456 .061 -.746 .231 -.243 1.908 -1.312 -.006 .006 z";

	private const string CliTilePath =
		"F1 M 19.503 0 H 4.496 A 4.496 4.496 0 0 0 0 4.496 v 15.007 A 4.496 4.496 0 0 0 4.496 24 h 15.007 A 4.496 4.496 0 0 0 24 19.503 V 4.496 A 4.496 4.496 0 0 0 19.503 0 z";

	private const string CliGlyphPath =
		"F1 M 9.064 3.344 a 4.578 4.578 0 0 1 2.285 -.312 c 1 .115 1.891 .54 2.673 1.275 .01 .01 .024 .017 .037 .021 a .09 .09 0 0 0 .043 0 4.55 4.55 0 0 1 3.046 .275 l .047 .022 .116 .057 a 4.581 4.581 0 0 1 2.188 2.399 c .209 .51 .313 1.041 .315 1.595 a 4.24 4.24 0 0 1 -.134 1.223 .123 .123 0 0 0 .03 .115 c .594 .607 .988 1.33 1.183 2.17 .289 1.425 -.007 2.71 -.887 3.854 l -.136 .166 a 4.548 4.548 0 0 1 -2.201 1.388 .123 .123 0 0 0 -.081 .076 c -.191 .551 -.383 1.023 -.74 1.494 -.9 1.187 -2.222 1.846 -3.711 1.838 -1.187 -.006 -2.239 -.44 -3.157 -1.302 a .107 .107 0 0 0 -.105 -.024 c -.388 .125 -.78 .143 -1.204 .138 a 4.441 4.441 0 0 1 -1.945 -.466 4.544 4.544 0 0 1 -1.61 -1.335 c -.152 -.202 -.303 -.392 -.414 -.617 a 5.81 5.81 0 0 1 -.37 -.961 4.582 4.582 0 0 1 -.014 -2.298 .124 .124 0 0 0 .006 -.056 .085 .085 0 0 0 -.027 -.048 4.467 4.467 0 0 1 -1.034 -1.651 3.896 3.896 0 0 1 -.251 -1.192 5.189 5.189 0 0 1 .141 -1.6 c .337 -1.112 .982 -1.985 1.933 -2.618 .212 -.141 .413 -.251 .601 -.33 .215 -.089 .43 -.164 .646 -.227 a .098 .098 0 0 0 .065 -.066 4.51 4.51 0 0 1 .829 -1.615 4.535 4.535 0 0 1 1.837 -1.388 z m 3.482 10.565 a .637 .637 0 0 0 0 1.272 h 3.636 a .637 .637 0 1 0 0 -1.272 h -3.636 z M 8.462 9.23 a .637 .637 0 0 0 -1.106 .631 l 1.272 2.224 -1.266 2.136 a .636 .636 0 1 0 1.095 .649 l 1.454 -2.455 a .636 .636 0 0 0 .005 -.64 L 8.462 9.23 z";

	private const string OpenCodePath = "F0 M 16 6 H 8 v 12 h 8 V 6 z m 4 16 H 4 V 2 h 16 v 20 z";

	private static McpConnectDialog? _open;

	private readonly ListBox _list;

	private readonly System.Windows.Controls.TextBlock _status;

	private readonly Wpf.Ui.Controls.Button _connect;

	private readonly List<ClientRow> _rows = [];

	private bool _busy;

	private sealed class ClientRow
	{
		public required string Name { get; init; }

		public Func<bool>? IsConnected { get; init; }

		public Func<bool>? Connect { get; init; }

		public System.Windows.Controls.TextBlock Detail { get; } = new() { FontSize = 12 };

		public ListBoxItem Item { get; set; } = null!;
	}

	public static void ShowSingle()
	{
		if (_open != null)
		{
			if (_open.WindowState == System.Windows.WindowState.Minimized)
				_open.WindowState = System.Windows.WindowState.Normal;
			_open.Activate();
			return;
		}

		_open = new McpConnectDialog();
		try
		{
			_open.ShowOwnedDialog();
		}
		finally
		{
			_open = null;
		}
	}

	private McpConnectDialog()
	{
		Title = Strings.Ui_Mcp_Connect;
		Width = 460;
		SizeToContent = SizeToContent.Height;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		ShowInTaskbar = false;
		ExtendsContentIntoTitleBar = true;
		SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

		_rows.Add(
			new ClientRow
			{
				Name = McpConnectInfo.DesktopClientLabel,
				IsConnected = McpConnectInfo.IsDesktopClientConnected,
				Connect = McpConnectInfo.ConnectDesktopClient,
			}
		);
		_rows.Add(
			new ClientRow
			{
				Name = McpConnectInfo.CliClientLabel,
				IsConnected = McpConnectInfo.IsCliClientConnected,
				Connect = McpConnectInfo.ConnectCliClient,
			}
		);
		_rows.Add(
			new ClientRow
			{
				Name = McpConnectInfo.OpenCodeClientLabel,
				IsConnected = McpConnectInfo.IsOpenCodeClientConnected,
				Connect = McpConnectInfo.ConnectOpenCodeClient,
			}
		);
		_rows.Add(new ClientRow { Name = Strings.Ui_Mcp_OtherApp });

		_list = new ListBox
		{
			BorderThickness = new Thickness(0),
			Background = Brushes.Transparent,
			SelectedIndex = 0,
		};
		for (int i = 0; i < _rows.Count; i++)
		{
			ClientRow row = _rows[i];
			row.Item = new ListBoxItem { Content = BuildRow(row, BuildIcon(i)), Padding = new Thickness(6, 8, 6, 8) };
			_list.Items.Add(row.Item);
		}
		_list.SelectedIndex = 0;
		_list.MouseDoubleClick += List_MouseDoubleClick;

		System.Windows.Controls.TextBlock header = new()
		{
			Text = Strings.Ui_Mcp_ChooseClient,
			Margin = new Thickness(0, 0, 0, 12),
			TextWrapping = TextWrapping.Wrap,
		};

		_status = new System.Windows.Controls.TextBlock
		{
			Margin = new Thickness(2, 10, 2, 0),
			TextWrapping = TextWrapping.Wrap,
			FontSize = 12,
			Visibility = Visibility.Collapsed,
		};
		_status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

		_connect = new Wpf.Ui.Controls.Button
		{
			Content = Strings.Ui_Mcp_ConnectButton,
			MinWidth = 110,
			Margin = new Thickness(0, 0, 8, 0),
			IsDefault = true,
		};
		_connect.Click += Connect_Click;
		Wpf.Ui.Controls.Button close = new()
		{
			Content = Strings.Common_Close,
			MinWidth = 110,
			IsCancel = true,
		};

		StackPanel buttons = new()
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 18, 0, 0),
		};
		buttons.Children.Add(_connect);
		buttons.Children.Add(close);

		StackPanel body = new() { Margin = new Thickness(24, 12, 24, 20) };
		body.Children.Add(header);
		body.Children.Add(_list);
		body.Children.Add(_status);
		body.Children.Add(buttons);

		Content = DialogChrome.Host(DialogChrome.TitleBar(Strings.Ui_Mcp_Connect), body);
		Loaded += Dialog_Loaded;
		Closed += Dialog_Closed;
	}

	private static FrameworkElement BuildRow(ClientRow row, FrameworkElement icon)
	{
		Grid grid = new();
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		icon.Width = 28;
		icon.Height = 28;
		icon.Margin = new Thickness(0, 0, 14, 0);
		icon.VerticalAlignment = VerticalAlignment.Center;
		grid.Children.Add(icon);

		StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
		text.Children.Add(new System.Windows.Controls.TextBlock { Text = row.Name, FontSize = 14 });
		row.Detail.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
		row.Detail.Text = row.IsConnected == null ? Strings.Ui_Mcp_OtherAppDetail : "";
		row.Detail.TextWrapping = TextWrapping.Wrap;
		text.Children.Add(row.Detail);
		Grid.SetColumn(text, 1);
		grid.Children.Add(text);
		return grid;
	}

	private static FrameworkElement BuildIcon(int index)
	{
		switch (index)
		{
			case 0:
				return Canvas24(Shape(DesktopIconPath, new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57))));
			case 1:
			{
				Canvas layered = new() { Width = 24, Height = 24 };
				layered.Children.Add(Shape(CliTilePath, Brushes.White));
				LinearGradientBrush glyph = new() { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
				glyph.GradientStops.Add(new GradientStop(Color.FromRgb(0xB1, 0xA7, 0xFF), 0));
				glyph.GradientStops.Add(new GradientStop(Color.FromRgb(0x7A, 0x9D, 0xFF), 0.5));
				glyph.GradientStops.Add(new GradientStop(Color.FromRgb(0x39, 0x41, 0xFF), 1));
				layered.Children.Add(Shape(CliGlyphPath, glyph));
				return new Viewbox { Child = layered, Stretch = Stretch.Uniform };
			}
			case 2:
			{
				System.Windows.Shapes.Path path = Shape(OpenCodePath, null);
				path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
				return Canvas24(path);
			}
			default:
				return new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Common.SymbolRegular.Copy24, FontSize = 24 };
		}
	}

	private static Viewbox Canvas24(System.Windows.Shapes.Path path)
	{
		Canvas canvas = new() { Width = 24, Height = 24 };
		canvas.Children.Add(path);
		return new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
	}

	private static System.Windows.Shapes.Path Shape(string data, Brush? fill)
	{
		Geometry geometry;
		try
		{
			geometry = Geometry.Parse(data);
			geometry.Freeze();
		}
		catch (Exception ex) when (ex is FormatException or InvalidOperationException)
		{
			App.Logger?.WriteLine(LogIdent, "An icon could not be drawn: " + ex.Message);
			geometry = Geometry.Empty;
		}
		return new System.Windows.Shapes.Path
		{
			Data = geometry,
			Fill = fill,
			Stretch = Stretch.None,
		};
	}

	private async void Dialog_Loaded(object sender, RoutedEventArgs e)
	{
		Loaded -= Dialog_Loaded;
		await RefreshStatusesAsync();
	}

	private async Task RefreshStatusesAsync()
	{
		foreach (ClientRow row in _rows)
		{
			if (row.IsConnected == null)
				continue;
			Func<bool> check = row.IsConnected;
			bool connected;
			try
			{
				connected = await Task.Run(check);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The connection check failed: " + ex.Message);
				connected = false;
			}
			if (!IsLoaded)
				return;
			row.Detail.Text = connected ? Strings.Ui_Mcp_StatusConnected : Strings.Ui_Mcp_StatusNotConnected;
		}
	}

	private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => _ = RunSelectedAsync();

	private void Connect_Click(object sender, RoutedEventArgs e) => _ = RunSelectedAsync();

	private async Task RunSelectedAsync()
	{
		if (_busy || _list.SelectedIndex < 0 || _list.SelectedIndex >= _rows.Count)
			return;

		ClientRow row = _rows[_list.SelectedIndex];
		if (row.Connect == null)
		{
			await CopyConfigAsync();
			return;
		}

		_busy = true;
		_connect.IsEnabled = false;
		ShowStatus(Strings.Ui_Mcp_Connecting);
		bool ok;
		try
		{
			Func<bool> connect = row.Connect;
			ok = await Task.Run(connect);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Connecting failed: " + ex.Message);
			ok = false;
		}
		finally
		{
			_busy = false;
		}

		if (!IsLoaded)
			return;
		_connect.IsEnabled = true;
		ShowStatus(string.Format(ok ? Strings.Ui_Mcp_ConnectedRestart : Strings.Ui_Mcp_ConnectFailed, row.Name));
		await RefreshStatusesAsync();
	}

	private async Task CopyConfigAsync()
	{
		string json = McpConnectInfo.ConfigJson();
		for (int attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				Clipboard.SetText(json);
				ShowStatus(Strings.Ui_Mcp_ConfigCopied);
				return;
			}
			catch (System.Runtime.InteropServices.COMException)
			{
				await Task.Delay(120);
			}
		}
		ShowStatus(Strings.Ui_Mcp_CopyFailed);
	}

	private void ShowStatus(string text)
	{
		_status.Text = text;
		_status.Visibility = Visibility.Visible;
	}

	private void Dialog_Closed(object? sender, EventArgs e)
	{
		Closed -= Dialog_Closed;
		Loaded -= Dialog_Loaded;
		_connect.Click -= Connect_Click;
		_list.MouseDoubleClick -= List_MouseDoubleClick;
	}
}
