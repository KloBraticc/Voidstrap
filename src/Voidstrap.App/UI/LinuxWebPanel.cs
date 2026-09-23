using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static Voidstrap.UI.LinuxWebKitInterop;

namespace Voidstrap.UI;

internal sealed class LinuxWebPanel : FrameworkElement, IDisposable
{
	private static readonly object StartLock = new();

	private static readonly List<GSourceFunc> PendingCallbacks = new();

	private static Thread? _gtkThread;

	private static bool _gtkReady;

	private const int HardwareAccelerationNever = 2;

	private const uint PaintPumpInterval = 33;

	private const int WebKitLoadFinished = 3;

	private const uint PaintPumpFallbackDelay = 6000;

	private static IntPtr _display;

	private readonly List<Delegate> _handlers = new();

	private IntPtr _window;

	private IntPtr _view;

	private IntPtr _manager;

	private uint _childXid;

	private uint _hostXid;

	private bool _disposed;

	private bool _created;

	private GSourceFunc? _paintPump;

	private long _requestedAt;

	private Rect _lastRect;

	public string? Source { get; set; }

	public string? AccentColor { get; set; }

	public string? ThemeFolder { get; set; }

	public Voidstrap.UI.Elements.Bootstrapper.CustomDialog? Owner { get; set; }

	public bool AllowDrag { get; set; }

	public LinuxWebPanel()
	{
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		LayoutUpdated += OnLayoutUpdated;
	}

	public static bool IsSupported => Voidstrap.Utility.Platform.IsLinux && EnsureGtk();

