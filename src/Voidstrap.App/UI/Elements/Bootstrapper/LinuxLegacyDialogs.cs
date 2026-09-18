using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Voidstrap.UI.Elements.Bootstrapper.Base;
using Voidstrap.UI.Elements.Settings;

namespace Voidstrap.UI.Elements.Bootstrapper;

internal sealed class LegacyProgressBar : System.Windows.Controls.Border
{
	private static readonly TimeSpan MarqueeDuration = TimeSpan.FromMilliseconds(1600d);

	private readonly System.Windows.Controls.Border _fill;
	private readonly TranslateTransform _slide = new();

	private double _maximum = 100d;
	private double _value;
	private bool _indeterminate = true;
	private bool _detached;

	internal LegacyProgressBar(Color track, Color border, Color fill)
	{
		Background = new SolidColorBrush(track);
		BorderBrush = new SolidColorBrush(border);
		BorderThickness = new Thickness(1d);
		ClipToBounds = true;
		SnapsToDevicePixels = true;
		UseLayoutRounding = true;

		_fill = new System.Windows.Controls.Border
		{
			Background = new SolidColorBrush(fill),
			HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
			RenderTransform = _slide
		};
		Child = _fill;
		SizeChanged += OnSizeChanged;
	}

	internal double Maximum
	{
		get => _maximum;
		set
		{
			_maximum = Math.Max(1d, value);
			Refresh();
		}
	}

	internal double Value
	{
		get => _value;
		set
		{
			_value = value;
			Refresh();
		}
	}

	internal bool IsIndeterminate
	{
		get => _indeterminate;
		set
		{
			_indeterminate = value;
			Refresh();
		}
	}

	internal void Detach()
	{
		if (_detached)
			return;

		_detached = true;
		SizeChanged -= OnSizeChanged;
		StopMarquee();
	}

	private void OnSizeChanged(object sender, SizeChangedEventArgs e)
	{
		Refresh();
	}

	private void Refresh()
	{
		if (_detached)
			return;

		double available = Math.Max(0d, ActualWidth - BorderThickness.Left - BorderThickness.Right);
		if (available <= 0d)
			return;

		StopMarquee();
		if (_indeterminate)
		{
			double width = Math.Max(24d, available * 0.35d);
			_fill.Width = width;
			StartMarquee(width, available);
			return;
		}

		_slide.X = 0d;
		_fill.Width = Math.Clamp(available * (_value / _maximum), 0d, available);
	}

	private void StartMarquee(double width, double available)
	{
		DoubleAnimation animation = new()
		{
			From = 0d - width,
			To = available,
			Duration = new Duration(MarqueeDuration),
			RepeatBehavior = RepeatBehavior.Forever
		};
		_slide.BeginAnimation(TranslateTransform.XProperty, animation);
	}

	private void StopMarquee()
	{
		_slide.BeginAnimation(TranslateTransform.XProperty, null);
	}
}

public abstract class LinuxBootstrapperDialogBase : System.Windows.Window, IBootstrapperDialog
{
	protected static readonly System.Windows.Media.FontFamily LegacyFont = new("Tahoma, DejaVu Sans, Liberation Sans, Segoe UI, Arial");
	protected static readonly System.Windows.Media.FontFamily ModernFont = new("Segoe UI, DejaVu Sans, Liberation Sans, Arial");

	private System.Windows.Window? _mainWindow;
	private bool _isClosing;
	private string _message = "Please wait...";
	private ProgressBarStyle _progressStyle = ProgressBarStyle.Marquee;
	private bool _cancelEnabled;

	protected System.Windows.Controls.TextBlock MessageText { get; set; } = null!;

	internal LegacyProgressBar Progress { get; set; } = null!;

	protected System.Windows.Controls.Button CancelButton { get; set; } = null!;

	public Voidstrap.Bootstrapper? Bootstrapper { get; set; }

	public Action? CancelCallback { get; set; }

	public string Message
	{
		get => _message;
		set
		{
			_message = value;
			if (MessageText is not null)
				MessageText.Text = value;
		}
	}

	public ProgressBarStyle ProgressStyle
	{
		get => _progressStyle;
		set
		{
			_progressStyle = value;
			if (Progress is not null)
				Progress.IsIndeterminate = value == ProgressBarStyle.Marquee;
		}
	}

	public int ProgressMaximum
	{
		get => Progress is null ? 0 : (int)Progress.Maximum;
		set
		{
			if (Progress is not null)
				Progress.Maximum = value;
		}
	}

	public int ProgressValue
	{
		get => Progress is null ? 0 : (int)Progress.Value;
		set
		{
			if (Progress is not null)
				Progress.Value = value;
		}
	}

