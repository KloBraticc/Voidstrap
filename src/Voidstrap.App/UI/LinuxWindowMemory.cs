using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static partial class LinuxWindowMemory
{
	private const string LogIdent = "LinuxWindowMemory";

	private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

	private static readonly MethodInfo? ChangeMouseOverMethod = typeof(MouseDevice).GetMethod("ChangeMouseOver", PrivateInstance);

	private static readonly FieldInfo? MouseOverField = typeof(MouseDevice).GetField("_mouseOver", PrivateInstance);

	private static readonly FieldInfo? PhysicallyOverField = typeof(MouseDevice).GetField("_isPhysicallyOver", PrivateInstance);

	private static readonly FieldInfo? InputSourceField = typeof(MouseDevice).GetField("_inputSource", PrivateInstance);

	private static readonly FieldInfo?[] TreeStateFields =
	[
		typeof(MouseDevice).GetField("_mouseOverTreeState", PrivateInstance),
		typeof(MouseDevice).GetField("_mouseCaptureWithinTreeState", PrivateInstance)
	];

	private static readonly FieldInfo? ChangedVisualsField = typeof(Visual).GetField("VoidstrapChangedVisuals", BindingFlags.Public | BindingFlags.Static);

	private static readonly FieldInfo? ChangedOverflowField = typeof(Visual).GetField("VoidstrapChangedOverflow", BindingFlags.Public | BindingFlags.Static);

	private static int _compactPending;

	private static int _sessionTrimming;

	private static bool _pageOutUnsupported;

	private const int PageOutAdvice = 21;

	private const int InvalidArgument = 22;

	private static readonly TimeSpan SessionTrimInterval = TimeSpan.FromMinutes(3);

	private static readonly TimeSpan FirstSessionTrim = TimeSpan.FromSeconds(20);

	public static void ReleaseAfterClose(Window closed, bool compact)
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		Dispatcher dispatcher = closed.Dispatcher;
		if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
			return;

		try
		{
			dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
			{
				Release(closed);
				if (compact && Interlocked.Exchange(ref _compactPending, 1) == 0)
					_ = CompactAsync(closed.GetType().Name);
			}));
		}
		catch (InvalidOperationException)
		{
		}
	}

	private static void Release(Window closed)
	{
		try
		{
			MouseDevice mouse = Mouse.PrimaryDevice;
			if (MouseOverField?.GetValue(mouse) is DependencyObject over && IsStale(over, closed))
			{
				PhysicallyOverField?.SetValue(mouse, false);
				ChangeMouseOverMethod?.Invoke(mouse, [null, Environment.TickCount]);
			}

			if (InputSourceField?.GetValue(mouse) is PresentationSource source
				&& (source.IsDisposed || source.RootVisual is null || ReferenceEquals(source.RootVisual, closed)))
			{
				InputSourceField.SetValue(mouse, null);
			}

			foreach (FieldInfo? field in TreeStateFields)
			{
				object? state = field?.GetValue(mouse);
				state?.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public)?.Invoke(state, null);
			}

			if (Mouse.Captured is DependencyObject captured && IsStale(captured, closed))
				Mouse.Capture(null);

			if (Keyboard.FocusedElement is DependencyObject focused && IsStale(focused, closed))
				Keyboard.ClearFocus();

			if (ChangedVisualsField?.GetValue(null) is IList changed && changed.Count > 0)
			{
				changed.Clear();
				ChangedOverflowField?.SetValue(null, true);
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The closed " + closed.GetType().Name + " could not be released from input: " + ex.Message);
		}
	}

	private static bool IsStale(DependencyObject element, Window closed)
	{
		Window? owner = Window.GetWindow(element);
		if (ReferenceEquals(owner, closed))
			return true;
		return owner is null && PresentationSource.FromDependencyObject(element) is null;
	}

	private static async Task CompactAsync(string windowName)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
			CollectEverything();
			await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
			CollectEverything();
			TrimWorkingSet();
			App.Logger.WriteLine(LogIdent, $"Closed {windowName}, managed memory {GC.GetTotalMemory(false) / 1048576} MB, resident memory {Environment.WorkingSet / 1048576} MB");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Memory could not be released after closing " + windowName + ": " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _compactPending, 0);
		}
	}

	public static void CompactAfterGame()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || Interlocked.Exchange(ref _compactPending, 1) != 0)
			return;

		_ = Task.Run(async delegate
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
				CollectEverything();
				TrimWorkingSet();
				App.Logger.WriteLine(LogIdent, $"Left the game, managed memory {GC.GetTotalMemory(false) / 1048576} MB, resident memory {Environment.WorkingSet / 1048576} MB");
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogIdent, "Memory could not be released after the game: " + ex.Message);
			}
			finally
			{
				Interlocked.Exchange(ref _compactPending, 0);
			}
		});
	}

	public static void KeepSessionTrimmed(Dispatcher dispatcher, Task sessionEnded)
	{
		if (!Voidstrap.Utility.Platform.IsLinux || Interlocked.Exchange(ref _sessionTrimming, 1) != 0)
			return;

		_ = Task.Run(async delegate
		{
			try
			{
				TimeSpan delay = FirstSessionTrim;
				while (!sessionEnded.IsCompleted)
				{
					await Task.WhenAny(sessionEnded, Task.Delay(delay)).ConfigureAwait(false);
					delay = SessionTrimInterval;
					if (sessionEnded.IsCompleted || dispatcher.HasShutdownStarted)
						break;
					bool windowShown = await dispatcher.InvokeAsync(HasTaskbarWindowShown, DispatcherPriority.ApplicationIdle).Task.ConfigureAwait(false);
					if (windowShown)
						continue;
					TrimNativeHeap();
					TrimWorkingSet();
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogIdent, "Session memory trimming stopped: " + ex.Message);
			}
			finally
			{
				Interlocked.Exchange(ref _sessionTrimming, 0);
			}
		});
	}

	private static bool HasTaskbarWindowShown()
	{
		Application? application = Application.Current;
		if (application is null)
			return false;
		foreach (Window window in application.Windows)
		{
			if (window.IsVisible && window.ShowInTaskbar && window.WindowState != System.Windows.WindowState.Minimized)
				return true;
		}
		return false;
	}

	public static void TrimWorkingSet()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || _pageOutUnsupported)
			return;

		long advised = 0;
		try
		{
			foreach (string line in File.ReadLines("/proc/self/maps"))
			{
				if (!TryParseFileMapping(line, out nint start, out nint end))
					continue;
				if (MAdvise(start, (nuint)(end - start), PageOutAdvice) == 0)
				{
					advised += end - start;
					continue;
				}
				if (Marshal.GetLastPInvokeError() == InvalidArgument && advised == 0)
				{
					_pageOutUnsupported = true;
					App.Logger.WriteLine(LogIdent, "This kernel cannot release unused code pages, the working set stays as is");
					return;
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
		{
			return;
		}
	}

	private static bool TryParseFileMapping(string line, out nint start, out nint end)
	{
		start = 0;
		end = 0;
		string[] parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 6 || parts[1].Length < 4)
			return false;
		string path = parts[5].Trim();
		if (!path.StartsWith('/') || path.StartsWith("/dev/", StringComparison.Ordinal) || path.StartsWith("/memfd:", StringComparison.Ordinal) || path.EndsWith("(deleted)", StringComparison.Ordinal))
			return false;
		if (parts[1][1] == 'w' && parts[1][3] == 's')
			return false;
		int dash = parts[0].IndexOf('-');
		if (dash <= 0)
			return false;
		if (!long.TryParse(parts[0].AsSpan(0, dash), System.Globalization.NumberStyles.HexNumber, null, out long low)
			|| !long.TryParse(parts[0].AsSpan(dash + 1), System.Globalization.NumberStyles.HexNumber, null, out long high)
			|| high <= low)
			return false;
		start = (nint)low;
		end = (nint)high;
		return true;
	}

	private static void CollectEverything()
	{
		GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		GC.WaitForPendingFinalizers();
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		TrimNativeHeap();
	}

	private static void TrimNativeHeap()
	{
		try
		{
			_ = MallocTrim(0);
		}
		catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
		{
		}
	}

	[LibraryImport("libc", EntryPoint = "malloc_trim")]
	private static partial int MallocTrim(nuint pad);

	[LibraryImport("libc", EntryPoint = "madvise", SetLastError = true)]
	private static partial int MAdvise(nint address, nuint length, int advice);
}