	private static bool EnsureGtk()
	{
		lock (StartLock)
		{
			if (_gtkReady)
				return true;

			if (_gtkThread != null)
				return _gtkReady;

			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				_ = setenv("GDK_BACKEND", "x11", 1);
				Environment.SetEnvironmentVariable("GDK_BACKEND", "x11");
				ManualResetEventSlim started = new();
				_gtkThread = new Thread(() =>
				{
					int argc = 0;
					IntPtr argv = IntPtr.Zero;
					_gtkReady = gtk_init_check(ref argc, ref argv);
					Voidstrap.Platform.Linux.LinuxWindowInterop.KeepIgnoringXErrors();
					_display = XOpenDisplay(null);
					started.Set();
					if (_gtkReady)
						gtk_main();
				})
				{
					IsBackground = true,
					Name = "Voidstrap web panel"
				};

				_gtkThread.Start();
				started.Wait(5000);
				if (!_gtkReady)
					App.Logger?.WriteLine("LinuxWebPanel::EnsureGtk", "The portable web engine could not start");
				else
					App.Logger?.WriteLine("LinuxWebPanel::EnsureGtk", $"engine thread ready in {Elapsed(start)}ms");

				return _gtkReady;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("LinuxWebPanel::EnsureGtk", "The portable web engine is unavailable: " + ex.Message);
				return false;
			}
		}
	}

	private static void Post(Action action)
	{
		GSourceFunc callback = null!;
		callback = _ =>
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("LinuxWebPanel::Post", "A web panel operation failed: " + ex.Message);
			}
			finally
			{
				lock (PendingCallbacks)
					PendingCallbacks.Remove(callback);
			}

			return false;
		};

		lock (PendingCallbacks)
			PendingCallbacks.Add(callback);

		g_idle_add(callback, IntPtr.Zero);
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (_created || _disposed || !IsSupported)
			return;

		Window? host = Window.GetWindow(this);
		if (host == null)
			return;

		_hostXid = ResolveHostWindow(host);
		if (_hostXid == 0)
		{
			App.Logger?.WriteLine("LinuxWebPanel::OnLoaded", "The host window could not be resolved for " + (host.Title ?? "no title"));
			return;
		}

		_created = true;
		Rect rect = ResolveRect(host);
		App.Logger?.WriteLine("LinuxWebPanel::OnLoaded",
			$"host {_hostXid:X} rect {rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0} source {Source}");
		_lastRect = rect;
		string? source = Source;
		string accent = AccentColor ?? "#0078d4";

		_requestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
		Post(() => Create(source, accent, rect));
	}

	private void Create(string? source, string accent, Rect rect)
	{
		_window = gtk_window_new(GtkWindowPopup);
		gtk_window_set_decorated(_window, false);
		gtk_window_resize(_window, Math.Max(1, (int)rect.Width), Math.Max(1, (int)rect.Height));

		_manager = webkit_user_content_manager_new();
		webkit_user_content_manager_register_script_message_handler(_manager, "voidstrap");

		ScriptMessageHandler messageHandler = OnScriptMessage;
		_handlers.Add(messageHandler);
		g_signal_connect_data(_manager, "script-message-received::voidstrap", Marshal.GetFunctionPointerForDelegate(messageHandler), IntPtr.Zero, IntPtr.Zero, 0);

		AddScript(_manager, "window.__vsAccentColor = " + System.Text.Json.JsonSerializer.Serialize(accent) + ";");
		AddScript(_manager, LinuxWebPanelBridge);
		AddScript(_manager, Voidstrap.UI.Elements.Bootstrapper.CustomDialog.WebPanelApi);

		_view = webkit_web_view_new_with_user_content_manager(_manager);

		IntPtr settings = webkit_web_view_get_settings(_view);
		webkit_settings_set_enable_developer_extras(settings, false);
		webkit_settings_set_hardware_acceleration_policy(settings, HardwareAccelerationNever);
		webkit_settings_set_javascript_can_open_windows_automatically(settings, false);
		webkit_settings_set_enable_html5_database(settings, false);
		webkit_settings_set_enable_html5_local_storage(settings, false);

		GdkRGBA transparent = new() { Red = 0d, Green = 0d, Blue = 0d, Alpha = 0d };
		webkit_web_view_set_background_color(_view, ref transparent);

		DecidePolicyHandler policyHandler = OnDecidePolicy;
		_handlers.Add(policyHandler);
		g_signal_connect_data(_view, "decide-policy", Marshal.GetFunctionPointerForDelegate(policyHandler), IntPtr.Zero, IntPtr.Zero, 0);

		LoadChangedHandler loadHandler = (view, loadEvent, data) =>
		{
			if (loadEvent != WebKitLoadFinished || _disposed)
				return;

			App.Logger?.WriteLine("LinuxWebPanel::Create", $"panel ready in {Elapsed(_requestedAt)}ms");
			Refresh();
			StartPaintPump();
			Dispatcher.BeginInvoke(new Action(() => Owner?.RequestWebPanelState()));
		};
		_handlers.Add(loadHandler);
		g_signal_connect_data(_view, "load-changed", Marshal.GetFunctionPointerForDelegate(loadHandler), IntPtr.Zero, IntPtr.Zero, 0);

		ContextMenuHandler contextHandler = OnContextMenu;
		_handlers.Add(contextHandler);
		g_signal_connect_data(_view, "context-menu", Marshal.GetFunctionPointerForDelegate(contextHandler), IntPtr.Zero, IntPtr.Zero, 0);

		gtk_container_add(_window, _view);
		gtk_widget_realize(_window);
		gtk_widget_show_all(_window);

		IntPtr gdkWindow = gtk_widget_get_window(_window);
		if (gdkWindow == IntPtr.Zero)
			return;

		_childXid = gdk_x11_window_get_xid(gdkWindow);
		if (_childXid == 0 || _display == IntPtr.Zero)
			return;

		int reparent = XReparentWindow(_display, _childXid, _hostXid, (int)rect.X, (int)rect.Y);
		_ = XFlush(_display);
		App.Logger?.WriteLine("LinuxWebPanel::Create",
			$"child {_childXid:X} reparent {reparent} into {_hostXid:X} at {rect.X:F0},{rect.Y:F0}");

		Refresh();

		if (!string.IsNullOrEmpty(source))
			webkit_web_view_load_uri(_view, source);

		SchedulePaintPump(PaintPumpFallbackDelay);
	}

	public void Evaluate(string script)
	{
		if (_disposed || string.IsNullOrEmpty(script))
			return;

		Post(() =>
		{
			if (_view != IntPtr.Zero)
				webkit_web_view_evaluate_javascript(_view, script, -1, null, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
		});
	}

	private void Refresh()
	{
		if (_window == IntPtr.Zero || _childXid == 0)
			return;

		gtk_widget_hide(_window);
		gtk_widget_show_all(_window);

		if (_view != IntPtr.Zero)
			gtk_widget_queue_draw(_view);

		if (_display != IntPtr.Zero)
		{
			_ = XMapWindow(_display, _childXid);
			_ = XFlush(_display);
		}
	}

	private void SchedulePaintPump(uint delay)
	{
		GSourceFunc callback = null!;
		callback = _ =>
		{
			try
			{
				if (!_disposed)
					StartPaintPump();
			}
			catch (Exception)
			{
			}
			finally
			{
				lock (PendingCallbacks)
					PendingCallbacks.Remove(callback);
			}

			return false;
		};

		lock (PendingCallbacks)
			PendingCallbacks.Add(callback);

		g_timeout_add(delay, callback, IntPtr.Zero);
	}

	private void StartPaintPump()
	{
		if (_paintPump != null)
			return;

		_paintPump = _ =>
		{
			if (_disposed)
				return false;

			PaintFrame();
			return true;
		};

		g_timeout_add(PaintPumpInterval, _paintPump, IntPtr.Zero);
	}

	private static long Elapsed(long from)
	{
		return (long)((System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000d / System.Diagnostics.Stopwatch.Frequency);
	}

	private void PaintFrame()
	{
		if (_window == IntPtr.Zero)
			return;

		IntPtr gdkWindow = gtk_widget_get_window(_window);
		if (gdkWindow == IntPtr.Zero)
			return;

		gdk_window_invalidate_rect(gdkWindow, IntPtr.Zero, true);
		gdk_window_process_updates(gdkWindow, true);
	}

	private void ScheduleRefresh(uint delay)
	{
		GSourceFunc callback = null!;
		callback = _ =>
		{
			try
			{
				if (!_disposed)
				{
					Refresh();
					Dispatcher.BeginInvoke(new Action(() => Owner?.RequestWebPanelState()));
				}
			}
			catch (Exception)
			{
			}
			finally
			{
				lock (PendingCallbacks)
					PendingCallbacks.Remove(callback);
			}

			return false;
		};

		lock (PendingCallbacks)
			PendingCallbacks.Add(callback);

		g_timeout_add(delay, callback, IntPtr.Zero);
	}

	private static void AddScript(IntPtr manager, string source)
	{
		IntPtr script = webkit_user_script_new(source, 0, 0, IntPtr.Zero, IntPtr.Zero);
		if (script != IntPtr.Zero)
			webkit_user_content_manager_add_script(manager, script);
	}

	private void OnScriptMessage(IntPtr manager, IntPtr result, IntPtr data)
	{
		try
		{
			IntPtr value = webkit_javascript_result_get_js_value(result);
			if (value == IntPtr.Zero)
				return;

			IntPtr text = jsc_value_to_string(value);
			string? message = Marshal.PtrToStringUTF8(text);
			if (string.IsNullOrEmpty(message))
				return;

			Dispatcher.BeginInvoke(new Action(() =>
			{
				if (message == "voidstrap:cancel")
					Owner?.HandleWebPanelCancel();
				else if (message == "voidstrap:drag" && AllowDrag)
					Owner?.HandleWebPanelDrag();
			}));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxWebPanel::OnScriptMessage", "A panel message failed: " + ex.Message);
		}
	}

	private const string LinuxWebPanelBridge = """
		window.chrome = window.chrome || {};
		window.chrome.webview = window.chrome.webview || {
			postMessage: function (message) {
				try { window.webkit.messageHandlers.voidstrap.postMessage(message); } catch (e) {}
			}
		};
		""";

	private bool OnDecidePolicy(IntPtr view, IntPtr decision, int decisionType, IntPtr data)
	{
		try
		{
			if (decisionType == 1)
			{
				webkit_policy_decision_ignore(decision);
				return true;
			}

			if (decisionType == 2)
			{
				if (!webkit_response_policy_decision_is_mime_type_supported(decision))
				{
					webkit_policy_decision_ignore(decision);
					return true;
				}

				return false;
			}

			IntPtr action = webkit_navigation_policy_decision_get_navigation_action(decision);
			if (action == IntPtr.Zero)
				return false;

			IntPtr request = webkit_navigation_action_get_request(action);
			if (request == IntPtr.Zero)
				return false;

			string? uri = Marshal.PtrToStringUTF8(webkit_uri_request_get_uri(request));
			if (IsAllowed(uri))
				return false;

			App.Logger?.WriteLine("LinuxWebPanel::OnDecidePolicy", "Blocked navigation to " + (uri ?? "an unknown target"));
			webkit_policy_decision_ignore(decision);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private bool IsAllowed(string? uri)
	{
		if (string.IsNullOrEmpty(uri))
			return false;

		if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
			return false;

		if (parsed.Scheme == Uri.UriSchemeFile)
		{
			string? folder = ThemeFolder;
			if (string.IsNullOrEmpty(folder))
				return false;

			string root = System.IO.Path.GetFullPath(folder);
			string target = System.IO.Path.GetFullPath(parsed.LocalPath);
			string relative = System.IO.Path.GetRelativePath(root, target);
			return relative != ".."
				&& !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
		}

		return parsed.Scheme == Uri.UriSchemeHttps
			&& string.Equals(parsed.Host, "cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase);
	}

	private bool OnContextMenu(IntPtr view, IntPtr menu, IntPtr evt, IntPtr hitTest, IntPtr data)
	{
		return true;
	}

	private void OnLayoutUpdated(object? sender, EventArgs e)
	{
		if (!_created || _disposed || _childXid == 0)
			return;

		Window? host = Window.GetWindow(this);
		if (host == null)
			return;

		Rect rect = ResolveRect(host);
		if (Math.Abs(rect.X - _lastRect.X) < 0.5d
			&& Math.Abs(rect.Y - _lastRect.Y) < 0.5d
			&& Math.Abs(rect.Width - _lastRect.Width) < 0.5d
			&& Math.Abs(rect.Height - _lastRect.Height) < 0.5d)
		{
			return;
		}

		_lastRect = rect;
		Post(() =>
		{
			if (_window == IntPtr.Zero)
				return;

			gtk_window_resize(_window, Math.Max(1, (int)rect.Width), Math.Max(1, (int)rect.Height));
			if (_display != IntPtr.Zero && _childXid != 0)
			{
				_ = XReparentWindow(_display, _childXid, _hostXid, (int)rect.X, (int)rect.Y);
				_ = XFlush(_display);
				Refresh();
			}
		});
	}

	private Rect ResolveRect(Window host)
	{
		try
		{
			GeneralTransform transform = TransformToAncestor(host);
			Point origin = transform.Transform(new Point(0d, 0d));
			return new Rect(origin.X, origin.Y, ActualWidth, ActualHeight);
		}
		catch (Exception)
		{
			return _lastRect;
		}
	}

	private static uint ResolveHostWindow(Window host)
	{
		try
		{
			nint handle = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(host.Title ?? string.Empty);
			return handle == 0 ? 0u : (uint)handle;
		}
		catch (Exception)
		{
			return 0;
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		Dispose();
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		Loaded -= OnLoaded;
		Unloaded -= OnUnloaded;
		LayoutUpdated -= OnLayoutUpdated;

		IntPtr window = _window;
		_window = IntPtr.Zero;
		_view = IntPtr.Zero;
		_childXid = 0;

		if (window != IntPtr.Zero)
			Post(() => gtk_widget_destroy(window));

		GC.SuppressFinalize(this);
	}
}
