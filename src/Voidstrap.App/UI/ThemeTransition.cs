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
		private Image? _image;

		private bool _cleaned;

		protected override int VisualChildrenCount => (_image != null) ? 1 : 0;

		public FadeAdorner(UIElement adornedElement, BitmapSource snapshot)
			: base(adornedElement)
		{
			_image = new Image
			{
				Source = snapshot,
				Stretch = Stretch.Fill,
				IsHitTestVisible = false
			};
			RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
			IsHitTestVisible = false;
			AddVisualChild(_image);
		}

		protected override Visual? GetVisualChild(int index) => _image;

		protected override Size MeasureOverride(Size constraint)
		{
			if (_image == null)
				return Size.Empty;
			_image.Measure(constraint);
			return AdornedElement.RenderSize;
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			_image?.Arrange(new Rect(new Point(0.0, 0.0), AdornedElement.RenderSize));
			return finalSize;
		}

		public void Play(Duration duration, Action onCompleted)
		{
			if (_cleaned || _image == null)
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
			if (_image != null)
			{
				_image.Source = null;
				RemoveVisualChild(_image);
				_image = null;
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

		BitmapSource? snapshot = TrySnapshot(content);
		if (snapshot == null)
		{
			SafeApply(applyTheme);
			return;
		}

		FadeAdorner adorner;
		try
		{
			adorner = new FadeAdorner(content, snapshot);
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
