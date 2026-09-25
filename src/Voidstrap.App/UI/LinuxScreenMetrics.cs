using System;
using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxScreenMetrics
{
	private static readonly object Sync = new();

	private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

	private static DispatcherTimer? _watchdog;

	private static bool _applied;

	private static int _width;

	private static int _height;

	private static int _workLeft;

	private static int _workTop;

	private static int _workWidth;

	private static int _workHeight;

	public static int Width => _width;

	public static int Height => _height;

	public static Rect WorkArea => new(_workLeft, _workTop, _workWidth, _workHeight);

	public static void Apply()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		ApplyCore(false);
		StartWatchdog();
	}

	public static bool Refresh() => Voidstrap.Utility.Platform.IsLinux && ApplyCore(true);

	private static void StartWatchdog()
	{
		if (_watchdog is not null || Application.Current is null)
			return;

		_watchdog = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
		{
			Interval = WatchInterval
		};
		_watchdog.Tick += OnWatchdogTick;
		_watchdog.Start();
		Application.Current.Exit += OnApplicationExit;
	}

	private static void OnWatchdogTick(object? sender, EventArgs args) => ApplyCore(false);

	private static void OnApplicationExit(object sender, ExitEventArgs args)
	{
		if (Application.Current is not null)
			Application.Current.Exit -= OnApplicationExit;

		if (_watchdog is null)
			return;

		_watchdog.Stop();
		_watchdog.Tick -= OnWatchdogTick;
		_watchdog = null;
	}

	private static bool ApplyCore(bool force)
	{
		if (!TryReadLayout(out int width, out int height, out int workLeft, out int workTop, out int workWidth, out int workHeight))
		{
			if (!_applied)
				App.Logger?.WriteLine("LinuxScreenMetrics::Apply", "The display size could not be read, the built in defaults stay in place");

			return false;
		}

		lock (Sync)
		{
			bool unchanged = _applied
				&& width == _width
				&& height == _height
				&& workLeft == _workLeft
				&& workTop == _workTop
				&& workWidth == _workWidth
				&& workHeight == _workHeight;

			if (unchanged && !force)
				return false;

			if (!Publish(width, height, workLeft, workTop, workWidth, workHeight))
				return false;

			_width = width;
			_height = height;
			_workLeft = workLeft;
			_workTop = workTop;
			_workWidth = workWidth;
			_workHeight = workHeight;
			_applied = true;
		}

		Voidstrap.Platform.Linux.LinuxDisplayMetrics.Invalidate();
		App.Logger?.WriteLine(
			"LinuxScreenMetrics::Apply",
			$"Reported a display of {width}x{height} with a work area of {workWidth}x{workHeight} at {workLeft},{workTop}");
		return true;
	}

	private static bool TryReadLayout(out int width, out int height, out int workLeft, out int workTop, out int workWidth, out int workHeight)
	{
		workLeft = 0;
		workTop = 0;
		workWidth = 0;
		workHeight = 0;

		if (Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetPrimaryScreen(out _, out _, out width, out height, out workLeft, out workTop, out workWidth, out workHeight)
			&& width > 0
			&& height > 0
			&& workWidth > 0
			&& workHeight > 0)
			return true;

		if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetScreenBounds(out width, out height) || width <= 0 || height <= 0)
			return false;

		if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWorkArea(out workLeft, out workTop, out workWidth, out workHeight)
			|| workWidth <= 0
			|| workHeight <= 0)
		{
			workLeft = 0;
			workTop = 0;
			workWidth = width;
			workHeight = height;
		}

		workLeft = Math.Clamp(workLeft, 0, Math.Max(0, width - 1));
		workTop = Math.Clamp(workTop, 0, Math.Max(0, height - 1));
		workWidth = Math.Clamp(workWidth, 1, width - workLeft);
		workHeight = Math.Clamp(workHeight, 1, height - workTop);
		return true;
	}

	private static bool Publish(int width, int height, int workLeft, int workTop, int workWidth, int workHeight)
	{
		try
		{
			Type type = typeof(SystemParameters);
			Type? slotType = type.GetNestedType("CacheSlot", BindingFlags.NonPublic | BindingFlags.Public);
			BitArray? cacheValid = type
				.GetField("_cacheValid", BindingFlags.NonPublic | BindingFlags.Static)?
				.GetValue(null) as BitArray;

			if (slotType == null || cacheValid == null)
			{
				App.Logger?.WriteLine("LinuxScreenMetrics::Apply", "The screen metrics could not be located");
				return false;
			}

			lock (cacheValid)
			{
				SetDouble(type, slotType, cacheValid, "_primaryScreenWidth", "PrimaryScreenWidth", width);
				SetDouble(type, slotType, cacheValid, "_primaryScreenHeight", "PrimaryScreenHeight", height);
				SetDouble(type, slotType, cacheValid, "_virtualScreenWidth", "VirtualScreenWidth", width);
				SetDouble(type, slotType, cacheValid, "_virtualScreenHeight", "VirtualScreenHeight", height);
				SetDouble(type, slotType, cacheValid, "_virtualScreenLeft", "VirtualScreenLeft", 0);
				SetDouble(type, slotType, cacheValid, "_virtualScreenTop", "VirtualScreenTop", 0);
				SetDouble(type, slotType, cacheValid, "_maximizedPrimaryScreenWidth", "MaximizedPrimaryScreenWidth", workWidth);
				SetDouble(type, slotType, cacheValid, "_maximizedPrimaryScreenHeight", "MaximizedPrimaryScreenHeight", workHeight);
				SetDouble(type, slotType, cacheValid, "_fullPrimaryScreenWidth", "FullPrimaryScreenWidth", workWidth);
				SetDouble(type, slotType, cacheValid, "_fullPrimaryScreenHeight", "FullPrimaryScreenHeight", workHeight);
				SetWorkArea(type, slotType, cacheValid, workLeft, workTop, workWidth, workHeight);
			}

			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxScreenMetrics::Apply", $"The screen metrics could not be applied: {ex.Message}");
			return false;
		}
	}

	private static void SetDouble(Type type, Type slotType, BitArray cacheValid, string field, string slot, double value)
	{
		FieldInfo? backing = type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static);

		if (backing == null || backing.FieldType != typeof(double))
			return;

		backing.SetValue(null, value);
		MarkValid(slotType, cacheValid, slot);
	}

	private static void SetWorkArea(Type type, Type slotType, BitArray cacheValid, int left, int top, int width, int height)
	{
		FieldInfo? internalField = type.GetField("_workAreaInternal", BindingFlags.NonPublic | BindingFlags.Static);

		if (internalField != null)
		{
			object? rect = Activator.CreateInstance(internalField.FieldType, left, top, left + width, top + height);

			if (rect != null)
			{
				internalField.SetValue(null, rect);
				MarkValid(slotType, cacheValid, "WorkAreaInternal");
			}
		}

		FieldInfo? workArea = type.GetField("_workArea", BindingFlags.NonPublic | BindingFlags.Static);

		if (workArea != null && workArea.FieldType == typeof(Rect))
		{
			workArea.SetValue(null, new Rect(left, top, width, height));
			MarkValid(slotType, cacheValid, "WorkArea");
		}
	}

	private static void MarkValid(Type slotType, BitArray cacheValid, string slot)
	{
		if (!Enum.TryParse(slotType, slot, out object? parsed) || parsed == null)
			return;

		int index = Convert.ToInt32(parsed);

		if (index >= 0 && index < cacheValid.Length)
			cacheValid[index] = true;
	}
}
