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
	public class CustomDialog : System.Windows.Window, Voidstrap.UI.IBootstrapperDialog
	{
		private readonly System.Windows.Controls.TextBlock _messageText;
		private readonly System.Windows.Controls.ProgressBar _progressBar;
		private readonly System.Windows.Controls.Button _cancelButton;
		private string _message = "";
		private System.Windows.Forms.ProgressBarStyle _progressStyle;
		private int _progressValue;
		private int _progressMaximum = 100;
		private System.Windows.Shell.TaskbarItemProgressState _taskbarProgressState;
		private double _taskbarProgressValue;
		private bool _cancelEnabled;
		private bool _closingProgrammatically;

		public CustomDialog()
			: this(false)
		{
		}

		internal CustomDialog(bool isDesignPreview)
		{
			Title = "Voidstrap";
			Width = 460;
			Height = 190;
			MinWidth = 360;
			MinHeight = 170;
			ResizeMode = System.Windows.ResizeMode.NoResize;
			WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

			System.Windows.Controls.Grid root = new()
			{
				Margin = new System.Windows.Thickness(24)
			};
			root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
			root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
			root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

			_messageText = new System.Windows.Controls.TextBlock
			{
				FontSize = 15,
				Text = "Preparing Roblox",
				TextWrapping = System.Windows.TextWrapping.Wrap,
				VerticalAlignment = System.Windows.VerticalAlignment.Center
			};
			System.Windows.Controls.Grid.SetRow(_messageText, 0);
			root.Children.Add(_messageText);

			_progressBar = new System.Windows.Controls.ProgressBar
			{
				Minimum = 0,
				Maximum = _progressMaximum,
				Height = 8,
				Margin = new System.Windows.Thickness(0, 22, 0, 18),
				VerticalAlignment = System.Windows.VerticalAlignment.Center
			};
			System.Windows.Controls.Grid.SetRow(_progressBar, 1);
			root.Children.Add(_progressBar);

			_cancelButton = new System.Windows.Controls.Button
			{
				Content = "Cancel",
				MinWidth = 96,
				Padding = new System.Windows.Thickness(16, 6, 16, 6),
				HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
				Visibility = System.Windows.Visibility.Collapsed
			};
			System.Windows.Controls.Grid.SetRow(_cancelButton, 2);
			root.Children.Add(_cancelButton);

			Content = root;
			_cancelButton.Click += OnCancelClick;
			Closing += OnClosing;
			Closed += OnClosed;
		}

		public Voidstrap.Bootstrapper? Bootstrapper { get; set; }

		public string Message
		{
			get => _message;
			set
			{
				_message = value ?? "";
				UpdateUi(() => _messageText.Text = _message);
			}
		}

		public System.Windows.Forms.ProgressBarStyle ProgressStyle
		{
			get => _progressStyle;
			set
			{
				_progressStyle = value;
				UpdateUi(() => _progressBar.IsIndeterminate = value == System.Windows.Forms.ProgressBarStyle.Marquee);
			}
		}

		public int ProgressValue
		{
			get => _progressValue;
			set
			{
				_progressValue = Math.Clamp(value, 0, Math.Max(1, _progressMaximum));
				UpdateUi(() => _progressBar.Value = _progressValue);
			}
		}

		public int ProgressMaximum
		{
			get => _progressMaximum;
			set
			{
				_progressMaximum = Math.Max(1, value);
				_progressValue = Math.Clamp(_progressValue, 0, _progressMaximum);
				UpdateUi(() =>
				{
					_progressBar.Maximum = _progressMaximum;
					_progressBar.Value = _progressValue;
				});
			}
		}

		public System.Windows.Shell.TaskbarItemProgressState TaskbarProgressState
		{
			get => _taskbarProgressState;
			set => _taskbarProgressState = value;
		}

		public double TaskbarProgressValue
		{
			get => _taskbarProgressValue;
			set => _taskbarProgressValue = value;
		}

		public bool CancelEnabled
		{
			get => _cancelEnabled;
			set
			{
				_cancelEnabled = value;
				UpdateUi(() =>
				{
					_cancelButton.IsEnabled = value;
					_cancelButton.Visibility = value ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
				});
			}
		}

		public Action? CancelCallback { get; set; }

		public Wpf.Ui.Appearance.WindowCornerPreference WindowCornerPreference { get; set; }

		public void ApplyCustomTheme(string name)
		{
			throw new PlatformNotSupportedException("Custom themes are not available on this platform");
		}

		public void ApplyCustomTheme(string name, string content)
		{
			throw new PlatformNotSupportedException("Custom themes are not available on this platform");
		}

		public void ShowBootstrapper()
		{
			if (Dispatcher.CheckAccess())
			{
				ShowDialog();
				return;
			}

			Dispatcher.Invoke(ShowDialog);
		}

		public void CloseBootstrapper()
		{
			_closingProgrammatically = true;
			UpdateUi(Close);
		}

		public void ShowSuccess(string message, Action? callback = null)
		{
			Message = message;
			ProgressStyle = System.Windows.Forms.ProgressBarStyle.Continuous;
			ProgressValue = ProgressMaximum;
			CancelEnabled = false;
			callback?.Invoke();
		}

		private void OnCancelClick(object sender, System.Windows.RoutedEventArgs e)
		{
			CancelCallback?.Invoke();
			Bootstrapper?.Cancel();
			CancelEnabled = false;
		}

		private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
		{
			if (_closingProgrammatically)
			{
				return;
			}

			CancelCallback?.Invoke();
			Bootstrapper?.Cancel();
		}

		private void OnClosed(object? sender, EventArgs e)
		{
			_cancelButton.Click -= OnCancelClick;
			Closing -= OnClosing;
			Closed -= OnClosed;
			CancelCallback = null;
			Bootstrapper = null;
		}

		private void UpdateUi(Action action)
		{
			if (Dispatcher.CheckAccess())
			{
				action();
				return;
			}

			Dispatcher.Invoke(action);
		}
	}

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
	internal sealed class RiShadeWgc : IDisposable
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
		Return = 13,
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

		public System.Drawing.Icon? Icon { get; set; }
		public string Text { get; set; } = "";
		public bool Visible { get; set; }
		public ContextMenuStrip? ContextMenuStrip { get; set; }
		public string BalloonTipTitle { get; set; } = "";
		public string BalloonTipText { get; set; } = "";
		public ToolTipIcon BalloonTipIcon { get; set; }

		public event EventHandler? DoubleClick;
		public event EventHandler? Click;
		public event EventHandler? BalloonTipClicked;
		public event EventHandler? BalloonTipClosed;
		public event MouseEventHandler? MouseClick;
		public event MouseEventHandler? MouseDoubleClick;

		public void ShowBalloonTip(int timeout)
		{
		}

		public void ShowBalloonTip(int timeout, string title, string message, ToolTipIcon icon)
		{
		}

		public void Dispose()
		{
			DoubleClick = null;
			Click = null;
			BalloonTipClicked = null;
			BalloonTipClosed = null;
			MouseClick = null;
			MouseDoubleClick = null;
			GC.SuppressFinalize(this);
		}
	}

	public static class SystemInformation
	{
		public static System.Drawing.Size PrimaryMonitorSize => new System.Drawing.Size(1920, 1080);
		public static System.Drawing.Size VirtualScreen => new System.Drawing.Size(1920, 1080);
	}

	public class Screen
	{
		private static readonly Screen _primary = new Screen();

		public static Screen PrimaryScreen => _primary;

		public static Screen[] AllScreens => new[] { _primary };

		public System.Drawing.Rectangle Bounds => new System.Drawing.Rectangle(0, 0, 1920, 1080);

		public System.Drawing.Rectangle WorkingArea => new System.Drawing.Rectangle(0, 0, 1920, 1040);

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
