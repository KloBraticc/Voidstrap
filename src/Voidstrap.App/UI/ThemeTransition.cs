using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class ThemeTransition
{
	private sealed class FadeAdorner : Adorner
	{
		private FrameworkElement? _child;

		private bool _cleaned;

		protected override int VisualChildrenCount => (_child != null) ? 1 : 0;

		public FadeAdorner(UIElement adornedElement, BitmapSource snapshot)
			: base(adornedElement)
		{
			Image image = new Image
			{
				Source = snapshot,
				Stretch = Stretch.Fill,
				IsHitTestVisible = false
			};
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
			_child = image;
			IsHitTestVisible = false;
			AddVisualChild(_child);
		}

		public FadeAdorner(UIElement adornedElement, Brush backdrop)
			: base(adornedElement)
		{
			_child = new Border
			{
				Background = backdrop,
				IsHitTestVisible = false
			};
			IsHitTestVisible = false;
			AddVisualChild(_child);
		}

		protected override Visual? GetVisualChild(int index) => _child;

		protected override Size MeasureOverride(Size constraint)
		{
			if (_child == null)
				return Size.Empty;
			_child.Measure(constraint);
			return AdornedElement.RenderSize;
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			_child?.Arrange(new Rect(new Point(0.0, 0.0), AdornedElement.RenderSize));
			return finalSize;
		}

		public void Play(Duration duration, Action onCompleted)
		{
			if (_cleaned || _child == null)
			{
				onCompleted?.Invoke();
				return;
			}
			DoubleAnimation fade = new DoubleAnimation
			{
				From = 1.0,
				To = 0.0,
				Duration = duration,
				EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
				FillBehavior = FillBehavior.HoldEnd
			};
			fade.Completed += delegate
			{
				onCompleted?.Invoke();
			};
			Wpf.Ui.Animations.RenderReady.Hold(this, duration.HasTimeSpan ? duration.TimeSpan : FadeDuration.TimeSpan);
			BeginAnimation(UIElement.OpacityProperty, fade);
		}

		public void Cleanup()
		{
			if (_cleaned)
				return;
			_cleaned = true;
			try
			{
				BeginAnimation(UIElement.OpacityProperty, null);
			}
			catch
			{
			}
			if (_child is Image image)
			{
				image.Source = null;
			}
			if (_child != null)
			{
				RemoveVisualChild(_child);
				_child = null;
			}
		}
	}

	private const double MaxSnapshotDimension = 1280.0;

	private static readonly Duration FadeDuration = new Duration(TimeSpan.FromMilliseconds(260.0));

	private static AdornerLayer? _activeLayer;

	private static FadeAdorner? _activeAdorner;

	public static void Animate(Window window, Action applyTheme)
	{
		if (applyTheme == null)
			return;

		FinishActiveTransition();

		if (window == null || !window.IsLoaded || window.ActualWidth <= 0.0 || window.ActualHeight <= 0.0 || window.Content is not UIElement content)
		{
			SafeApply(applyTheme);
			return;
		}

		AdornerLayer? layer;
		try
		{
			layer = AdornerLayer.GetAdornerLayer(content);
		}
		catch
		{
			layer = null;
		}
		if (layer == null)
		{
			SafeApply(applyTheme);
			return;
		}

		BitmapSource? snapshot = Voidstrap.Utility.Platform.IsWindows ? TrySnapshot(content) : null;
		Brush? backdrop = snapshot == null ? TryResolveBackdrop(window, content) : null;
		if (snapshot == null && backdrop == null)
		{
			SafeApply(applyTheme);
			return;
		}

		FadeAdorner adorner;
		try
		{
			adorner = snapshot != null
				? new FadeAdorner(content, snapshot)
				: new FadeAdorner(content, backdrop!);
			layer.Add(adorner);
		}
		catch
		{
			SafeApply(applyTheme);
			return;
		}

		SafeApply(applyTheme);

		_activeLayer = layer;
		_activeAdorner = adorner;

		window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
		{
			if (!ReferenceEquals(_activeAdorner, adorner))
				return;
			adorner.Play(FadeDuration, delegate
			{
				if (ReferenceEquals(_activeAdorner, adorner))
				{
					_activeAdorner = null;
					_activeLayer = null;
				}
				RemoveAdorner(layer, adorner);
			});
		}));
	}

	private static void FinishActiveTransition()
	{
		FadeAdorner? adorner = _activeAdorner;
		AdornerLayer? layer = _activeLayer;
		_activeAdorner = null;
		_activeLayer = null;
		if (adorner == null)
			return;
		RemoveAdorner(layer, adorner);
	}

	private static void RemoveAdorner(AdornerLayer? layer, FadeAdorner adorner)
	{
		try
		{
			layer?.Remove(adorner);
		}
		catch
		{
		}
		try
		{
			adorner.Cleanup();
		}
		catch
		{
		}
	}

	private static void SafeApply(Action applyTheme)
	{
		try
		{
			applyTheme();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ThemeTransition::Apply", ex);
		}
	}

	private static SolidColorBrush? TryResolveBackdrop(Window window, UIElement content)
	{
		try
		{
			Color? colour = null;
			if (window.TryFindResource("WindowBackgroundColorPrimary") is Color primary)
				colour = primary;
			if (colour == null && window.TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush application && application.Color.A > 0)
				colour = application.Color;
			if (colour == null && window.Background is SolidColorBrush background && background.Color.A > 0)
				colour = background.Color;
			if (colour == null && content is Panel panel && panel.Background is SolidColorBrush panelBackground && panelBackground.Color.A > 0)
				colour = panelBackground.Color;
			if (colour == null)
				return null;

			SolidColorBrush brush = new SolidColorBrush(Color.FromRgb(colour.Value.R, colour.Value.G, colour.Value.B));
			brush.Freeze();
			return brush;
		}
		catch
		{
			return null;
		}
	}

	private static RenderTargetBitmap? TrySnapshot(UIElement element)
	{
		try
		{
			double width = element.RenderSize.Width;
			double height = element.RenderSize.Height;
			if (width <= 0.0 || height <= 0.0)
				return null;

			double dpiX = 96.0;
			double dpiY = 96.0;
			CompositionTarget? target = PresentationSource.FromVisual(element)?.CompositionTarget;
			if (target != null)
			{
				Matrix toDevice = target.TransformToDevice;
				dpiX = 96.0 * toDevice.M11;
				dpiY = 96.0 * toDevice.M22;
			}

			double scale = 1.0;
			double longest = Math.Max(width * dpiX / 96.0, height * dpiY / 96.0);
			if (longest > MaxSnapshotDimension)
				scale = MaxSnapshotDimension / longest;

			int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpiX / 96.0 * scale));
			int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpiY / 96.0 * scale));

			RenderTargetBitmap bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpiX * scale, dpiY * scale, PixelFormats.Pbgra32);
			bitmap.Render(element);
			bitmap.Freeze();
			return bitmap;
		}
		catch
		{
			return null;
		}
	}
}
