using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.UI;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Mvvm.Contracts;
using Wpf.Ui.Mvvm.Services;

namespace Voidstrap.UI.Elements.Base;

public abstract class WpfUiWindow : UiWindow, IDisposable
{
	private static readonly IThemeService _themeService = new ThemeService();

	private static readonly HashSet<string> SharedStyleDictionaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"Default.xaml",
		"RinUI.xaml",
		"FastFlags.xaml",
		"AnimationsDisabled.xaml"
	};

	private static readonly Dictionary<Voidstrap.Enums.Theme, ResourceDictionary> _builtInThemeCache = new Dictionary<Voidstrap.Enums.Theme, ResourceDictionary>();

	private Voidstrap.Enums.Theme? _lastAppliedTheme;

	private ResourceDictionary? _lastAppliedDict;

	private bool _disposed;

	protected WpfUiWindow()
	{
		Voidstrap.UI.RoundedWindowChrome.Prepare(this);
		ApplyTheme();
	}

	public void ApplyTheme()
	{
		Voidstrap.Enums.Theme final = App.Settings.Prop.Theme2.GetFinal();
		bool flag = final == Voidstrap.Enums.Theme.Custom;
		if (flag || _lastAppliedTheme != final)
		{
			ThemeType theme = ((final != Voidstrap.Enums.Theme.Light) ? ThemeType.Dark : ThemeType.Light);
			try
			{
				_themeService.SetTheme(theme);
				if (Voidstrap.Utility.Platform.IsWindows)
				{
					_themeService.SetSystemAccent();
				}
				else
				{
					Wpf.Ui.Appearance.Accent.Apply(Voidstrap.Utility.SystemAccent.Get(), theme);
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("WpfUiWindow::ApplyTheme", "Wpf.Ui theme service failed: " + ex.Message);
			}
			ResourceDictionary resourceDictionary = null;
			if (flag)
			{
				resourceDictionary = LoadCustomThemeDict();
			}
			if (resourceDictionary == null)
			{
				resourceDictionary = LoadBuiltInThemeDict(final);
			}
			if (resourceDictionary != null)
			{
				ReplaceThemeDictionary(resourceDictionary);
				_lastAppliedTheme = final;
			}
		}
		WindowBackdrop.ApplyThemeToAllOpenWindows();
	}

	private ResourceDictionary? LoadCustomThemeDict()
	{
		try
		{
			return Voidstrap.Utility.CustomTheme.LoadForApp();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("WpfUiWindow::LoadCustomThemeDict", "Custom theme failed, falling back: " + ex.Message);
			return null;
		}
	}

	private static ResourceDictionary? LoadBuiltInThemeDict(Voidstrap.Enums.Theme theme)
	{
		if (_builtInThemeCache.TryGetValue(theme, out ResourceDictionary? cached))
		{
			return cached;
		}
		string text = Enum.GetName(typeof(Voidstrap.Enums.Theme), theme) ?? "Dark";
		ResourceDictionary? loaded = TryLoadStyleDictionary(text);
		if (loaded == null && !string.Equals(text, "Dark", StringComparison.OrdinalIgnoreCase))
		{
			loaded = TryLoadStyleDictionary("Dark");
		}
		if (loaded != null)
		{
			_builtInThemeCache[theme] = loaded;
		}
		return loaded;
	}

	private static ResourceDictionary? TryLoadStyleDictionary(string name)
	{
		try
		{
			return new ResourceDictionary
			{
				Source = new Uri("pack://application:,,,/UI/Style/" + name + ".xaml", UriKind.Absolute)
			};
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("WpfUiWindow::LoadBuiltInThemeDict", "Failed to load " + name + ".xaml: " + ex.Message);
			return null;
		}
	}

	private void ReplaceThemeDictionary(ResourceDictionary newDict)
	{
		if (Application.Current == null)
		{
			return;
		}
		Collection<ResourceDictionary> mergedDictionaries = Application.Current.Resources.MergedDictionaries;
		if (_lastAppliedDict != null && mergedDictionaries.Contains(_lastAppliedDict))
		{
			mergedDictionaries.Remove(_lastAppliedDict);
		}
		for (int num = mergedDictionaries.Count - 1; num >= 0; num--)
		{
			string? text = mergedDictionaries[num].Source?.ToString();
			if (text == null || !text.Contains("/UI/Style/", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (ReferenceEquals(mergedDictionaries[num], newDict))
			{
				continue;
			}
			int slash = text.LastIndexOf('/');
			string fileName = slash >= 0 ? text.Substring(slash + 1) : text;
			if (!SharedStyleDictionaries.Contains(fileName))
			{
				mergedDictionaries.RemoveAt(num);
			}
		}
		mergedDictionaries.Add(newDict);
		_lastAppliedDict = newDict;
	}

	[System.Runtime.InteropServices.DllImport("gdi32.dll")]
	private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

	[System.Runtime.InteropServices.DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr hObject);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo lpmi);

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct NativePoint
	{
		public int X;

		public int Y;
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct NativeMonitorInfo
	{
		public int Size;

		public NativeRect Monitor;

		public NativeRect Work;

		public int Flags;
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct NativeMinMaxInfo
	{
		public NativePoint Reserved;

		public NativePoint MaxSize;

		public NativePoint MaxPosition;

		public NativePoint MinTrackSize;

		public NativePoint MaxTrackSize;
	}

	private const int WmGetMinMaxInfo = 36;

	private HwndSource? _hwndSource;

	private static readonly bool IsWindows11OrNewer = Voidstrap.Utility.Platform.IsWindows && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		if (Icon == null)
		{
			try
			{
				Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute));
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("WpfUiWindow::OnSourceInitialized", "Failed to set window icon: " + ex.Message);
			}
		}
		if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
		{
			if (ResizeMode == ResizeMode.CanResize || ResizeMode == ResizeMode.CanResizeWithGrip)
			{
				_hwndSource = hwndSource;
				hwndSource.AddHook(WindowProc);
			}
		}
		ApplyRoundedCorners();
		Voidstrap.UI.WindowBackdrop.Apply(this);
	}

	protected override void OnActivated(EventArgs e)
	{
		base.OnActivated(e);
		Voidstrap.Utility.MemoryManager.SetActive();
	}

	protected override void OnDeactivated(EventArgs e)
	{
		base.OnDeactivated(e);
		Voidstrap.Utility.MemoryManager.SetBackground();
	}

	private void ApplyRoundedCorners()
	{
		try
		{
			if (IsWindows11OrNewer)
			{
				Wpf.Ui.Interop.UnsafeNativeMethods.ApplyWindowCornerPreference(this, WindowCornerPreference.Round);
				return;
			}
			if (AllowsTransparency)
			{
				return;
			}
			ApplyWin10RoundRegion();
			SizeChanged += OnRoundRegionSizeChanged;
			StateChanged += OnRoundRegionStateChanged;
			DpiChanged += OnRoundRegionDpiChanged;
			IsVisibleChanged += OnRoundRegionVisibilityChanged;
		}
		catch
		{
		}
	}

	private void ApplyWin10RoundRegion()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		if (PresentationSource.FromVisual(this) is not HwndSource hwndSource)
		{
			return;
		}
		IntPtr handle = hwndSource.Handle;
		if (handle == IntPtr.Zero)
		{
			return;
		}
		if (Voidstrap.UI.WindowBackdrop.HasBackdrop(this))
		{
			hwndSource.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
		}
		else
		{
			System.Windows.Media.Color surface = Voidstrap.UI.WindowBackdrop.CreateSurfaceColor();
			surface.A = byte.MaxValue;
			hwndSource.CompositionTarget.BackgroundColor = surface;
		}
		if (base.WindowState == System.Windows.WindowState.Maximized)
		{
			SetWindowRgn(handle, IntPtr.Zero, bRedraw: true);
			return;
		}
		System.Windows.Media.Matrix m = hwndSource.CompositionTarget.TransformToDevice;
		int w = (int)Math.Ceiling(base.ActualWidth * m.M11);
		int h = (int)Math.Ceiling(base.ActualHeight * m.M22);
		if (w > 0 && h > 0)
		{
			int r = Math.Max(2, (int)Math.Round(16.0 * m.M11));
			IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r);
			if (rgn != IntPtr.Zero)
			{
				if (SetWindowRgn(handle, rgn, bRedraw: true) == 0)
				{
					DeleteObject(rgn);
				}
			}
		}
	}

	private void OnRoundRegionDpiChanged(object? sender, DpiChangedEventArgs e)
	{
		try
		{
			ApplyWin10RoundRegion();
		}
		catch
		{
		}
	}

	private void OnRoundRegionVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
	{
		if (e.NewValue is not true)
		{
			return;
		}
		try
		{
			ApplyWin10RoundRegion();
		}
		catch
		{
		}
	}

	private void OnRoundRegionSizeChanged(object sender, SizeChangedEventArgs e)
	{
		try
		{
			ApplyWin10RoundRegion();
		}
		catch
		{
		}
	}

	private void OnRoundRegionStateChanged(object? sender, EventArgs e)
	{
		try
		{
			ApplyWin10RoundRegion();
		}
		catch
		{
		}
	}

	private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WmGetMinMaxInfo && Voidstrap.Utility.Platform.IsWindows)
		{
			IntPtr monitor = MonitorFromWindow(hwnd, 2u);
			if (monitor != IntPtr.Zero)
			{
				NativeMonitorInfo mi = default(NativeMonitorInfo);
				mi.Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMonitorInfo>();
				if (GetMonitorInfo(monitor, ref mi))
				{
					NativeMinMaxInfo mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMinMaxInfo>(lParam);
					mmi.MaxPosition.X = mi.Work.Left - mi.Monitor.Left;
					mmi.MaxPosition.Y = mi.Work.Top - mi.Monitor.Top;
					mmi.MaxSize.X = mi.Work.Right - mi.Work.Left;
					mmi.MaxSize.Y = mi.Work.Bottom - mi.Work.Top;
					double minW = MinWidth > 0 ? MinWidth : 800.0;
					double minH = MinHeight > 0 ? MinHeight : 500.0;
					System.Windows.Media.Matrix matrix = _hwndSource?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
					mmi.MinTrackSize.X = (int)Math.Ceiling(minW * matrix.M11);
					mmi.MinTrackSize.Y = (int)Math.Ceiling(minH * matrix.M22);
					System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
					handled = true;
				}
			}
		}
		return IntPtr.Zero;
	}

	protected override void OnClosed(EventArgs e)
	{
		SizeChanged -= OnRoundRegionSizeChanged;
		StateChanged -= OnRoundRegionStateChanged;
		DpiChanged -= OnRoundRegionDpiChanged;
		IsVisibleChanged -= OnRoundRegionVisibilityChanged;
		if (_hwndSource != null)
		{
			_hwndSource.RemoveHook(WindowProc);
			_hwndSource = null;
		}
		Dispose();
		base.OnClosed(e);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		SizeChanged -= OnRoundRegionSizeChanged;
		StateChanged -= OnRoundRegionStateChanged;
		DpiChanged -= OnRoundRegionDpiChanged;
		IsVisibleChanged -= OnRoundRegionVisibilityChanged;
		if (_hwndSource != null)
		{
			_hwndSource.RemoveHook(WindowProc);
			_hwndSource = null;
		}
		_lastAppliedDict = null;
		GC.SuppressFinalize(this);
	}
}
