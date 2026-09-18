using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Controls;

public partial class WorldServerMap : UserControl
{
	private const double MapWidth = 3600.0;
	private const double MapHeight = 1800.0;
	private const double MaxZoom = 48.0;
	private const double PinSize = 12.0;
	private const double RingSize = 22.0;
	private const double WheelStep = 1.3;
	private const double ButtonStep = 1.6;
	private const double FocusZoom = 5.0;

	private static Geometry? _land;
	private static Geometry? _borders;

	private sealed class Pin
	{
		public BehaviourViewModel.DatacenterItem Item = null!;
		public Grid Element = null!;
		public Ellipse Dot = null!;
		public Ellipse Ring = null!;
	}

	public static readonly DependencyProperty DatacentersProperty = DependencyProperty.Register(nameof(Datacenters), typeof(IEnumerable), typeof(WorldServerMap), new PropertyMetadata(null, OnDatacentersChanged));

	public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(nameof(SelectedKey), typeof(string), typeof(WorldServerMap), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedKeyChanged));

	public static readonly DependencyProperty UserLatitudeProperty = DependencyProperty.Register(nameof(UserLatitude), typeof(double), typeof(WorldServerMap), new PropertyMetadata(double.NaN, OnUserLocationChanged));

	public static readonly DependencyProperty UserLongitudeProperty = DependencyProperty.Register(nameof(UserLongitude), typeof(double), typeof(WorldServerMap), new PropertyMetadata(double.NaN, OnUserLocationChanged));

	public IEnumerable? Datacenters
	{
		get => (IEnumerable?)GetValue(DatacentersProperty);
		set => SetValue(DatacentersProperty, value);
	}

	public string SelectedKey
	{
		get => (string)GetValue(SelectedKeyProperty);
		set => SetValue(SelectedKeyProperty, value);
	}

	public double UserLatitude
	{
		get => (double)GetValue(UserLatitudeProperty);
		set => SetValue(UserLatitudeProperty, value);
	}

	public double UserLongitude
	{
		get => (double)GetValue(UserLongitudeProperty);
		set => SetValue(UserLongitudeProperty, value);
	}

	private readonly MatrixTransform _view = new MatrixTransform(Matrix.Identity);
	private readonly List<Pin> _pins = new List<Pin>();
	private readonly Border _label;
	private readonly TextBlock _labelText;
	private INotifyCollectionChanged? _observed;
	private bool _rebuildQueued;
	private bool _fitted;
	private bool _dragging;
	private bool _dragMoved;
	private Point _dragStart;
	private Matrix _dragMatrix;
	private double _fitScale = 1.0;

	public WorldServerMap()
	{
		InitializeComponent();
		_land ??= LoadGeometry("WorldLand.txt");
		_borders ??= LoadGeometry("WorldBorders.txt");
		LandPath.Data = _land;
		BorderPath.Data = _borders;
		MapLayer.RenderTransform = _view;


		_labelText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold };
		_labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
		_label = new Border { Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), IsHitTestVisible = false, Visibility = Visibility.Collapsed, Child = _labelText };
		_label.SetResourceReference(Border.BackgroundProperty, "SystemAccentColorPrimaryBrush");
		_label.SetResourceReference(Border.BorderBrushProperty, "SolidBackgroundFillColorBaseBrush");
		Panel.SetZIndex(_label, 4);
		PinLayer.Children.Add(_label);
	}

	private static Geometry LoadGeometry(string name)
	{
		Geometry geometry = Geometry.Parse(Resource.GetString(name));
		geometry.Freeze();
		return geometry;
	}

	private static Point Project(double lat, double lon)
	{
		return new Point((lon + 180.0) * 10.0, (90.0 - lat) * 10.0);
	}

	private Point ToScreen(double lat, double lon)
	{
		return _view.Matrix.Transform(Project(lat, lon));
	}

	private static void OnDatacentersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		WorldServerMap map = (WorldServerMap)d;
		map.Detach();
		if (map.IsLoaded)
			map.Attach();
		map.QueueRebuild();
	}

	private static void OnSelectedKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		WorldServerMap map = (WorldServerMap)d;
		foreach (Pin pin in map._pins)
			map.StylePin(pin);
		map.UpdateSelectionText();
		map.Reposition();
	}

	private static void OnUserLocationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		((WorldServerMap)d).Reposition();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		Attach();
		QueueRebuild();
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		Detach();
		ClearPins();
	}

	private void Attach()
	{
		if (_observed != null)
			return;
		_observed = Datacenters as INotifyCollectionChanged;
		if (_observed != null)
			_observed.CollectionChanged += Datacenters_CollectionChanged;
	}

	private void Detach()
	{
		if (_observed != null)
			_observed.CollectionChanged -= Datacenters_CollectionChanged;
		_observed = null;
	}

	private void Datacenters_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		QueueRebuild();
	}

	private void QueueRebuild()
	{
		if (_rebuildQueued)
			return;
		_rebuildQueued = true;
		Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RebuildPins));
	}

	private void RebuildPins()
	{
		_rebuildQueued = false;
		ClearPins();
		if (Datacenters != null)
		{
			foreach (object entry in Datacenters)
			{
				if (entry is not BehaviourViewModel.DatacenterItem item)
					continue;
				Pin pin = CreatePin(item);
				_pins.Add(pin);
				PinLayer.Children.Add(pin.Element);
				StylePin(pin);
			}
		}
		UpdateSelectionText();
		Reposition();
	}

	private void ClearPins()
	{
		foreach (Pin pin in _pins)
		{
			pin.Item.PropertyChanged -= Item_PropertyChanged;
			pin.Element.MouseLeftButtonDown -= Pin_MouseLeftButtonDown;
			PinLayer.Children.Remove(pin.Element);
		}
		_pins.Clear();
	}

	private Pin CreatePin(BehaviourViewModel.DatacenterItem item)
	{
		Ellipse ring = new Ellipse { Width = RingSize, Height = RingSize, StrokeThickness = 2.0, Fill = Brushes.Transparent, Visibility = Visibility.Collapsed };
		ring.SetResourceReference(Shape.StrokeProperty, "SystemAccentColorPrimaryBrush");
		Ellipse dot = new Ellipse { Width = PinSize, Height = PinSize, StrokeThickness = 1.5 };
		dot.SetResourceReference(Shape.StrokeProperty, "SolidBackgroundFillColorBaseBrush");
		Grid element = new Grid { Width = RingSize, Height = RingSize, Background = Brushes.Transparent, Cursor = Cursors.Hand };
		element.Children.Add(ring);
		element.Children.Add(dot);
		ToolTipService.SetInitialShowDelay(element, 150);
		Pin pin = new Pin { Item = item, Element = element, Dot = dot, Ring = ring };
		element.Tag = pin;
		element.MouseLeftButtonDown += Pin_MouseLeftButtonDown;
		item.PropertyChanged += Item_PropertyChanged;
		return pin;
	}

	private void StylePin(Pin pin)
	{
		BehaviourViewModel.DatacenterItem item = pin.Item;
		string fill = !item.IsAllowed ? "TextFillColorTertiaryBrush"
			: item.PingMs < 0 ? "TextFillColorSecondaryBrush"
			: item.PingMs < 70 ? "SystemFillColorSuccessBrush"
			: item.PingMs < 140 ? "SystemFillColorCautionBrush"
			: "SystemFillColorCriticalBrush";
		pin.Dot.SetResourceReference(Shape.FillProperty, fill);
		pin.Element.Opacity = item.IsAllowed ? 1.0 : 0.45;
		bool selected = IsSelected(item);
		pin.Ring.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
		Panel.SetZIndex(pin.Element, selected ? 2 : (item.IsAllowed ? 1 : 0));
		string detail = item.PingDisplay;
		if (item.DistanceKm >= 0.0)
			detail += ", " + item.DistanceDisplay;
		string text = item.Location + "\n" + detail + "\n" + item.ServerIps;
		if (!item.IsAllowed)
			text += "\nBlocked in the Datacenters list";
		pin.Element.ToolTip = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
	}

	private bool IsSelected(BehaviourViewModel.DatacenterItem item)
	{
		return string.Equals(item.Key, SelectedKey, StringComparison.OrdinalIgnoreCase);
	}

	private Pin? SelectedPin()
	{
		return _pins.FirstOrDefault(p => IsSelected(p.Item));
	}

	private void UpdateSelectionText()
	{
		Pin? pin = SelectedPin();
		SelectionText.Text = pin == null ? "Click a pin to choose a datacenter" : $"Preferred: {pin.Item.Location} ({pin.Item.PingDisplay})";
	}

	private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		Pin? pin = _pins.FirstOrDefault(p => ReferenceEquals(p.Item, sender));
		if (pin == null)
			return;
		StylePin(pin);
		if (IsSelected(pin.Item))
		{
			UpdateSelectionText();
			Reposition();
		}
	}

	private void Pin_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
		if (sender is not Grid { Tag: Pin pin } || !pin.Item.IsAllowed)
			return;
		SelectedKey = pin.Item.Key;
		Focus();
	}

	private void Reposition()
	{
		foreach (Pin pin in _pins)
		{
			Point p = ToScreen(pin.Item.Lat, pin.Item.Lon);
			Canvas.SetLeft(pin.Element, p.X - RingSize / 2.0);
			Canvas.SetTop(pin.Element, p.Y - RingSize / 2.0);
		}
		Pin? selected = SelectedPin();
		if (selected == null)
		{
			_label.Visibility = Visibility.Collapsed;
			return;
		}
		Point s = ToScreen(selected.Item.Lat, selected.Item.Lon);
		_labelText.Text = selected.Item.Location + ", " + selected.Item.PingDisplay;
		Canvas.SetLeft(_label, s.X + RingSize / 2.0 + 2.0);
		Canvas.SetTop(_label, s.Y - 13.0);
		_label.Visibility = Visibility.Visible;
	}

	private void ApplyView(Matrix m)
	{
		double vw = Surface.ActualWidth;
		double vh = Surface.ActualHeight;
		if (vw <= 0.0 || vh <= 0.0)
			return;
		double scale = m.M11;
		double w = MapWidth * scale;
		double h = MapHeight * scale;
		m.OffsetX = w <= vw ? (vw - w) / 2.0 : Math.Clamp(m.OffsetX, vw - w, 0.0);
		m.OffsetY = h <= vh ? (vh - h) / 2.0 : Math.Clamp(m.OffsetY, vh - h, 0.0);
		_view.Matrix = m;
		LandPath.StrokeThickness = 0.6 / scale;
		BorderPath.StrokeThickness = 0.8 / scale;
		Reposition();
	}

	private void FitWorld()
	{
		double vw = Surface.ActualWidth;
		double vh = Surface.ActualHeight;
		if (vw <= 0.0 || vh <= 0.0)
			return;
		_fitScale = Math.Min(vw / MapWidth, vh / MapHeight);
		_fitted = true;
		ApplyView(new Matrix(_fitScale, 0.0, 0.0, _fitScale, 0.0, 0.0));
	}

	private void ZoomAt(Point at, double factor)
	{
		Matrix m = _view.Matrix;
		double target = Math.Clamp(m.M11 * factor, _fitScale, _fitScale * MaxZoom);
		factor = target / m.M11;
		m.ScaleAt(factor, factor, at.X, at.Y);
		ApplyView(m);
	}

	private void CenterOn(double lat, double lon)
	{
		double scale = Math.Max(_view.Matrix.M11, Math.Min(_fitScale * FocusZoom, _fitScale * MaxZoom));
		Point p = Project(lat, lon);
		ApplyView(new Matrix(scale, 0.0, 0.0, scale, Surface.ActualWidth / 2.0 - p.X * scale, Surface.ActualHeight / 2.0 - p.Y * scale));
	}

	private Point ViewCenter()
	{
		return new Point(Surface.ActualWidth / 2.0, Surface.ActualHeight / 2.0);
	}

	private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		double vw = Surface.ActualWidth;
		double vh = Surface.ActualHeight;
		if (vw <= 0.0 || vh <= 0.0)
			return;
		double fit = Math.Min(vw / MapWidth, vh / MapHeight);
		if (!_fitted || _view.Matrix.M11 <= fit)
		{
			FitWorld();
			return;
		}
		_fitScale = fit;
		ApplyView(_view.Matrix);
	}

	private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_dragging = true;
		_dragMoved = false;
		_dragStart = e.GetPosition(Surface);
		_dragMatrix = _view.Matrix;
		Surface.CaptureMouse();
		Focus();
	}

	private void Viewport_MouseMove(object sender, MouseEventArgs e)
	{
		if (!_dragging)
			return;
		if (e.LeftButton != MouseButtonState.Pressed)
		{
			EndDrag();
			return;
		}
		Vector delta = e.GetPosition(Surface) - _dragStart;
		if (!_dragMoved && delta.Length < 3.0)
			return;
		_dragMoved = true;
		Matrix m = _dragMatrix;
		m.Translate(delta.X, delta.Y);
		ApplyView(m);
	}

	private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		EndDrag();
	}

	private void EndDrag()
	{
		if (!_dragging)
			return;
		_dragging = false;
		Surface.ReleaseMouseCapture();
	}

	private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		if (e.RightButton != MouseButtonState.Pressed)
			return;
		ZoomAt(e.GetPosition(Surface), e.Delta > 0 ? WheelStep : 1.0 / WheelStep);
		e.Handled = true;
	}

	private Pin? FindMatch(string text)
	{
		text = text.Trim();
		if (text.Length == 0)
			return null;
		return _pins.FirstOrDefault(p => p.Item.Location.Contains(text, StringComparison.OrdinalIgnoreCase));
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		Pin? pin = FindMatch(SearchBox.Text);
		if (pin != null)
			CenterOn(pin.Item.Lat, pin.Item.Lon);
	}

	private void SearchBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter)
			return;
		Pin? pin = FindMatch(SearchBox.Text);
		if (pin != null && pin.Item.IsAllowed)
			SelectedKey = pin.Item.Key;
		e.Handled = true;
	}

	private void GoToSelected_Click(object sender, RoutedEventArgs e)
	{
		Pin? pin = SelectedPin();
		if (pin != null)
			CenterOn(pin.Item.Lat, pin.Item.Lon);
	}

	private void ZoomIn_Click(object sender, RoutedEventArgs e)
	{
		ZoomAt(ViewCenter(), ButtonStep);
	}

	private void ZoomOut_Click(object sender, RoutedEventArgs e)
	{
		ZoomAt(ViewCenter(), 1.0 / ButtonStep);
	}
}
