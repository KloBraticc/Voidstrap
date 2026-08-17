using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
		RefreshInputs();
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
		SpectrumThumb.Stroke = new SolidColorBrush(lum < 0.75 ? Colors.White : Colors.Black);

		var valBrush = new LinearGradientBrush(Colors.Black, HsvToColor(_h, _s, 1), 0);
		ValueTrack.Background = valBrush;
		double vw = ValueTrack.ActualWidth;
		if (vw > 18)
		{
			Canvas.SetLeft(ValueHandle, _v * (vw - 18));
			Canvas.SetTop(ValueHandle, 0);
		}

		Color solid = HsvToColor(_h, _s, _v);
		var aBrush = new LinearGradientBrush(Color.FromArgb(0, solid.R, solid.G, solid.B), Color.FromArgb(255, solid.R, solid.G, solid.B), 0);
		AlphaGradientRect.Fill = aBrush;
		double aw = AlphaTrack.ActualWidth;
		if (aw > 18)
		{
			Canvas.SetLeft(AlphaHandle, _a * (aw - 18));
			Canvas.SetTop(AlphaHandle, 0);
		}

		PreviewRect.Fill = new SolidColorBrush(CurrentColor());
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

	private void SpectrumUpdate(Point p)
	{
		double w = SpectrumGrid.ActualWidth;
		double h = SpectrumGrid.ActualHeight;
		if (w < 1 || h < 1)
			return;
		_h = Math.Clamp(p.X / w, 0, 1) * 360.0;
		_s = 1 - Math.Clamp(p.Y / h, 0, 1);
		Commit();
	}

	private void Spectrum_MouseDown(object sender, MouseButtonEventArgs e)
	{
		_dragSpectrum = true;
		SpectrumGrid.CaptureMouse();
		SpectrumUpdate(e.GetPosition(SpectrumGrid));
	}

	private void Spectrum_MouseMove(object sender, MouseEventArgs e)
	{
		if (_dragSpectrum)
			SpectrumUpdate(e.GetPosition(SpectrumGrid));
	}

	private void Spectrum_MouseUp(object sender, MouseButtonEventArgs e)
	{
		_dragSpectrum = false;
		SpectrumGrid.ReleaseMouseCapture();
	}

	private void Value_MouseDown(object sender, MouseButtonEventArgs e)
	{
		_dragValue = true;
		ValueTrack.CaptureMouse();
		_v = Math.Clamp(e.GetPosition(ValueTrack).X / Math.Max(1, ValueTrack.ActualWidth), 0, 1);
		Commit();
	}

	private void Value_MouseMove(object sender, MouseEventArgs e)
	{
		if (!_dragValue)
			return;
		_v = Math.Clamp(e.GetPosition(ValueTrack).X / Math.Max(1, ValueTrack.ActualWidth), 0, 1);
		Commit();
	}

	private void Value_MouseUp(object sender, MouseButtonEventArgs e)
	{
		_dragValue = false;
		ValueTrack.ReleaseMouseCapture();
	}

	private void Alpha_MouseDown(object sender, MouseButtonEventArgs e)
	{
		_dragAlpha = true;
		AlphaTrack.CaptureMouse();
		_a = Math.Clamp(e.GetPosition(AlphaTrack).X / Math.Max(1, AlphaTrack.ActualWidth), 0, 1);
		Commit();
	}

	private void Alpha_MouseMove(object sender, MouseEventArgs e)
	{
		if (!_dragAlpha)
			return;
		_a = Math.Clamp(e.GetPosition(AlphaTrack).X / Math.Max(1, AlphaTrack.ActualWidth), 0, 1);
		Commit();
	}

	private void Alpha_MouseUp(object sender, MouseButtonEventArgs e)
	{
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
