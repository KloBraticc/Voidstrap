using System;
using System.Collections;
using System.Reflection;
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
			GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
			TrimNativeHeap();
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
}
