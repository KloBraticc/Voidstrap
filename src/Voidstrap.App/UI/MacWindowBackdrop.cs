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

	private MacWindowBackdrop(Window window)
	{
		_window = window;
		window.Loaded += OnLoaded;
		window.ContentRendered += OnReady;
		window.Activated += OnReady;
		window.Closed += OnClosed;
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
				_native ??= new MacOSWindowBackdrop(window);
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
		_window.Background = active
			? _window is Elements.Settings.MainWindow ? Brushes.Transparent : WindowBackdrop.CreateSurfaceBrush(_window)
			: WindowBackdrop.CreateOpaqueSurfaceBrush(_window);
		if (_window is Elements.Settings.MainWindow main)
			main.ApplyBackdropSurface();
		_window.InvalidateVisual();
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
		_native?.Dispose();
		_native = null;
		Windows.Remove(_window);
		GC.SuppressFinalize(this);
	}
}
