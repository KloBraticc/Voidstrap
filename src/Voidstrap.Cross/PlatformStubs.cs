using System;

namespace Voidstrap.UI.Elements.Bootstrapper.Base
{
	public class WinFormsDialogBase : System.Windows.Forms.Control, Voidstrap.UI.IBootstrapperDialog
	{
		private readonly Voidstrap.UI.Elements.Bootstrapper.CustomDialog _dialog = new();

		public Voidstrap.Bootstrapper? Bootstrapper
		{
			get => _dialog.Bootstrapper;
			set => _dialog.Bootstrapper = value;
		}

		public string Message
		{
			get => _dialog.Message;
			set => _dialog.Message = value;
		}

		public System.Windows.Forms.ProgressBarStyle ProgressStyle
		{
			get => _dialog.ProgressStyle;
			set => _dialog.ProgressStyle = value;
		}

		public int ProgressValue
		{
			get => _dialog.ProgressValue;
			set => _dialog.ProgressValue = value;
		}

		public int ProgressMaximum
		{
			get => _dialog.ProgressMaximum;
			set => _dialog.ProgressMaximum = value;
		}

		public System.Windows.Shell.TaskbarItemProgressState TaskbarProgressState
		{
			get => _dialog.TaskbarProgressState;
			set => _dialog.TaskbarProgressState = value;
		}

		public double TaskbarProgressValue
		{
			get => _dialog.TaskbarProgressValue;
			set => _dialog.TaskbarProgressValue = value;
		}

		public bool CancelEnabled
		{
			get => _dialog.CancelEnabled;
			set => _dialog.CancelEnabled = value;
		}

		public Action? CancelCallback
		{
			get => _dialog.CancelCallback;
			set => _dialog.CancelCallback = value;
		}

		public void ShowBootstrapper()
		{
			_dialog.ShowBootstrapper();
		}

		public void CloseBootstrapper()
		{
			_dialog.CloseBootstrapper();
		}

		public void ShowSuccess(string message, Action? callback = null)
		{
			_dialog.ShowSuccess(message, callback);
		}
	}
}

namespace Voidstrap.UI.Elements.Bootstrapper
{
	public class VistaDialog : Voidstrap.UI.Elements.Bootstrapper.Base.WinFormsDialogBase
	{
	}

	public class LegacyDialog2008 : Voidstrap.UI.Elements.Bootstrapper.Base.WinFormsDialogBase
	{
	}

	public class LegacyDialog2011 : Voidstrap.UI.Elements.Bootstrapper.Base.WinFormsDialogBase
	{
	}

	public class ProgressDialog : Voidstrap.UI.Elements.Bootstrapper.Base.WinFormsDialogBase
	{
	}
}

namespace Voidstrap.Integrations.RiShade
{
	public sealed class RiShadeWgc : IDisposable
	{
		public bool IsClosed => true;
		public long DroppedCount => 0;

		public static RiShadeWgc? TryCreate(Vortice.Direct3D11.ID3D11Device device, IntPtr targetHwnd, double targetFps = 0)
		{
			return null;
		}

		public void SetTargetFps(double targetFps)
		{
		}

		public bool TryCopyLatestFrame(Vortice.Direct3D11.ID3D11DeviceContext context, Vortice.Direct3D11.ID3D11Texture2D? target, int width, int height)
		{
			return false;
		}

		public bool TryCopyLatestFrame(Vortice.Direct3D11.ID3D11DeviceContext context, Vortice.Direct3D11.ID3D11Texture2D? target, int width, int height, out double elapsedMs)
		{
			elapsedMs = 0;
			return false;
		}

		public void WaitForFrame(int timeoutMs)
		{
		}

		public void Dispose()
		{
			GC.SuppressFinalize(this);
		}
	}
}

namespace Microsoft.VisualBasic.Devices
{
	public class ComputerInfo
	{
		public ulong TotalPhysicalMemory => ReadMemInfo("MemTotal:");

		public ulong AvailablePhysicalMemory => ReadMemInfo("MemAvailable:");

