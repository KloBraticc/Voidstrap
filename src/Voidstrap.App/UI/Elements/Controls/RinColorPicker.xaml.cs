using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.UI.Elements.Controls;

public partial class RinColorPicker : UserControl
{
	public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
		nameof(SelectedColor),
		typeof(Color),
		typeof(RinColorPicker),
		new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

	public static readonly DependencyProperty AlphaEnabledProperty = DependencyProperty.Register(
		nameof(AlphaEnabled),
		typeof(bool),
		typeof(RinColorPicker),
		new PropertyMetadata(true, OnAlphaEnabledChanged));

	private double _h;
	private double _s = 1;
	private double _v = 1;
	private double _a = 1;
	private bool _updating;
	private bool _dragSpectrum;
	private bool _dragValue;
	private bool _dragAlpha;
	private SolidColorBrush? _linuxSpectrumStroke;
	private SolidColorBrush? _linuxPreviewBrush;
	private LinearGradientBrush? _linuxValueBrush;
	private GradientStop? _linuxValueStop;
	private LinearGradientBrush? _linuxAlphaBrush;
	private GradientStop? _linuxAlphaStart;
	private GradientStop? _linuxAlphaEnd;
	private int _linuxInputRefreshPending;

	public Color SelectedColor
	{
		get => (Color)GetValue(SelectedColorProperty);
		set => SetValue(SelectedColorProperty, value);
	}

	public bool AlphaEnabled
	{
		get => (bool)GetValue(AlphaEnabledProperty);
		set => SetValue(AlphaEnabledProperty, value);
	}

	public event EventHandler<Color>? ColorChanged;

