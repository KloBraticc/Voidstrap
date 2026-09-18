using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Voidstrap.UI;

public static class LinuxComboBoxGuard
{
	private static readonly ConditionalWeakTable<ComboBox, Lifecycle> Lifecycles = new();
	private static bool _installed;

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.IsLinux)
			return;

		_installed = true;
		EventManager.RegisterClassHandler(
			typeof(ComboBox),
			FrameworkElement.LoadedEvent,
			new RoutedEventHandler(OnLoaded),
			true);
		EventManager.RegisterClassHandler(
			typeof(ComboBox),
			FrameworkElement.UnloadedEvent,
			new RoutedEventHandler(OnUnloaded),
			true);
	}

	private static void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not ComboBox box)
			return;

		Lifecycle lifecycle = Lifecycles.GetValue(box, static value => new Lifecycle(value));
		lifecycle.Attach();
	}

	private static void OnUnloaded(object sender, RoutedEventArgs e)
	{
		if (sender is not ComboBox box || !Lifecycles.TryGetValue(box, out Lifecycle? lifecycle))
			return;

		lifecycle.Detach();
		Lifecycles.Remove(box);
	}

	private sealed class Lifecycle
	{
		private static readonly DependencyPropertyDescriptor? OwnerWindowStateDescriptor =
			DependencyPropertyDescriptor.FromProperty(Window.WindowStateProperty, typeof(Window));

		private const int MaxPresentationRepairs = 2;
		private const int MaxLogicalRecoveries = 3;
		private static readonly TimeSpan PresentationTimeout = TimeSpan.FromMilliseconds(500);
		private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(2);

		private readonly ComboBox _box;
		private Popup? _popup;
		private DispatcherTimer? _watchdog;
		private int _watchdogGeneration;
		private int _generation;
		private int _repairCount;
		private int _recoveryCount;
		private long _lastReport;
		private string _closeReason = "none";
		private bool _requestedOpen;
		private bool _closeRequested;
		private bool _logicalRecoveryQueued;
		private bool _presented;
		private bool _attached;
		private Window? _owner;

		internal Lifecycle(ComboBox box)
		{
			_box = box;
		}

		internal void Attach()
		{
			if (_attached)
				return;

			_attached = true;
			_box.DropDownOpened += OnDropDownOpened;
			_box.DropDownClosed += OnDropDownClosed;
			_box.PreviewKeyDown += OnPreviewKeyDown;
			_box.SelectionChanged += OnSelectionChanged;
			_box.LostMouseCapture += OnLostMouseCapture;
			_box.IsVisibleChanged += OnIsVisibleChanged;
			_owner = Window.GetWindow(_box);
			if (_owner is not null)
			{
				_owner.Deactivated += OnOwnerDeactivated;
				_owner.StateChanged += OnOwnerStateChanged;
				OwnerWindowStateDescriptor?.AddValueChanged(_owner, OnOwnerStateChanged);
			}
			BindPopup();
			if (_box.IsDropDownOpen)
				BeginOpen();
		}

		internal void Detach()
		{
			if (!_attached)
				return;

			_attached = false;
			_generation++;
			StopWatchdog();
			_box.DropDownOpened -= OnDropDownOpened;
			_box.DropDownClosed -= OnDropDownClosed;
			_box.PreviewKeyDown -= OnPreviewKeyDown;
			_box.SelectionChanged -= OnSelectionChanged;
			_box.LostMouseCapture -= OnLostMouseCapture;
			_box.IsVisibleChanged -= OnIsVisibleChanged;
			if (_owner is not null)
			{
				_owner.Deactivated -= OnOwnerDeactivated;
				_owner.StateChanged -= OnOwnerStateChanged;
				OwnerWindowStateDescriptor?.RemoveValueChanged(_owner, OnOwnerStateChanged);
				_owner = null;
			}
			UnbindPopup();
		}

		private void BindPopup()
		{
			_box.ApplyTemplate();
			Popup? popup;
			try
			{
				popup = _box.Template?.FindName("Popup", _box) as Popup;
			}
			catch (InvalidOperationException)
			{
				return;
			}

			if (ReferenceEquals(_popup, popup))
				return;

			UnbindPopup();
			_popup = popup;
			if (_popup is null)
				return;

			_popup.StaysOpen = true;
			_popup.Opened += OnPopupOpened;
			_popup.Closed += OnPopupClosed;
		}

		private void UnbindPopup()
		{
			if (_popup is null)
				return;

			_popup.Opened -= OnPopupOpened;
			_popup.Closed -= OnPopupClosed;
			_popup = null;
		}

		private void OnDropDownOpened(object? sender, EventArgs e)
		{
			BeginOpen();
		}

		private void BeginOpen()
		{
			_generation++;
			_repairCount = 0;
			_closeReason = "none";
			_requestedOpen = true;
			_closeRequested = false;
			_logicalRecoveryQueued = false;
			_presented = false;
			BindPopup();
			RestoreInput();
			EnsureCapture();
			ArmWatchdog(_generation);
			QueueVerification(_generation, DispatcherPriority.Render);
		}

		private void OnDropDownClosed(object? sender, EventArgs e)
		{
			if (_requestedOpen
				&& !_closeRequested
				&& _popup is { IsOpen: false }
				&& CanPresent()
				&& _recoveryCount < MaxLogicalRecoveries)
			{
				_closeReason = "unexpected-surface-close";
				QueueLogicalRecovery();
				return;
			}

			_requestedOpen = false;
			_presented = false;
			_closeReason = "logical-close";
			_generation++;
			_repairCount = 0;
			StopWatchdog();
		}

		private void OnPopupOpened(object? sender, EventArgs e)
		{
			if (!_box.IsDropDownOpen)
				return;

			QueueVerification(_generation, DispatcherPriority.Input);
		}

		private void OnPopupClosed(object? sender, EventArgs e)
		{
			if (!_box.IsDropDownOpen)
			{
				if (_requestedOpen && !_closeRequested && CanPresent() && _recoveryCount < MaxLogicalRecoveries)
					QueueLogicalRecovery();
				return;
			}

			_closeReason = "unexpected-surface-close";
			QueueVerification(_generation, DispatcherPriority.Input);
		}

		private void OnPreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (!_box.IsDropDownOpen)
				return;

			bool altNavigation = e.Key is Key.Up or Key.Down
				&& (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

			if (e.Key is Key.Escape or Key.Enter or Key.Tab || altNavigation)
			{
				_closeRequested = true;
				_closeReason = "keyboard-close";
			}
		}

		private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (!_box.IsDropDownOpen)
				return;

			_closeRequested = true;
			_closeReason = "selection-close";
		}

		private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (e.NewValue is true)
				return;

			_closeRequested = true;
			_closeReason = "visibility-close";
			_presented = false;
			StopWatchdog();

			if (_box.IsDropDownOpen)
				_box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
		}

		private void OnLostMouseCapture(object sender, MouseEventArgs e)
		{
			if (_box.IsDropDownOpen && _presented && !IsWithinBox(Mouse.Captured))
				RequestClose("capture-loss");
		}

		private void OnOwnerDeactivated(object? sender, EventArgs e)
		{
			if (_box.IsDropDownOpen)
				RequestClose("owner-deactivated");
		}

		private void OnOwnerStateChanged(object? sender, EventArgs e)
		{
			if (_owner?.WindowState == System.Windows.WindowState.Minimized && _box.IsDropDownOpen)
				RequestClose("owner-minimized");
		}

		private void RequestClose(string reason)
		{
			if (!_box.IsDropDownOpen)
				return;

			_closeReason = reason;
			_closeRequested = true;
			_requestedOpen = false;
			_presented = false;
			int generation = ++_generation;
			StopWatchdog();
			_ = _box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
			{
				if (_attached && _closeRequested && generation == _generation && _box.IsDropDownOpen)
					_box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
			}));
		}

		private bool IsWithinBox(IInputElement? element)
		{
			DependencyObject? current = element as DependencyObject;
			DependencyObject? popupChild = _popup?.Child;
			while (current is not null)
			{
				if (ReferenceEquals(current, _box) || ReferenceEquals(current, popupChild))
					return true;

				current = current is FrameworkElement frameworkElement
					? frameworkElement.Parent ?? frameworkElement.TemplatedParent
					: null;
			}

			return false;
		}

		private bool CanPresent()
		{
			return _attached
				&& _box.IsLoaded
				&& _box.IsVisible
				&& _owner?.WindowState != System.Windows.WindowState.Minimized;
		}

		private void QueueLogicalRecovery()
		{
			if (_logicalRecoveryQueued)
				return;

			_logicalRecoveryQueued = true;
			_recoveryCount++;
			int generation = ++_generation;
			StopWatchdog();
			_ = _box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
			{
				_logicalRecoveryQueued = false;
				if (!_requestedOpen || _closeRequested || generation != _generation || !CanPresent())
					return;

				Report(CreateState("restoring logical state after surface loss"));
				_box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
				_box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
			}));
		}

		private void QueueVerification(int generation, DispatcherPriority priority)
		{
			_ = _box.Dispatcher.BeginInvoke(priority, new Action(() => Verify(generation)));
		}

		private void Verify(int generation)
		{
			if (!_attached || generation != _generation || !_box.IsDropDownOpen)
				return;

			Window? owner = Window.GetWindow(_box);
			if (!_box.IsLoaded || !_box.IsVisible || owner is { WindowState: System.Windows.WindowState.Minimized })
			{
				_box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
				return;
			}

			BindPopup();
			if (_popup is null)
			{
				FailClosed("template popup missing");
				return;
			}

			FrameworkElement? child = _popup.Child as FrameworkElement;
			bool effective = Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(_popup);
			bool presented = child is not null
				&& PresentationSource.FromVisual(child) is not null
				&& child.ActualWidth > 0d
				&& child.ActualHeight > 0d;

			if (_popup.IsOpen && effective && presented)
			{
				RestoreInput();
				EnsureCapture();
				_presented = true;
				_repairCount = 0;
				_recoveryCount = 0;
				StopWatchdog();
				return;
			}

			if (_repairCount >= MaxPresentationRepairs)
			{
				FailClosed("presentation recovery exhausted");
				return;
			}

			_repairCount++;
			Report(CreateState("forcing a real reopen transition"));
			RestoreInput();
			Wpf.Ui.Controls.PopupReveal.SetEffectiveIsOpen(_popup, false);
			Wpf.Ui.Controls.PopupReveal.SetEffectiveIsOpen(_popup, true);
			if (!_popup.IsOpen)
				_popup.SetCurrentValue(Popup.IsOpenProperty, true);
			EnsureCapture();
			ArmWatchdog(generation);
		}

		private void RestoreInput()
		{
			if (_popup?.Child is not FrameworkElement child)
				return;

			child.BeginAnimation(UIElement.OpacityProperty, null);
			child.Opacity = 1d;
			child.IsHitTestVisible = true;
		}

		private void EnsureCapture()
		{
			if (_box.IsDropDownOpen && Mouse.Captured is null)
				Mouse.Capture(_box, CaptureMode.SubTree);
		}

		private void FailClosed(string reason)
		{
			_closeReason = reason;
			_presented = false;
			Report(CreateState(reason));
			_generation++;
			StopWatchdog();
			_box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
		}

		private string CreateState(string reason)
		{
			FrameworkElement? child = _popup?.Child as FrameworkElement;
			Window? owner = Window.GetWindow(_box);
			string nativeState = "native=unknown";
#if CROSSPLAT
			if (System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetPortablePopupSnapshot(owner, out var snapshot))
			{
				nativeState = "native=" + snapshot.NativeWindowCount
					+ ", presentedNative=" + snapshot.PresentedNativeWindowCount
					+ ", portableVisible=" + snapshot.VisibleCount;
			}
#endif

			return reason
				+ ", logical=" + _box.IsDropDownOpen
				+ ", effective=" + (_popup is not null && Wpf.Ui.Controls.PopupReveal.GetEffectiveIsOpen(_popup))
				+ ", popup=" + (_popup?.IsOpen ?? false)
				+ ", source=" + (child is not null && PresentationSource.FromVisual(child) is not null)
				+ ", hitTest=" + (child?.IsHitTestVisible ?? false)
				+ ", capture=" + (Mouse.Captured?.GetType().Name ?? "none")
				+ ", generation=" + _generation
				+ ", closeReason=" + _closeReason
				+ ", " + nativeState;
		}

		private void Report(string reason)
		{
			long now = Environment.TickCount64;
			if (now - _lastReport < ReportInterval.TotalMilliseconds)
				return;

			_lastReport = now;
			string name = string.IsNullOrWhiteSpace(_box.Name) ? _box.GetType().Name : _box.Name;
			string owner = Window.GetWindow(_box)?.GetType().Name ?? "unknown window";
			App.Logger?.WriteLine("LinuxComboBoxGuard", name + " in " + owner + ": " + reason);
		}

		private void ArmWatchdog(int generation)
		{
			StopWatchdog();
			_watchdog = new DispatcherTimer(DispatcherPriority.Input, _box.Dispatcher)
			{
				Interval = PresentationTimeout
			};
			_watchdogGeneration = generation;
			_watchdog.Tick += OnWatchdog;
			_watchdog.Start();
		}

		private void StopWatchdog()
		{
			if (_watchdog is null)
				return;

			_watchdog.Stop();
			_watchdog.Tick -= OnWatchdog;
			_watchdog = null;
		}

		private void OnWatchdog(object? sender, EventArgs e)
		{
			int generation = _watchdogGeneration;
			StopWatchdog();
			Verify(generation);
		}
	}
}