		public ulong TotalVirtualMemory => ReadMemInfo("SwapTotal:") + ReadMemInfo("MemTotal:");

		public ulong AvailableVirtualMemory => ReadMemInfo("SwapFree:") + ReadMemInfo("MemAvailable:");

		private static ulong ReadMemInfo(string key)
		{
			try
			{
				if (!System.IO.File.Exists("/proc/meminfo"))
				{
					return 0;
				}
				foreach (string line in System.IO.File.ReadLines("/proc/meminfo"))
				{
					if (!line.StartsWith(key, StringComparison.Ordinal))
					{
						continue;
					}
					string[] parts = line.Substring(key.Length).Trim().Split(' ');
					if (parts.Length > 0 && ulong.TryParse(parts[0], out ulong kilobytes))
					{
						return kilobytes * 1024UL;
					}
					return 0;
				}
			}
			catch
			{
			}
			return 0;
		}
	}
}

namespace System.Windows.Forms
{
	public enum ProgressBarStyle
	{
		Blocks,
		Continuous,
		Marquee
	}

	public enum ToolTipIcon
	{
		None,
		Info,
		Warning,
		Error
	}

	public enum DialogResult
	{
		None,
		OK,
		Cancel,
		Abort,
		Retry,
		Ignore,
		Yes,
		No
	}

	public struct Padding
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;

		public Padding(int left, int top, int right, int bottom)
		{
			Left = left;
			Top = top;
			Right = right;
			Bottom = bottom;
		}

		public Padding(int all)
		{
			Left = all;
			Top = all;
			Right = all;
			Bottom = all;
		}

		public int Horizontal => Left + Right;