	public TaskbarItemProgressState TaskbarProgressState { get; set; }

	public double TaskbarProgressValue { get; set; }

	public bool CancelEnabled
	{
		get => _cancelEnabled;
		set
		{
			_cancelEnabled = value;
			if (CancelButton is not null)
			{
				CancelButton.IsEnabled = value;
				CancelButton.Visibility = value ? Visibility.Visible : Visibility.Hidden;
			}
		}
	}

	protected LinuxBootstrapperDialogBase()
	{
		ShowInTaskbar = true;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterScreen;
		SnapsToDevicePixels = true;
		UseLayoutRounding = true;
		Title = App.Settings.Prop.BootstrapperTitle;
		Icon = Voidstrap.Extensions.IconEx.GetBootstrapperWindowIcon();

		_mainWindow = System.Windows.Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
		if (App.Settings.Prop.BackgroundWindow)
			_mainWindow?.Hide();

		AudioPlayerHelper.PlayStartupAudio();
		Closed += OnDialogClosed;
		Closing += OnDialogClosing;
	}

	protected void FinishSetup()
	{
		Message = _message;
		ProgressStyle = _progressStyle;
		CancelEnabled = _cancelEnabled;
		if (CancelButton is not null)
			CancelButton.Click += OnCancelClicked;
	}

	private void OnCancelClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			CancelCallback?.Invoke();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("LinuxBootstrapperDialogBase::OnCancelClicked", ex);
		}

		Bootstrapper?.Cancel();
		CloseBootstrapper();
	}

	private void OnDialogClosing(object? sender, CancelEventArgs e)
	{
		if (_isClosing)
			return;

		try
		{
			CancelCallback?.Invoke();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("LinuxBootstrapperDialogBase::OnDialogClosing", ex);
		}

		Bootstrapper?.Cancel();
	}

	private void OnDialogClosed(object? sender, EventArgs e)
	{
		Closed -= OnDialogClosed;
		Closing -= OnDialogClosing;
		if (CancelButton is not null)
			CancelButton.Click -= OnCancelClicked;
		Progress?.Detach();

		_mainWindow = System.Windows.Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
		if (App.Settings.Prop.BackgroundWindow)
			_mainWindow?.Show();

		AudioPlayerHelper.StopAudio();
	}

	public void ShowBootstrapper()
	{
		this.ShowOwnedDialog();
	}

	public void CloseBootstrapper()
	{
		if (_isClosing)
			return;

		_isClosing = true;
		_ = Dispatcher.BeginInvoke(new Action(Close));
	}

	public void ShowSuccess(string message, Action? callback)
	{
		BaseFunctions.ShowSuccess(message, callback);
	}

	protected static System.Windows.Controls.Border CreateSurface(Color background, UIElement content)
	{
		return new System.Windows.Controls.Border
		{
			Background = new SolidColorBrush(background),
			Child = content,
			SnapsToDevicePixels = true,
			UseLayoutRounding = true
		};
	}

	protected static System.Windows.Controls.TextBlock CreateText(
		System.Windows.Media.FontFamily font,
		double fontSize,
		Color foreground)
	{
		return new System.Windows.Controls.TextBlock
		{
			FontFamily = font,
			FontSize = fontSize,
			Foreground = new SolidColorBrush(foreground),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
	}

	protected static System.Windows.Controls.Button CreateLegacyButton(
		string content,
		double width,
		double height,
		System.Windows.Media.FontFamily font,
		double fontSize,
		Color face,
		Color text,
		Color border)
	{
		System.Windows.Controls.Button button = new()
		{
			Content = content,
			Width = width,
			Height = height,
			FontFamily = font,
			FontSize = fontSize,
			MinWidth = 0d,
			MinHeight = 0d,
			Padding = new Thickness(0d),
			OverridesDefaultStyle = true,
			FocusVisualStyle = null,
			Foreground = new SolidColorBrush(text),
			Cursor = System.Windows.Input.Cursors.Hand,
			Template = BuildLegacyButtonTemplate(face, text, border)
		};
		return button;
	}

	private static ControlTemplate BuildLegacyButtonTemplate(Color face, Color text, Color border)
	{
		Color hover = Blend(face, Colors.White, 0.35d);
		Color pressed = Blend(face, Colors.Black, 0.12d);
		Color disabled = Blend(face, Colors.Gray, 0.35d);

		FrameworkElementFactory root = new(typeof(System.Windows.Controls.Border), "Face");
		root.SetValue(System.Windows.Controls.Border.BackgroundProperty, new SolidColorBrush(face));
		root.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new SolidColorBrush(border));
		root.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1d));
		root.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(3d));
		root.SetValue(System.Windows.Controls.Border.SnapsToDevicePixelsProperty, true);

		FrameworkElementFactory presenter = new(typeof(System.Windows.Controls.ContentPresenter));
		presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
		presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
		presenter.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new SolidColorBrush(text));
		root.AppendChild(presenter);

		ControlTemplate template = new(typeof(System.Windows.Controls.Button))
		{
			VisualTree = root
		};

		Trigger over = new()
		{
			Property = UIElement.IsMouseOverProperty,
			Value = true
		};
		over.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, new SolidColorBrush(hover), "Face"));
		template.Triggers.Add(over);

		Trigger down = new()
		{
			Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty,
			Value = true
		};
		down.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, new SolidColorBrush(pressed), "Face"));
		template.Triggers.Add(down);

		Trigger off = new()
		{
			Property = UIElement.IsEnabledProperty,
			Value = false
		};
		off.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, new SolidColorBrush(disabled), "Face"));
		template.Triggers.Add(off);

		template.Seal();
		return template;
	}

	private static Color Blend(Color from, Color to, double amount)
	{
		return Color.FromRgb(
			(byte)Math.Clamp(from.R + (to.R - from.R) * amount, 0d, 255d),
			(byte)Math.Clamp(from.G + (to.G - from.G) * amount, 0d, 255d),
			(byte)Math.Clamp(from.B + (to.B - from.B) * amount, 0d, 255d));
	}

	protected static System.Windows.Controls.Image CreateIcon(double size)
	{
		System.Windows.Controls.Image icon = new()
		{
			Width = size,
			Height = size,
			Stretch = Stretch.Uniform,
			Source = Voidstrap.Extensions.IconEx.GetBootstrapperWindowIcon()
		};
		RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
		return icon;
	}

	protected static void Place(UIElement element, double left, double top)
	{
		Canvas.SetLeft(element, left);
		Canvas.SetTop(element, top);
	}
}

