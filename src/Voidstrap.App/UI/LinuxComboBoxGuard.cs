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
		if (_installed || !Voidstrap.Utility.Platform.UsesPortableUi)
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
			RestoreGrownOwner();
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

			RestorePlacement(_popup);
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
			if (GrowOwnerBeforeOpen())
				return;
			FitToWindow();
			RestoreInput();
			EnsureCapture();
			ArmWatchdog(_generation);
			QueueVerification(_generation, DispatcherPriority.Render);
		}

		private double _naturalDropDownHeight = double.NaN;

		private void FitToWindow()
		{
			if (_popup is null || Window.GetWindow(_box) is not Window owner || owner.ActualHeight <= 0)
				return;

			Point top;
			try
			{
				top = _box.TranslatePoint(new Point(0, 0), owner);
			}
			catch (InvalidOperationException)
			{
				return;
			}

			if (double.IsNaN(_naturalDropDownHeight))
				_naturalDropDownHeight = _box.MaxDropDownHeight;

			Thickness margin = SurfaceMargin();
			double gap = PlacementGap();
			double below = owner.ActualHeight - top.Y - _box.ActualHeight - gap - margin.Bottom - WindowEdge;
			double above = top.Y - gap - margin.Top - WindowEdge;
			double needed = NeededSurfaceHeight();
			bool openUp = below < needed && above > below;
			double room = Math.Max(MinimumSurfaceHeight, openUp ? above : below) - SurfaceChrome;
			PlaceAtEdge(openUp);
			_box.SetCurrentValue(ComboBox.MaxDropDownHeightProperty, Math.Min(_naturalDropDownHeight, room));
		}

		private const double WindowEdge = 4;

		private const double SurfaceChrome = 6;

		private const double MinimumSurfaceHeight = 54;

		private CustomPopupPlacementCallback? _templatePlacementCallback;

		private PlacementMode _templatePlacement;

		private bool _placementCaptured;

		private double NeededSurfaceHeight()
		{
			return Math.Min(_naturalDropDownHeight + SurfaceChrome, _box.Items.Count * 36d + 24d);
		}

		private FrameworkElement? Surface()
		{
			FrameworkElement? surface = _popup?.Child as FrameworkElement;
			if (surface is Panel panel && panel.Children.Count == 1)
				surface = panel.Children[0] as FrameworkElement;
			return surface;
		}

		private Thickness SurfaceMargin()
		{
			return Surface()?.Margin ?? default;
		}

		private double PlacementGap()
		{
			if (_popup is null)
				return 0;
			double gap = Wpf.Ui.Controls.PopupReveal.GetPlacementGap(_popup);
			return double.IsFinite(gap) && gap > 0 ? gap : 0;
		}

		private void CapturePlacement()
		{
			if (_placementCaptured || _popup is null)
				return;
			_placementCaptured = true;
			_templatePlacement = _popup.Placement;
			_templatePlacementCallback = _popup.CustomPopupPlacementCallback;
		}

		private void RestorePlacement(Popup popup)
		{
			if (!_placementCaptured)
				return;
			_placementCaptured = false;
			popup.Placement = _templatePlacement;
			popup.CustomPopupPlacementCallback = _templatePlacementCallback;
			_templatePlacementCallback = null;
		}

		private void PlaceAtEdge(bool openUp)
		{
			if (_popup is null)
				return;
			CapturePlacement();
			_popup.CustomPopupPlacementCallback = openUp ? PlaceAbove : PlaceBelow;
			_popup.Placement = PlacementMode.Custom;
		}

		private CustomPopupPlacement[] PlaceBelow(Size popupSize, Size targetSize, Point offset)
		{
			Thickness margin = SurfaceMargin();
			return [new CustomPopupPlacement(new Point(-margin.Left, targetSize.Height + PlacementGap() - margin.Top), PopupPrimaryAxis.Vertical)];
		}

		private CustomPopupPlacement[] PlaceAbove(Size popupSize, Size targetSize, Point offset)
		{
			Thickness margin = SurfaceMargin();
			return [new CustomPopupPlacement(new Point(-margin.Left, -popupSize.Height - PlacementGap() + margin.Bottom), PopupPrimaryAxis.Vertical)];
		}

		private Window? _grownOwner;
		private double _grownOwnerMinHeight;
		private bool _reopenAfterGrow;
		private DispatcherTimer? _growTimer;

		private bool GrowOwnerBeforeOpen()
		{
			if (_grownOwner is not null || _popup is null || Window.GetWindow(_box) is not Window owner
				|| owner.ActualHeight <= 0 || owner.SizeToContent == SizeToContent.Manual)
				return false;

			Point top;
			try
			{
				top = _box.TranslatePoint(new Point(0, 0), owner);
			}
			catch (InvalidOperationException)
			{
				return false;
			}

			if (double.IsNaN(_naturalDropDownHeight))
				_naturalDropDownHeight = _box.MaxDropDownHeight;

			Thickness margin = SurfaceMargin();
			double gap = PlacementGap();
			double below = owner.ActualHeight - top.Y - _box.ActualHeight - gap - margin.Bottom - WindowEdge;
			double above = top.Y - gap - margin.Top - WindowEdge;
			double needed = NeededSurfaceHeight();
			if (below >= needed || above >= needed)
				return false;

			_grownOwner = owner;
			_grownOwnerMinHeight = owner.MinHeight;
			_reopenAfterGrow = true;
			owner.SizeChanged += OnGrownOwnerSizeChanged;
			_growTimer = new DispatcherTimer(DispatcherPriority.Background, _box.Dispatcher)
			{
				Interval = PresentationTimeout
			};
			_growTimer.Tick += OnGrowTimer;
			_growTimer.Start();
			owner.MinHeight = owner.ActualHeight + (needed - below);
			_ = _box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CloseForGrow));
			return true;
		}

		private void CloseForGrow()
		{
			if (_attached && _reopenAfterGrow && _box.IsDropDownOpen)
				_box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, false);
		}

		private void OnGrownOwnerSizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (_reopenAfterGrow)
				_ = _box.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(ReopenAfterGrow));
		}

		private void OnGrowTimer(object? sender, EventArgs e)
		{
			ReopenAfterGrow();
		}

		private void ReopenAfterGrow()
		{
			StopGrowTimer();
			if (!_reopenAfterGrow)
				return;

			_reopenAfterGrow = false;
			if (_attached && CanPresent())
				_box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
			else
				RestoreGrownOwner();
		}

		private void StopGrowTimer()
		{
			if (_growTimer is null)
				return;
			_growTimer.Stop();
			_growTimer.Tick -= OnGrowTimer;
			_growTimer = null;
		}

		private void RestoreGrownOwner()
		{
			_reopenAfterGrow = false;
			StopGrowTimer();
			if (_grownOwner is null)
				return;
			_grownOwner.SizeChanged -= OnGrownOwnerSizeChanged;
			_grownOwner.MinHeight = _grownOwnerMinHeight;
			_grownOwner = null;
		}

		private void OnDropDownClosed(object? sender, EventArgs e)
		{
			if (_reopenAfterGrow)
			{
				_requestedOpen = false;
				_presented = false;
				_generation++;
				StopWatchdog();
				return;
			}

			RestoreGrownOwner();
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