		public int Vertical => Top + Bottom;
	}

	public class TextBox : Control
	{
	}

	public class Control : IDisposable
	{
		public string Text { get; set; } = "";
		public bool Visible { get; set; }
		public bool Enabled { get; set; } = true;
		public System.Drawing.Size Size { get; set; }
		public System.Drawing.Point Location { get; set; }
		public IntPtr Handle => IntPtr.Zero;
		public bool InvokeRequired => false;

		public object? Invoke(Delegate method)
		{
			return method.DynamicInvoke();
		}

		public object? Invoke(Delegate method, params object[] args)
		{
			return method.DynamicInvoke(args);
		}

		public void Show()
		{
		}

		public void Hide()
		{
		}

		public void Invalidate()
		{
		}

		public void Dispose()
		{
			GC.SuppressFinalize(this);
		}
	}

	public class ToolStripItemCollection
	{
		public object Add(string text, System.Drawing.Image? image, EventHandler onClick)
		{
			return new object();
		}
	}

	public class ContextMenuStrip : IDisposable
	{
		public ToolStripItemCollection Items { get; } = new ToolStripItemCollection();

		public void Dispose()
		{
			GC.SuppressFinalize(this);
		}
	}

	public enum MouseButtons
	{
		None,
		Left,
		Right,
		Middle
	}

	public enum Keys
	{
		None = 0,
		Back = 8,
		Tab = 9,
		Enter = 13,
		Return = Enter,
		ShiftKey = 16,
		ControlKey = 17,
		Menu = 18,
		Pause = 19,
		CapsLock = 20,
		Escape = 27,
		Space = 32,
		PageUp = 33,
		PageDown = 34,
		End = 35,
		Home = 36,
		Left = 37,
		Up = 38,
		Right = 39,
		Down = 40,
		Insert = 45,
		Delete = 46,
		D0 = 48,
		D1 = 49,
		D2 = 50,
		D3 = 51,
		D4 = 52,
		D5 = 53,
		D6 = 54,
		D7 = 55,
		D8 = 56,
		D9 = 57,
		A = 65,
		B = 66,
		C = 67,
		D = 68,
		E = 69,
		F = 70,
		G = 71,
		H = 72,
		I = 73,
		J = 74,
		K = 75,
		L = 76,
		M = 77,
		N = 78,
		O = 79,
		P = 80,
		Q = 81,
		R = 82,
		S = 83,
		T = 84,
		U = 85,
		V = 86,
		W = 87,
		X = 88,
		Y = 89,
		Z = 90,
		LWin = 91,
		RWin = 92,
		NumPad0 = 96,
		NumPad1 = 97,
		NumPad2 = 98,
		NumPad3 = 99,
		NumPad4 = 100,
		NumPad5 = 101,
		NumPad6 = 102,
		NumPad7 = 103,
		NumPad8 = 104,
		NumPad9 = 105,
		F1 = 112,
		F2 = 113,
		F3 = 114,
		F4 = 115,
		F5 = 116,
		F6 = 117,
		F7 = 118,
		F8 = 119,
		F9 = 120,
		F10 = 121,
		F11 = 122,
		F12 = 123,
		NumLock = 144,
		Scroll = 145,
		LShiftKey = 160,
		RShiftKey = 161,
		LControlKey = 162,
		RControlKey = 163,
		LMenu = 164,
		RMenu = 165,
		Oemtilde = 192,
		OemOpenBrackets = 219,
		OemPipe = 220,
		OemCloseBrackets = 221,
		OemQuestion = 191,
		OemQuotes = 222,
		Shift = 65536,
		Control = 131072,
		Alt = 262144
	}

	public class MouseEventArgs : EventArgs
	{
		public MouseButtons Button { get; set; }
		public int Clicks { get; set; }
		public int X { get; set; }
		public int Y { get; set; }
	}

	public class FolderBrowserDialog : IDisposable
	{
		public string SelectedPath { get; set; } = "";
		public string Description { get; set; } = "";
		public bool ShowNewFolderButton { get; set; }

		public DialogResult ShowDialog()
		{
			return DialogResult.Cancel;
		}

		public void Dispose()
		{
			GC.SuppressFinalize(this);
		}
	}

	public delegate void MouseEventHandler(object? sender, MouseEventArgs e);

	public class NotifyIcon : IDisposable
	{
		public NotifyIcon()
		{
		}

		public NotifyIcon(System.ComponentModel.IContainer container)
		{
		}

		private bool _visible;

		public System.Drawing.Icon? Icon { get; set; }
		public string Text { get; set; } = "Voidstrap";

		public bool Visible
		{
			get => _visible;
			set
			{
				if (_visible == value)
				{
					return;
				}

				_visible = value;
				if (value)
				{
					if (Voidstrap.UI.Tray.LinuxTray.TryStart(Text))
					{
						AttachTrayHandlers();
					}
					return;
				}

				Voidstrap.UI.Tray.LinuxTray.Stop();
			}
		}

		private void AttachTrayHandlers()
		{
			Voidstrap.UI.Tray.StatusNotifierItemObject? item = Voidstrap.UI.Tray.LinuxTray.Item;
			if (item == null)
			{
				return;
			}

			item.Activated += OnTrayActivated;
			item.SecondaryActivated += OnTraySecondaryActivated;
			item.ContextMenuRequested += OnTrayContextMenu;
		}

		private void OnTrayActivated()
		{
			Click?.Invoke(this, EventArgs.Empty);
			MouseClick?.Invoke(this, new MouseEventArgs { Button = MouseButtons.Left, Clicks = 1 });
		}

		private void OnTraySecondaryActivated()
		{
			MouseClick?.Invoke(this, new MouseEventArgs { Button = MouseButtons.Middle, Clicks = 1 });
		}

		private void OnTrayContextMenu()
		{
			MouseClick?.Invoke(this, new MouseEventArgs { Button = MouseButtons.Right, Clicks = 1 });
		}
		public ContextMenuStrip? ContextMenuStrip { get; set; }
		public string BalloonTipTitle { get; set; } = "";
		public string BalloonTipText { get; set; } = "";
		public ToolTipIcon BalloonTipIcon { get; set; }

		public event EventHandler? DoubleClick { add { } remove { } }
		public event EventHandler? Click;
		public event EventHandler? BalloonTipClicked { add { } remove { } }
		public event EventHandler? BalloonTipClosed { add { } remove { } }
		public event MouseEventHandler? MouseClick;
		public event MouseEventHandler? MouseDoubleClick { add { } remove { } }

		public void ShowBalloonTip(int timeout)
		{
			Voidstrap.UI.Tray.LinuxTray.Notify(BalloonTipTitle, BalloonTipText);
		}

		public void ShowBalloonTip(int timeout, string title, string message, ToolTipIcon icon)
		{
			Voidstrap.UI.Tray.LinuxTray.Notify(title, message);
		}

		public void Dispose()
		{
			Voidstrap.UI.Tray.LinuxTray.Stop();
			Click = null;
			MouseClick = null;
			GC.SuppressFinalize(this);
		}
	}

	public static class SystemInformation
	{
		public static System.Drawing.Size PrimaryMonitorSize
		{
			get
			{
				System.Drawing.Rectangle bounds = Screen.PrimaryScreen.Bounds;
				return new System.Drawing.Size(bounds.Width, bounds.Height);
			}
		}

		public static System.Drawing.Size VirtualScreen => PrimaryMonitorSize;
	}

	public class Screen
	{
		private static readonly Screen _primary = new Screen();

		public static Screen PrimaryScreen => _primary;

		public static Screen[] AllScreens => new[] { _primary };

		public System.Drawing.Rectangle Bounds
		{
			get
			{
				Voidstrap.Platform.Linux.LinuxDisplayBounds bounds = Voidstrap.Platform.Linux.LinuxDisplayMetrics.Current.Bounds;
				return new System.Drawing.Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
			}
		}

		public System.Drawing.Rectangle WorkingArea
		{
			get
			{
				Voidstrap.Platform.Linux.LinuxDisplayBounds workArea = Voidstrap.Platform.Linux.LinuxDisplayMetrics.Current.WorkArea;
				return new System.Drawing.Rectangle(workArea.Left, workArea.Top, workArea.Width, workArea.Height);
			}
		}

		public bool Primary => true;

		public string DeviceName => "Display";

		public static Screen FromHandle(IntPtr hwnd)
		{
			return _primary;
		}

		public static Screen FromPoint(System.Drawing.Point point)
		{
			return _primary;
		}
	}
}