public sealed class LinuxLegacyDialog2008 : LinuxBootstrapperDialogBase
{
	private static readonly Color Surface = Color.FromRgb(240, 240, 240);
	private static readonly Color Ink = Color.FromRgb(0, 0, 0);
	private static readonly Color Face = Color.FromRgb(225, 225, 225);
	private static readonly Color Edge = Color.FromRgb(160, 160, 160);

	public LinuxLegacyDialog2008()
	{
		Width = 311d;
		Height = 122d;
		WindowStyle = WindowStyle.SingleBorderWindow;
		Background = new SolidColorBrush(Surface);

		Canvas surface = new();

		MessageText = CreateText(LegacyFont, 11d, Ink);
		MessageText.Width = 287d;
		Place(MessageText, 12d, 16d);

		Progress = new LegacyProgressBar(Color.FromRgb(230, 230, 230), Edge, Color.FromRgb(6, 176, 37))
		{
			Width = 281d,
			Height = 20d
		};
		Place(Progress, 15d, 47d);

		CancelButton = CreateLegacyButton("Cancel", 75d, 23d, LegacyFont, 11d, Face, Ink, Edge);
		Place(CancelButton, 221d, 83d);

		surface.Children.Add(MessageText);
		surface.Children.Add(Progress);
		surface.Children.Add(CancelButton);
		Content = CreateSurface(Surface, surface);
		FinishSetup();
	}
}

public sealed class LinuxLegacyDialog2011 : LinuxBootstrapperDialogBase
{
	private static readonly Color Surface = Color.FromRgb(240, 240, 240);
	private static readonly Color Ink = Color.FromRgb(0, 0, 0);
	private static readonly Color Face = Color.FromRgb(225, 225, 225);
	private static readonly Color Edge = Color.FromRgb(160, 160, 160);

	public LinuxLegacyDialog2011()
	{
		Width = 362d;
		Height = 131d;
		WindowStyle = WindowStyle.SingleBorderWindow;
		Background = new SolidColorBrush(Surface);

		Canvas surface = new();

		System.Windows.Controls.Image icon = CreateIcon(32d);
		Place(icon, 19d, 16d);

		MessageText = CreateText(ModernFont, 12d, Ink);
		MessageText.Width = 287d;
		Place(MessageText, 55d, 23d);

		Progress = new LegacyProgressBar(Color.FromRgb(230, 230, 230), Edge, Color.FromRgb(6, 176, 37))
		{
			Width = 287d,
			Height = 26d
		};
		Place(Progress, 58d, 51d);

		CancelButton = CreateLegacyButton("Cancel", 75d, 23d, ModernFont, 12d, Face, Ink, Edge);
		Place(CancelButton, 271d, 83d);

		surface.Children.Add(icon);
		surface.Children.Add(MessageText);
		surface.Children.Add(Progress);
		surface.Children.Add(CancelButton);
		Content = CreateSurface(Surface, surface);
		FinishSetup();
	}
}

