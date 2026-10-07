using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxWebImageSource
{
	private const string LogIdent = "LinuxWebImageSource";

	private static bool _installed;

	private static readonly HashSet<DrawingImage> _presented = new(ReferenceEqualityComparer.Instance);

	private static bool _refreshQueued;

	private static readonly ConditionalWeakTable<Border, BrushHostState> BrushHosts = new();
	private static readonly DependencyPropertyDescriptor? BackgroundDescriptor = DependencyPropertyDescriptor.FromProperty(Border.BackgroundProperty, typeof(Border));

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.UsesPortableUi)
			return;

		_installed = true;
		EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnBorderLoaded));
		EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnBorderUnloaded));
		try
		{
			object? context = typeof(XamlReader).GetProperty("BamlSharedSchemaContext", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
			MethodInfo? lookup = context?.GetType().GetMethod("GetKnownXamlType", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
			object? knownType = lookup?.Invoke(context, new object[] { typeof(ImageSource) });
			object? converter = knownType?.GetType().GetProperty("TypeConverter", BindingFlags.Public | BindingFlags.Instance)?.GetValue(knownType);
			FieldInfo? instance = converter?.GetType().GetField("_instance", BindingFlags.NonPublic | BindingFlags.Instance);
			FieldInfo? instanceSet = converter?.GetType().GetField("_instanceIsSet", BindingFlags.NonPublic | BindingFlags.Instance);
			if (instance == null || instanceSet == null)
			{
				App.Logger.WriteLine(LogIdent, "The image source converter could not be located, web images keep loading on the interface thread");
				return;
			}

			instance.SetValue(converter, new Converter());
			instanceSet.SetValue(converter, true);
			App.Logger.WriteLine(LogIdent, "Web images in bindings now load in the background");
		}
		catch (Exception ex)
		{
			App.Logger.WriteException(LogIdent, ex);
		}
	}

	private static bool TryGetWebUri(object? value, out Uri uri)
	{
		uri = null!;
		Uri? candidate = value switch
		{
			Uri direct => direct,
			string text when text.Length > 0 && Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed) => parsed,
			_ => null
		};
		if (candidate == null || !candidate.IsAbsoluteUri)
			return false;
		if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return false;
		uri = candidate;
		return true;
	}

	private static ImageSource CreateDeferred(Uri uri)
	{
		DrawingGroup content = new DrawingGroup();
		DrawingImage image = new DrawingImage(content);
		Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
		Task<BitmapSource?> load = Voidstrap.Utility.DynamicRenderSystem.LoadWebImageAsync(uri.AbsoluteUri);
		if (load.IsCompletedSuccessfully)
		{
			if (Present(image, content, load.Result))
				QueueLayoutRefresh(dispatcher);
			return image;
		}

		load.ContinueWith(task =>
		{
			BitmapSource? bitmap = task.IsCompletedSuccessfully ? task.Result : null;
			if (bitmap == null || dispatcher.HasShutdownStarted)
				return;
			dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
			{
				if (Present(image, content, bitmap))
					QueueLayoutRefresh(dispatcher);
			}));
		}, TaskScheduler.Default);
		return image;
	}

	private static bool Present(DrawingImage image, DrawingGroup content, BitmapSource? bitmap)
	{
		if (bitmap == null || content.IsFrozen || bitmap.Width <= 0 || bitmap.Height <= 0)
			return false;
		content.Children.Clear();
		content.Children.Add(new ImageDrawing(bitmap, new Rect(0, 0, bitmap.Width, bitmap.Height)));
		_presented.Add(image);
		return true;
	}

	private static void QueueLayoutRefresh(Dispatcher dispatcher)
	{
		if (_refreshQueued)
			return;
		_refreshQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshPresentedImages));
	}

	private static void RefreshPresentedImages()
	{
		_refreshQueued = false;
		if (_presented.Count == 0)
			return;
		try
		{
			Application? application = Application.Current;
			if (application != null)
			{
				foreach (Window window in application.Windows)
					InvalidatePresented(window);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Loaded web images could not update their layout: " + ex.Message);
		}
		finally
		{
			_presented.Clear();
		}
	}

	private static void InvalidatePresented(DependencyObject element)
	{
		if (element is Image target && target.Source is DrawingImage source && _presented.Contains(source))
			target.InvalidateMeasure();
		if (element is Border border && BrushHosts.TryGetValue(border, out BrushHostState? state))
			state.Present();
		else if (element is Panel)
			UpdatePresentedBrush(element, Panel.BackgroundProperty);
		else if (element is Control)
			UpdatePresentedBrush(element, Control.BackgroundProperty);
		if (element is System.Windows.Shapes.Shape)
		{
			UpdatePresentedBrush(element, System.Windows.Shapes.Shape.FillProperty);
			UpdatePresentedBrush(element, System.Windows.Shapes.Shape.StrokeProperty);
		}
		int count = VisualTreeHelper.GetChildrenCount(element);
		for (int index = 0; index < count; index++)
			InvalidatePresented(VisualTreeHelper.GetChild(element, index));
	}

	private static void UpdatePresentedBrush(DependencyObject element, DependencyProperty property)
	{
		if (element.GetValue(property) is not ImageBrush brush
			|| brush.ImageSource is not DrawingImage drawing
			|| !_presented.Contains(drawing)
			|| drawing.Drawing is not DrawingGroup group
			|| group.Children.Count != 1
			|| group.Children[0] is not ImageDrawing { ImageSource: BitmapSource bitmap })
			return;
		if (brush.IsFrozen)
		{
			ImageBrush replacement = brush.CloneCurrentValue();
			replacement.ImageSource = bitmap;
			element.SetCurrentValue(property, replacement);
		}
		else
		{
			brush.SetCurrentValue(ImageBrush.ImageSourceProperty, bitmap);
		}
	}

	private static void OnBorderLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is Border { Background: ImageBrush } border)
			BrushHosts.GetValue(border, CreateBrushHost).Attach();
	}

	private static BrushHostState CreateBrushHost(Border border) => new(border);

	private static void OnBorderUnloaded(object sender, RoutedEventArgs e)
	{
		if (sender is Border border && BrushHosts.TryGetValue(border, out BrushHostState? state))
			state.Detach();
	}

	private sealed class BrushHostState
	{
		private readonly Border _host;
		private ImageBrush? _brush;
		private bool _listening;

		public BrushHostState(Border host) => _host = host;

		public void Attach()
		{
			if (_listening)
				return;
			_listening = true;
			BackgroundDescriptor?.AddValueChanged(_host, OnBackgroundChanged);
			Update();
		}

		public void Detach()
		{
			if (!_listening)
				return;
			_listening = false;
			BackgroundDescriptor?.RemoveValueChanged(_host, OnBackgroundChanged);
			if (_brush is { IsFrozen: false })
				_brush.Changed -= OnBrushChanged;
			if (_brush != null)
				Voidstrap.Utility.DynamicRenderSystem.PresentPortableBrushImage(_host, _brush, null);
			_brush = null;
		}

		private void OnBackgroundChanged(object? sender, EventArgs e) => Update();

		private void OnBrushChanged(object? sender, EventArgs e) => Present();

		private void Update()
		{
			ImageBrush? next = _host.Background as ImageBrush;
			if (!ReferenceEquals(_brush, next))
			{
				if (_brush is { IsFrozen: false })
					_brush.Changed -= OnBrushChanged;
				if (next == null && _brush != null)
					Voidstrap.Utility.DynamicRenderSystem.PresentPortableBrushImage(_host, _brush, null);
				_brush = next;
				if (_brush is { IsFrozen: false })
					_brush.Changed += OnBrushChanged;
			}
			Present();
		}

		public void Present()
		{
			if (_brush == null)
				return;
			ImageSource? source = _brush.ImageSource;
			if (source is DrawingImage { Drawing: DrawingGroup { Children.Count: 1 } group } && group.Children[0] is ImageDrawing drawing)
				source = drawing.ImageSource;
			Voidstrap.Utility.DynamicRenderSystem.PresentPortableBrushImage(_host, _brush, source as BitmapSource);
		}
	}

	private sealed class Converter : ImageSourceConverter
	{
		public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
		{
			if (TryGetWebUri(value, out Uri uri))
				return CreateDeferred(uri);
			return base.ConvertFrom(context, culture, value);
		}
	}
}