namespace Microsoft.Web.WebView2.Core
{
	public enum CoreWebView2HostResourceAccessKind
	{
		Deny,
		Allow,
		DenyCors
	}

	public enum CoreWebView2PermissionState
	{
		Default,
		Allow,
		Deny
	}

	public enum CoreWebView2WebResourceContext
	{
		All
	}

	public class CoreWebView2Settings
	{
		public bool AreDefaultContextMenusEnabled { get; set; }
		public bool AreDevToolsEnabled { get; set; }
		public bool AreHostObjectsAllowed { get; set; }
		public bool IsWebMessageEnabled { get; set; }
		public bool IsStatusBarEnabled { get; set; }
		public bool IsBuiltInErrorPageEnabled { get; set; }
		public bool IsZoomControlEnabled { get; set; }
		public bool AreBrowserAcceleratorKeysEnabled { get; set; }
		public bool IsPasswordAutosaveEnabled { get; set; }
		public bool IsGeneralAutofillEnabled { get; set; }
		public bool IsSwipeNavigationEnabled { get; set; }
	}

	public class CoreWebView2WebResourceResponse
	{
	}

	public class CoreWebView2WebResourceRequest
	{
		public string Uri { get; set; } = "";
	}

	public class CoreWebView2EnvironmentOptions
	{
		public CoreWebView2EnvironmentOptions()
		{
		}

		public CoreWebView2EnvironmentOptions(string additionalBrowserArguments)
		{
		}
	}

	public class CoreWebView2Environment
	{
		public static System.Threading.Tasks.Task<CoreWebView2Environment> CreateAsync(string? browserExecutableFolder = null, string? userDataFolder = null, CoreWebView2EnvironmentOptions? options = null)
		{
			return System.Threading.Tasks.Task.FromResult(new CoreWebView2Environment());
		}