	public RinColorPicker()
	{
		InitializeComponent();
		Loaded += OnLoaded;
		SizeChanged += OnAnySizeChanged;
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			InitializeLinuxRendering();
			SpectrumGrid.Focusable = true;
			ValueTrack.Focusable = true;
			AlphaTrack.Focusable = true;
			SpectrumGrid.KeyDown += OnLinuxTrackKeyDown;
			ValueTrack.KeyDown += OnLinuxTrackKeyDown;
			AlphaTrack.KeyDown += OnLinuxTrackKeyDown;
			HexBox.LostKeyboardFocus += OnLinuxInputLostKeyboardFocus;
			C1Box.LostKeyboardFocus += OnLinuxInputLostKeyboardFocus;
			C2Box.LostKeyboardFocus += OnLinuxInputLostKeyboardFocus;
			C3Box.LostKeyboardFocus += OnLinuxInputLostKeyboardFocus;
			AlphaBox.LostKeyboardFocus += OnLinuxInputLostKeyboardFocus;
			LostMouseCapture += OnLinuxLostMouseCapture;
			PreviewMouseLeftButtonUp += OnLinuxPreviewMouseUp;
			Unloaded += OnLinuxUnloaded;
		}
	}

	private void OnLinuxInputLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		RefreshInputs();
	}

	private void OnLinuxTrackKeyDown(object sender, KeyEventArgs e)
	{
		bool changed = true;
		if (ReferenceEquals(sender, SpectrumGrid))
		{
			switch (e.Key)
			{
				case Key.Left:
					_h = (_h + 359) % 360;
					break;
				case Key.Right:
					_h = (_h + 1) % 360;
					break;
				case Key.Up:
					_s = Math.Min(1, _s + 0.01);
					break;
				case Key.Down:
					_s = Math.Max(0, _s - 0.01);
					break;
				default:
					changed = false;
					break;
			}
		}
		else if (ReferenceEquals(sender, ValueTrack))
		{
			changed = ApplyLinuxTrackKey(e.Key, ref _v);
		}
		else
		{
			changed = ApplyLinuxTrackKey(e.Key, ref _a);
		}

		if (changed)
		{
			Commit();
			e.Handled = true;
		}
	}

	private static bool ApplyLinuxTrackKey(Key key, ref double value)
	{
		switch (key)
		{
			case Key.Left:
			case Key.Down:
				value = Math.Max(0, value - 1.0 / 255.0);
				return true;
			case Key.Right:
			case Key.Up:
				value = Math.Min(1, value + 1.0 / 255.0);
				return true;
			case Key.Home:
				value = 0;
				return true;
			case Key.End:
				value = 1;
				return true;
			default:
				return false;
		}
	}

	private void InitializeLinuxRendering()
	{
		_linuxSpectrumStroke = new SolidColorBrush(Colors.White);
		_linuxPreviewBrush = new SolidColorBrush(SelectedColor);
		_linuxValueStop = new GradientStop(Colors.Red, 1);
		_linuxValueBrush = new LinearGradientBrush
		{
			StartPoint = new Point(0, 0.5),
			EndPoint = new Point(1, 0.5),
			GradientStops = new GradientStopCollection
			{
				new GradientStop(Colors.Black, 0),
				_linuxValueStop
			}
		};
		_linuxAlphaStart = new GradientStop(Colors.Transparent, 0);
		_linuxAlphaEnd = new GradientStop(Colors.White, 1);
		_linuxAlphaBrush = new LinearGradientBrush
		{
			StartPoint = new Point(0, 0.5),
			EndPoint = new Point(1, 0.5),
			GradientStops = new GradientStopCollection
			{
				_linuxAlphaStart,
				_linuxAlphaEnd
			}
		};
		SpectrumThumb.Stroke = _linuxSpectrumStroke;
		PreviewRect.Fill = _linuxPreviewBrush;
		ValueTrack.Background = _linuxValueBrush;
		AlphaGradientRect.Fill = _linuxAlphaBrush;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		ColorToHsv(SelectedColor, out _h, out _s, out _v);
		_a = SelectedColor.A / 255.0;
		RefreshAll();
	}

	private void OnAnySizeChanged(object sender, SizeChangedEventArgs e)
	{
		RefreshVisuals();
	}

	private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var self = (RinColorPicker)d;
		if (self._updating)
			return;
		Color c = (Color)e.NewValue;
		ColorToHsv(c, out double h, out double s, out double v);
		if (s > 0.005 && v > 0.005)
			self._h = h;
		if (v > 0.005)
			self._s = s;
		self._v = v;
		self._a = c.A / 255.0;
		self.RefreshAll();
	}

	private static void OnAlphaEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var self = (RinColorPicker)d;
		bool on = (bool)e.NewValue;
		self.AlphaRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
		self.AlphaInputRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
	}

	private Color CurrentColor()
	{
		Color c = HsvToColor(_h, _s, _v);
		c.A = (byte)Math.Round(_a * 255);
		return c;
	}

	private void Commit()
	{
		_updating = true;
		Color c = CurrentColor();
		SelectedColor = c;
		_updating = false;
		ColorChanged?.Invoke(this, c);
		RefreshVisuals();
		if (Voidstrap.Utility.Platform.IsLinux && IsDragging)
			QueueLinuxInputRefresh();
		else
			RefreshInputs();
	}

	private bool IsDragging => _dragSpectrum || _dragValue || _dragAlpha;

	internal bool IsInputRefreshPending => System.Threading.Volatile.Read(ref _linuxInputRefreshPending) != 0;

	internal void BeginAlphaInteraction()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
			BeginLinuxDrag(AlphaTrack, 2);
	}

	internal void EndInteraction()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
			EndLinuxDrag(true);
	}

	private void QueueLinuxInputRefresh()
	{
		if (System.Threading.Interlocked.Exchange(ref _linuxInputRefreshPending, 1) != 0)
			return;

		Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
		{
			System.Threading.Volatile.Write(ref _linuxInputRefreshPending, 0);
			if (IsLoaded)
				RefreshInputs();
		}));
	}

	private void RefreshAll()
	{
		RefreshVisuals();
		RefreshInputs();
	}

	private void RefreshVisuals()
	{
		double w = SpectrumGrid.ActualWidth;
		double h = SpectrumGrid.ActualHeight;
		if (w > 1 && h > 1)
		{
			Canvas.SetLeft(SpectrumThumb, _h / 360.0 * w - 7);
			Canvas.SetTop(SpectrumThumb, (1 - _s) * h - 7);
		}
		Color opaque = HsvToColor(_h, _s, _v);
		double lum = (0.299 * opaque.R + 0.587 * opaque.G + 0.114 * opaque.B) / 255.0;
		Color thumbColor = lum < 0.75 ? Colors.White : Colors.Black;
		if (_linuxSpectrumStroke != null)
			_linuxSpectrumStroke.Color = thumbColor;
		else
			SpectrumThumb.Stroke = new SolidColorBrush(thumbColor);

		Color maximum = HsvToColor(_h, _s, 1);
		if (_linuxValueStop != null)
			_linuxValueStop.Color = maximum;
		else
			ValueTrack.Background = new LinearGradientBrush(Colors.Black, maximum, 0);
		double vw = ValueTrack.ActualWidth;
		if (vw > 18)
		{
			Canvas.SetLeft(ValueHandle, _v * (vw - 18));
			Canvas.SetTop(ValueHandle, 0);
		}

		Color solid = HsvToColor(_h, _s, _v);
		Color transparent = Color.FromArgb(0, solid.R, solid.G, solid.B);
		Color opaqueAlpha = Color.FromArgb(255, solid.R, solid.G, solid.B);
		if (_linuxAlphaStart != null && _linuxAlphaEnd != null)
		{
			_linuxAlphaStart.Color = transparent;
			_linuxAlphaEnd.Color = opaqueAlpha;
		}
		else
			AlphaGradientRect.Fill = new LinearGradientBrush(transparent, opaqueAlpha, 0);
		double aw = AlphaTrack.ActualWidth;
		if (aw > 18)
		{
			Canvas.SetLeft(AlphaHandle, _a * (aw - 18));
			Canvas.SetTop(AlphaHandle, 0);
		}

		Color current = CurrentColor();
		if (_linuxPreviewBrush != null)
			_linuxPreviewBrush.Color = current;
		else
			PreviewRect.Fill = new SolidColorBrush(current);
	}

	private void RefreshInputs()
	{
		_updating = true;
		Color c = CurrentColor();
		HexBox.Text = AlphaEnabled && c.A != 255
			? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
			: $"#{c.R:X2}{c.G:X2}{c.B:X2}";
		if (ModeBox.SelectedIndex == 0)
		{
			C1Label.Text = "Red";
			C2Label.Text = "Green";
			C3Label.Text = "Blue";
			C1Box.MaxLength = 3;
			C1Box.Text = c.R.ToString();
			C2Box.Text = c.G.ToString();
			C3Box.Text = c.B.ToString();
		}
		else
		{
			C1Label.Text = "Hue";
			C2Label.Text = "Saturation";
			C3Label.Text = "Value";
			C1Box.MaxLength = 3;
			C1Box.Text = ((int)Math.Round(_h)).ToString();
			C2Box.Text = ((int)Math.Round(_s * 100)).ToString();
			C3Box.Text = ((int)Math.Round(_v * 100)).ToString();
		}
		AlphaBox.Text = ((int)Math.Round(_a * 100)).ToString();
		_updating = false;
	}

	internal void SetSpectrumFromPosition(Point p)
	{
		double w = SpectrumGrid.ActualWidth;
		double h = SpectrumGrid.ActualHeight;
		if (w < 1 || h < 1)
			return;
		double hue = Math.Clamp(p.X / w, 0, 1) * 360.0;
		double saturation = 1 - Math.Clamp(p.Y / h, 0, 1);
		if (Math.Abs(_h - hue) < 0.0001 && Math.Abs(_s - saturation) < 0.0001)
			return;
		_h = hue;
		_s = saturation;
		Commit();
	}

	internal void SetValueFromPosition(double x)
	{
		double width = ValueTrack.ActualWidth;
		if (width <= 1)
			return;
		double value = Voidstrap.Utility.Platform.IsLinux
			? Math.Clamp((x - ValueHandle.Width / 2.0) / Math.Max(1, width - ValueHandle.Width), 0, 1)
			: Math.Clamp(x / width, 0, 1);
		if (Math.Abs(_v - value) < 0.0001)
			return;
		_v = value;
		Commit();
	}

	internal void SetAlphaFromPosition(double x)
	{
		double width = AlphaTrack.ActualWidth;
		if (width <= 1)
			return;
		double alpha = Voidstrap.Utility.Platform.IsLinux
			? Math.Clamp((x - AlphaHandle.Width / 2.0) / Math.Max(1, width - AlphaHandle.Width), 0, 1)
			: Math.Clamp(x / width, 0, 1);
		if (Math.Abs(_a - alpha) < 0.0001)
			return;
		_a = alpha;
		Commit();
	}

	private void BeginLinuxDrag(UIElement target, int mode)
	{
		_dragSpectrum = mode == 0;
		_dragValue = mode == 1;
		_dragAlpha = mode == 2;
		target.Focus();
		Mouse.Capture(target, CaptureMode.Element);
	}

	private void EndLinuxDrag(bool releaseCapture)
	{
		if (!IsDragging)
			return;
		_dragSpectrum = false;
		_dragValue = false;
		_dragAlpha = false;
		if (releaseCapture && Mouse.Captured != null)
			Mouse.Capture(null);
		QueueLinuxInputRefresh();
	}

	private void OnLinuxLostMouseCapture(object sender, MouseEventArgs e)
	{
		EndLinuxDrag(false);
	}

	private void OnLinuxPreviewMouseUp(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left && IsDragging)
		{
			EndLinuxDrag(true);
			e.Handled = true;
		}
	}

	private void OnLinuxUnloaded(object sender, RoutedEventArgs e)
	{
		EndLinuxDrag(true);
	}

	private void Spectrum_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (e.ChangedButton != MouseButton.Left)
				return;
			BeginLinuxDrag(SpectrumGrid, 0);
			SetSpectrumFromPosition(e.GetPosition(SpectrumGrid));
			e.Handled = true;
			return;
		}
		_dragSpectrum = true;
		SpectrumGrid.CaptureMouse();
		SetSpectrumFromPosition(e.GetPosition(SpectrumGrid));
	}

	private void Spectrum_MouseMove(object sender, MouseEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (!_dragSpectrum)
				return;
			if (e.LeftButton != MouseButtonState.Pressed)
			{
				EndLinuxDrag(true);
				return;
			}
			SetSpectrumFromPosition(e.GetPosition(SpectrumGrid));
			e.Handled = true;
			return;
		}
		if (_dragSpectrum)
			SetSpectrumFromPosition(e.GetPosition(SpectrumGrid));
	}

	private void Spectrum_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (e.ChangedButton == MouseButton.Left && _dragSpectrum)
			{
				EndLinuxDrag(true);
				e.Handled = true;
			}
			return;
		}
		_dragSpectrum = false;
		SpectrumGrid.ReleaseMouseCapture();
	}

	private void Value_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (e.ChangedButton != MouseButton.Left)
				return;
			BeginLinuxDrag(ValueTrack, 1);
			SetValueFromPosition(e.GetPosition(ValueTrack).X);
			e.Handled = true;
			return;
		}
		_dragValue = true;
		ValueTrack.CaptureMouse();
		_v = Math.Clamp(e.GetPosition(ValueTrack).X / Math.Max(1, ValueTrack.ActualWidth), 0, 1);
		Commit();
	}

	private void Value_MouseMove(object sender, MouseEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (!_dragValue)
				return;
			if (e.LeftButton != MouseButtonState.Pressed)
			{
				EndLinuxDrag(true);
				return;
			}
			SetValueFromPosition(e.GetPosition(ValueTrack).X);
			e.Handled = true;
			return;
		}
		if (!_dragValue)
			return;
		_v = Math.Clamp(e.GetPosition(ValueTrack).X / Math.Max(1, ValueTrack.ActualWidth), 0, 1);
		Commit();
	}

	private void Value_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (e.ChangedButton == MouseButton.Left && _dragValue)
			{
				EndLinuxDrag(true);
				e.Handled = true;
			}
			return;
		}
		_dragValue = false;
		ValueTrack.ReleaseMouseCapture();
	}

	private void Alpha_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (e.ChangedButton != MouseButton.Left)
				return;
			BeginLinuxDrag(AlphaTrack, 2);
			SetAlphaFromPosition(e.GetPosition(AlphaTrack).X);
			e.Handled = true;
			return;
		}
		_dragAlpha = true;
		AlphaTrack.CaptureMouse();
		_a = Math.Clamp(e.GetPosition(AlphaTrack).X / Math.Max(1, AlphaTrack.ActualWidth), 0, 1);
		Commit();
	}

	private void Alpha_MouseMove(object sender, MouseEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (!_dragAlpha)
				return;
			if (e.LeftButton != MouseButtonState.Pressed)
			{
				EndLinuxDrag(true);
				return;
			}
			SetAlphaFromPosition(e.GetPosition(AlphaTrack).X);
			e.Handled = true;
			return;
		}
		if (!_dragAlpha)
			return;
		_a = Math.Clamp(e.GetPosition(AlphaTrack).X / Math.Max(1, AlphaTrack.ActualWidth), 0, 1);
		Commit();
	}

	private void Alpha_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			if (e.ChangedButton == MouseButton.Left && _dragAlpha)
			{
				EndLinuxDrag(true);
				e.Handled = true;
			}
			return;
		}
		_dragAlpha = false;
		AlphaTrack.ReleaseMouseCapture();
	}

	private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_updating || !HexBox.IsKeyboardFocused)
			return;
		string t = HexBox.Text.Trim().TrimStart('#');
		if (t.Length != 6 && t.Length != 8)
			return;
		if (!uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
			return;
		byte a = 255, r, g, b;
		if (t.Length == 8)
		{
			a = (byte)(val >> 24);
			r = (byte)(val >> 16);
			g = (byte)(val >> 8);
			b = (byte)val;
		}
		else
		{
			r = (byte)(val >> 16);
			g = (byte)(val >> 8);
			b = (byte)val;
		}
		ColorToHsv(Color.FromRgb(r, g, b), out _h, out _s, out _v);
		_a = a / 255.0;
		_updating = true;
		SelectedColor = Color.FromArgb(a, r, g, b);
		_updating = false;
		ColorChanged?.Invoke(this, SelectedColor);
		RefreshVisuals();
	}

	private void Channel_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_updating)
			return;
		var box = (TextBox)sender;
		if (!box.IsKeyboardFocused || !int.TryParse(box.Text, out int val))
			return;
		if (ModeBox.SelectedIndex == 0)
		{
			val = Math.Clamp(val, 0, 255);
			Color c = CurrentColor();
			byte r = c.R, g = c.G, b = c.B;
			if (box == C1Box) r = (byte)val;
			else if (box == C2Box) g = (byte)val;
			else b = (byte)val;
			ColorToHsv(Color.FromRgb(r, g, b), out _h, out _s, out _v);
		}
		else
		{
			if (box == C1Box) _h = Math.Clamp(val, 0, 360);
			else if (box == C2Box) _s = Math.Clamp(val, 0, 100) / 100.0;
			else _v = Math.Clamp(val, 0, 100) / 100.0;
		}
		_updating = true;
		SelectedColor = CurrentColor();
		_updating = false;
		ColorChanged?.Invoke(this, SelectedColor);
		RefreshVisuals();
	}

	private void AlphaBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_updating || !AlphaBox.IsKeyboardFocused || !int.TryParse(AlphaBox.Text, out int val))
			return;
		_a = Math.Clamp(val, 0, 100) / 100.0;
		_updating = true;
		SelectedColor = CurrentColor();
		_updating = false;
		ColorChanged?.Invoke(this, SelectedColor);
		RefreshVisuals();
	}

	private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (IsLoaded)
			RefreshInputs();
	}

	private static Color HsvToColor(double h, double s, double v)
	{
		h = ((h % 360) + 360) % 360;
		double c = v * s;
		double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
		double m = v - c;
		double r, g, b;
		if (h < 60) { r = c; g = x; b = 0; }
		else if (h < 120) { r = x; g = c; b = 0; }
		else if (h < 180) { r = 0; g = c; b = x; }
		else if (h < 240) { r = 0; g = x; b = c; }
		else if (h < 300) { r = x; g = 0; b = c; }
		else { r = c; g = 0; b = x; }
		return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
	}

	private static void ColorToHsv(Color c, out double h, out double s, out double v)
	{
		double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
		double max = Math.Max(r, Math.Max(g, b));
		double min = Math.Min(r, Math.Min(g, b));
		double d = max - min;
		v = max;
		s = max <= 0 ? 0 : d / max;
		if (d <= 0)
			h = 0;
		else if (max == r)
			h = 60 * (((g - b) / d) % 6);
		else if (max == g)
			h = 60 * ((b - r) / d + 2);
		else
			h = 60 * ((r - g) / d + 4);
		if (h < 0)
			h += 360;
	}
}
