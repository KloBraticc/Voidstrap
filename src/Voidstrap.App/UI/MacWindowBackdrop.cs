using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Voidstrap.Enums;
using Voidstrap.Models;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.UI;

internal sealed class MacWindowBackdrop : IDisposable
{
	private static readonly ConditionalWeakTable<Window, MacWindowBackdrop> Windows = new();
	private readonly Window _window;
	private MacOSWindowBackdrop? _native;
	private bool _disposed;
	private string? _logged;
#if CROSSPLAT
	private System.Numerics.Vector4? _clearColor;
#endif

	private MacWindowBackdrop(Window window)
	{
		_window = window;
		window.Loaded += OnLoaded;
		window.ContentRendered += OnReady;
		window.Activated += OnReady;
		window.Closed += OnClosed;
		window.SizeChanged += OnSizeChanged;
	}

	internal static void Apply(Window window)
	{
		if (Voidstrap.Integrations.Overlays.LinuxOverlaySurface.IsOverlayWindow(window))
			return;
		Windows.GetValue(window, Create).Update();
	}

	private static MacWindowBackdrop Create(Window window) => new(window);

	private void OnLoaded(object sender, RoutedEventArgs e) => Update();

	private void OnReady(object? sender, EventArgs e) => Update();

	private void OnClosed(object? sender, EventArgs e) => Dispose();

	private void OnSizeChanged(object sender, SizeChangedEventArgs e) => _window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(Update));

	private void Update()
	{
		if (_disposed)
			return;
		BackdropType type = WindowBackdrop.ResolveFor(_window);
		bool enabled = type != BackdropType.None && !SystemParameters.HighContrast;
		nint window = MacWindowMode.ResolveNativeWindow(_window);
		if (enabled && window != 0)
		{
			try
			{
				if (_native == null)
				{
					_native = new MacOSWindowBackdrop(window);
					App.Logger?.WriteLine("MacWindowBackdrop::Update", "Native state before the backdrop: " + _native.Describe());
				}
				int material = type switch
				{
					BackdropType.Sidebar or BackdropType.Mica => 7,
					BackdropType.Popover or BackdropType.Acrylic => 6,
					BackdropType.Hud or BackdropType.Aero => 13,
					BackdropType.UnderWindow or BackdropType.MicaAlt => 21,
					BackdropType.UnderPage => 22,
					_ => 18
				};
				_native.Apply(material, App.Settings.Prop.Theme2.GetFinal() != Theme.Light);
			}
			catch (Exception ex)
			{
				_native?.Dispose();
				_native = null;
				App.Logger?.WriteException("MacWindowBackdrop::Apply", ex);
			}
		}
		else
		{
			_native?.Dispose();
			_native = null;
		}
		bool active = enabled && _native != null;
		ApplyClearColor(active);
		string state = type + (window == 0 ? " no native window" : active ? " active" : " inactive");
		if (state != _logged)
		{
			_logged = state;
			App.Logger?.WriteLine("MacWindowBackdrop::Update", _window.GetType().Name + " backdrop " + state + (_native != null ? ", " + _native.Describe() : ""));
		}
		_window.Background = active
			? _window is Elements.Settings.MainWindow ? Brushes.Transparent : WindowBackdrop.CreateSurfaceBrush(_window)
			: WindowBackdrop.CreateOpaqueSurfaceBrush(_window);
		if (_window is Elements.Settings.MainWindow main)
			main.ApplyBackdropSurface();
		_window.InvalidateVisual();
	}

	private void ApplyClearColor(bool transparent)
	{
#if CROSSPLAT
		if (!System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(_window, out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host)
			|| host?.CompositionTarget?.Compositor is not { } compositor)
			return;
		_clearColor ??= compositor.ClearColor;
		System.Numerics.Vector4 target = transparent ? System.Numerics.Vector4.Zero : _clearColor.Value;
		if (compositor.ClearColor != target)
			compositor.ClearColor = target;
#endif
	}

	internal static bool IsActive(Window window) => Windows.TryGetValue(window, out MacWindowBackdrop? state) && state._native != null;

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_window.Loaded -= OnLoaded;
		_window.ContentRendered -= OnReady;
		_window.Activated -= OnReady;
		_window.Closed -= OnClosed;
		_window.SizeChanged -= OnSizeChanged;
		_native?.Dispose();
		_native = null;
		Windows.Remove(_window);
		GC.SuppressFinalize(this);
	}
}