		public CoreWebView2WebResourceResponse CreateWebResourceResponse(System.IO.Stream? content, int statusCode, string reasonPhrase, string headers)
		{
			return new CoreWebView2WebResourceResponse();
		}
	}

	public class CoreWebView2WebMessageReceivedEventArgs
	{
		public string WebMessageAsJson { get; set; } = "";

		public string TryGetWebMessageAsString()
		{
			return "";
		}
	}

	public class CoreWebView2NavigationCompletedEventArgs
	{
		public bool IsSuccess { get; set; }
	}

	public class CoreWebView2NewWindowRequestedEventArgs
	{
		public bool Handled { get; set; }
	}

	public class CoreWebView2NavigationStartingEventArgs
	{
		public string Uri { get; set; } = "";
		public bool Cancel { get; set; }
	}

	public class CoreWebView2DownloadStartingEventArgs
	{
		public bool Cancel { get; set; }
	}

	public class CoreWebView2PermissionRequestedEventArgs
	{
		public CoreWebView2PermissionState State { get; set; }
	}

	public class CoreWebView2ContextMenuRequestedEventArgs
	{
		public bool Handled { get; set; }
	}

	public class CoreWebView2WebResourceRequestedEventArgs
	{
		public CoreWebView2WebResourceRequest Request { get; } = new CoreWebView2WebResourceRequest();
		public CoreWebView2WebResourceResponse? Response { get; set; }
	}

	public class CoreWebView2
	{
		public CoreWebView2Settings Settings { get; } = new CoreWebView2Settings();

		public CoreWebView2Environment Environment { get; } = new CoreWebView2Environment();

		public event EventHandler<CoreWebView2WebMessageReceivedEventArgs>? WebMessageReceived { add { } remove { } }
		public event EventHandler<CoreWebView2NavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
		public event EventHandler<CoreWebView2NewWindowRequestedEventArgs>? NewWindowRequested { add { } remove { } }
		public event EventHandler<CoreWebView2NavigationStartingEventArgs>? NavigationStarting { add { } remove { } }
		public event EventHandler<CoreWebView2DownloadStartingEventArgs>? DownloadStarting { add { } remove { } }
		public event EventHandler<CoreWebView2PermissionRequestedEventArgs>? PermissionRequested { add { } remove { } }
		public event EventHandler<CoreWebView2ContextMenuRequestedEventArgs>? ContextMenuRequested { add { } remove { } }
		public event EventHandler<CoreWebView2WebResourceRequestedEventArgs>? WebResourceRequested { add { } remove { } }

		public void SetVirtualHostNameToFolderMapping(string hostName, string folderPath, CoreWebView2HostResourceAccessKind accessKind)
		{
		}

		public void AddWebResourceRequestedFilter(string uri, CoreWebView2WebResourceContext resourceContext)
		{
		}

		public System.Threading.Tasks.Task<string> AddScriptToExecuteOnDocumentCreatedAsync(string javaScript)
		{
			return System.Threading.Tasks.Task.FromResult(string.Empty);
		}

		public System.Threading.Tasks.Task<string> ExecuteScriptAsync(string javaScript)
		{
			return System.Threading.Tasks.Task.FromResult(string.Empty);
		}

		public void Navigate(string uri)
		{
		}
	}
}

namespace Microsoft.Web.WebView2.Wpf
{
	public class WebView2 : System.Windows.Controls.Control, IDisposable
	{
		public Microsoft.Web.WebView2.Core.CoreWebView2? CoreWebView2 { get; private set; }

		public System.Drawing.Color DefaultBackgroundColor { get; set; }

		public System.Threading.Tasks.Task EnsureCoreWebView2Async(Microsoft.Web.WebView2.Core.CoreWebView2Environment? environment = null)
		{
			return System.Threading.Tasks.Task.CompletedTask;
		}

		public void Dispose()
		{
			GC.SuppressFinalize(this);
		}
	}
}