public sealed class LinuxProgressDialog : LinuxBootstrapperDialogBase
{
	private static readonly Color Frame = Color.FromRgb(180, 180, 180);
	private static readonly Color Panel = Color.FromRgb(255, 255, 255);
	private static readonly Color Ink = Color.FromRgb(0, 0, 0);
	private static readonly Color Face = Color.FromRgb(238, 238, 238);
	private static readonly Color Edge = Color.FromRgb(176, 176, 176);

	public LinuxProgressDialog()
	{
		Width = 520d;
		Height = 320d;
		WindowStyle = WindowStyle.None;
		Background = new SolidColorBrush(Frame);

		Canvas inner = new();

		System.Windows.Controls.Image icon = CreateIcon(92d);
		Place(icon, 211d, 65d);

		MessageText = CreateText(LegacyFont, 15d, Ink);
		MessageText.Width = 460d;
		MessageText.TextAlignment = TextAlignment.Center;
		Place(MessageText, 28d, 198d);

		Progress = new LegacyProgressBar(Color.FromRgb(235, 235, 235), Edge, Color.FromRgb(6, 176, 37))
		{
			Width = 460d,
			Height = 20d
		};
		Place(Progress, 28d, 240d);

		CancelButton = CreateLegacyButton("Cancel", 130d, 44d, LegacyFont, 16d, Face, Color.FromRgb(75, 75, 75), Edge);
		Place(CancelButton, 193d, 263d);

		inner.Children.Add(icon);
		inner.Children.Add(MessageText);
		inner.Children.Add(Progress);
		inner.Children.Add(CancelButton);

		System.Windows.Controls.Border panel = new()
		{
			Width = 518d,
			Height = 318d,
			Background = new SolidColorBrush(Panel),
			Child = inner
		};

		Canvas surface = new();
		Place(panel, 1d, 1d);
		surface.Children.Add(panel);
		Content = CreateSurface(Frame, surface);
		FinishSetup();
	}
}

public sealed class LinuxVistaDialog : LinuxBootstrapperDialogBase
{
	private static readonly Color Surface = Color.FromRgb(255, 255, 255);
	private static readonly Color Footer = Color.FromRgb(240, 240, 240);
	private static readonly Color Heading = Color.FromRgb(0, 51, 153);
	private static readonly Color Ink = Color.FromRgb(0, 0, 0);
	private static readonly Color Face = Color.FromRgb(225, 225, 225);
	private static readonly Color Edge = Color.FromRgb(160, 160, 160);

	public LinuxVistaDialog()
	{
		Width = 420d;
		Height = 176d;
		WindowStyle = WindowStyle.SingleBorderWindow;
		Background = new SolidColorBrush(Surface);

		Grid root = new();
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1d, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		Grid body = new() { Margin = new Thickness(24d, 22d, 24d, 18d) };
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });

		System.Windows.Controls.Image icon = CreateIcon(32d);
		icon.VerticalAlignment = VerticalAlignment.Top;
		Grid.SetColumn(icon, 0);

		StackPanel column = new() { Margin = new Thickness(16d, 0d, 0d, 0d) };
		Grid.SetColumn(column, 1);

		MessageText = CreateText(ModernFont, 15d, Heading);
		MessageText.TextTrimming = TextTrimming.None;
		MessageText.TextWrapping = TextWrapping.Wrap;

		Progress = new LegacyProgressBar(Color.FromRgb(235, 235, 235), Edge, Color.FromRgb(6, 176, 37))
		{
			Height = 16d,
			Margin = new Thickness(0d, 16d, 0d, 0d)
		};

		column.Children.Add(MessageText);
		column.Children.Add(Progress);
		body.Children.Add(icon);
		body.Children.Add(column);
		Grid.SetRow(body, 0);

		System.Windows.Controls.Border footer = new()
		{
			Background = new SolidColorBrush(Footer),
			BorderBrush = new SolidColorBrush(Color.FromRgb(223, 223, 223)),
			BorderThickness = new Thickness(0d, 1d, 0d, 0d),
			Padding = new Thickness(12d)
		};
		Grid.SetRow(footer, 1);

		CancelButton = CreateLegacyButton("Cancel", 88d, 26d, ModernFont, 12d, Face, Ink, Edge);
		CancelButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
		footer.Child = CancelButton;

		root.Children.Add(body);
		root.Children.Add(footer);
		Content = CreateSurface(Surface, root);
		FinishSetup();
	}
}
