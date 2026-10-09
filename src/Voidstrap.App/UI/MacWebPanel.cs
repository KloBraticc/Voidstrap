using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.UI;

internal sealed class MacWebPanel : FrameworkElement, IDisposable
{
	private const string ContextMenuBlock = "document.addEventListener('contextmenu', function (e) { e.preventDefault(); }, true);";

	private MacOSWebView? _view;
	private Window? _host;
	private DispatcherTimer? _dragTimer;
	private MacOSWindow.Rect _dragFrame;
	private (double X, double Y) _dragMouse;
	private nint _dragWindow;
	private Rect _lastRect = Rect.Empty;
	private bool _lastVisible;
	private bool _failed;
	private bool _disposed;

	public string? SourceFile { get; set; }

	public string? AccentColor { get; set; }

	public string? ThemeFolder { get; set; }

	public double CornerRadius { get; set; }

	public Voidstrap.UI.Elements.Bootstrapper.CustomDialog? Owner { get; set; }

	public bool AllowDrag { get; set; }

	public static bool IsSupported => Voidstrap.Utility.Platform.IsMacOS;

	public MacWebPanel()
	{
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		LayoutUpdated += OnLayoutUpdated;
		IsVisibleChanged += OnIsVisibleChanged;
	}

	public void Evaluate(string script) => _view?.Evaluate(script);

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		_host = Window.GetWindow(this);
		if (_host != null)
		{
			_host.Activated -= OnHostChanged;
			_host.Activated += OnHostChanged;
			_host.ContentRendered -= OnHostChanged;
			_host.ContentRendered += OnHostChanged;
		}
		Sync();
	}

	private void OnHostChanged(object? sender, EventArgs e) => Sync();

	private void OnLayoutUpdated(object? sender, EventArgs e) => Sync();

	private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Sync();

	private void Sync()
	{
		if (_disposed || _failed || _host == null || !IsLoaded)
			return;
		if (_view == null && !TryCreate())
			return;
		Rect rect = ResolveRect();
		bool visible = IsVisible && _host.IsVisible;
		if (visible == _lastVisible
			&& Math.Abs(rect.X - _lastRect.X) < 0.5d
			&& Math.Abs(rect.Y - _lastRect.Y) < 0.5d
			&& Math.Abs(rect.Width - _lastRect.Width) < 0.5d
			&& Math.Abs(rect.Height - _lastRect.Height) < 0.5d)
			return;
		_lastRect = rect;
		_lastVisible = visible;
		_view!.SetFrame(rect.X, rect.Y, rect.Width, rect.Height, visible);
	}

	private bool TryCreate()
	{
		if (_host == null || string.IsNullOrEmpty(SourceFile) || string.IsNullOrEmpty(ThemeFolder))
			return false;
		nint native = MacWindowMode.ResolveNativeWindow(_host);
		if (native == 0)
			return false;
		try
		{
			string accent = "window.__vsAccentColor = " + System.Text.Json.JsonSerializer.Serialize(AccentColor ?? "#0078d4") + ";";
			string folder = ThemeFolder;
			_view = new MacOSWebView(native, SourceFile, folder,
				[accent, LinuxWebPanel.WebPanelBridge, Voidstrap.UI.Elements.Bootstrapper.CustomDialog.WebPanelApi, ContextMenuBlock],
				CornerRadius,
				uri => IsAllowed(folder, uri));
			_view.MessageReceived += OnMessage;
			_view.NavigationFinished += OnNavigationFinished;
			App.Logger?.WriteLine("MacWebPanel::TryCreate", "Created the web panel for " + SourceFile);
			return true;
		}
		catch (Exception ex)
		{
			_failed = true;
			App.Logger?.WriteException("MacWebPanel::TryCreate", ex);
			return false;
		}
	}

	private static bool IsAllowed(string folder, string uri)
	{
		bool allowed = LinuxWebPanel.IsAllowed(folder, uri);
		if (!allowed)
			App.Logger?.WriteLine("MacWebPanel::IsAllowed", "Blocked navigation to " + uri);
		return allowed;
	}

	private void OnNavigationFinished()
	{
		App.Logger?.WriteLine("MacWebPanel::OnNavigationFinished", "The web panel finished loading");
		Dispatcher.BeginInvoke(new Action(() => Owner?.RequestWebPanelState()));
	}

	private void OnMessage(string message)
	{
		if (message == "voidstrap:cancel")
			Owner?.HandleWebPanelCancel();
		else if (message == "voidstrap:drag" && AllowDrag)
			Dispatcher.BeginInvoke(new Action(BeginDrag));
	}

	private void BeginDrag()
	{
		if (_disposed || _host == null || _dragTimer != null || MacWindowMode.IsMaximized(_host) || !MacOSWebView.IsLeftMouseDown())
			return;
		_dragWindow = MacWindowMode.ResolveNativeWindow(_host);
		if (_dragWindow == 0)
			return;
		_dragFrame = MacOSWindow.GetFrame(_dragWindow);
		_dragMouse = MacOSWindow.MouseLocation();
		_dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
		_dragTimer.Tick += OnDragTick;
		_dragTimer.Start();
	}

	private void OnDragTick(object? sender, EventArgs e)
	{
		if (_disposed || !MacOSWebView.IsLeftMouseDown())
		{
			EndDrag();
			return;
		}
		(double x, double y) = MacOSWindow.MouseLocation();
		MacOSWindow.SetFrame(_dragWindow, new MacOSWindow.Rect(_dragFrame.X + x - _dragMouse.X, _dragFrame.Y + y - _dragMouse.Y, _dragFrame.Width, _dragFrame.Height), false);
	}

	private void EndDrag()
	{
		if (_dragTimer == null)
			return;
		_dragTimer.Stop();
		_dragTimer.Tick -= OnDragTick;
		_dragTimer = null;
		if (_host != null && _dragWindow != 0)
		{
			MacOSWindow.Rect frame = MacOSWindow.GetFrame(_dragWindow);
			_host.Left = frame.X;
			_host.Top = MacOSWindow.PrimaryScreenHeight() - (frame.Y + frame.Height);
		}
	}

	private Rect ResolveRect()
	{
		try
		{
			GeneralTransform transform = TransformToAncestor(_host!);
			return transform.TransformBounds(new Rect(0d, 0d, ActualWidth, ActualHeight));
		}
		catch (Exception)
		{
			return _lastRect;
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

	public void Dispose()
	{
		if (_disposed)
			return;
		EndDrag();
		_disposed = true;
		Loaded -= OnLoaded;
		Unloaded -= OnUnloaded;
		LayoutUpdated -= OnLayoutUpdated;
		IsVisibleChanged -= OnIsVisibleChanged;
		if (_host != null)
		{
			_host.Activated -= OnHostChanged;
			_host.ContentRendered -= OnHostChanged;
			_host = null;
		}
		if (_view != null)
		{
			_view.MessageReceived -= OnMessage;
			_view.NavigationFinished -= OnNavigationFinished;
			_view.Dispose();
			_view = null;
		}
		GC.SuppressFinalize(this);
	}
}
