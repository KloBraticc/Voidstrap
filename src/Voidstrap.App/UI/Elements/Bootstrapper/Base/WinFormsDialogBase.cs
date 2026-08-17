using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Shell;
using Voidstrap.Extensions;
using Voidstrap.UI.Utility;

namespace Voidstrap.UI.Elements.Bootstrapper.Base;

public class WinFormsDialogBase : Form, IBootstrapperDialog
{
	public const int TaskbarProgressMaximum = 100;

	private bool _isClosing;

	protected string _message = "Please wait...";

	protected ProgressBarStyle _progressStyle;

	protected int _progressValue;

	protected int _progressMaximum;

	protected TaskbarItemProgressState _taskbarProgressState;

	protected double _taskbarProgressValue;

	protected bool _cancelEnabled;

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public Voidstrap.Bootstrapper? Bootstrapper { get; set; }

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public virtual string Message
	{
		get
		{
			return _message;
		}
		set
		{
			if (base.InvokeRequired)
			{
				Invoke(delegate
				{
					_message = value;
				});
			}
			else
			{
				_message = value;
			}
		}
	}

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public virtual ProgressBarStyle ProgressStyle
	{
		get
		{
			return _progressStyle;
		}
		set
		{
			if (base.InvokeRequired)
			{
				Invoke(delegate
				{
					_progressStyle = value;
				});
			}
			else
			{
				_progressStyle = value;
			}
		}
	}

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public virtual int ProgressMaximum
	{
		get
		{
			return _progressMaximum;
		}
		set
		{
			if (base.InvokeRequired)
			{
				Invoke(delegate
				{
					_progressMaximum = value;
				});
			}
			else
			{
				_progressMaximum = value;
			}
		}
	}

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public virtual int ProgressValue
	{
		get
		{
			return _progressValue;
		}
		set
		{
			if (base.InvokeRequired)
			{
				Invoke(delegate
				{
					_progressValue = value;
				});
			}
			else
			{
				_progressValue = value;
			}
		}
	}

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public Action? CancelCallback { get; set; }

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public virtual bool CancelEnabled
	{
		get
		{
			return _cancelEnabled;
		}
		set
		{
			if (base.InvokeRequired)
			{
				Invoke(delegate
				{
					_cancelEnabled = value;
				});
			}
			else
			{
				_cancelEnabled = value;
			}
		}
	}

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public TaskbarItemProgressState TaskbarProgressState
	{
		get
		{
			return _taskbarProgressState;
		}
		set
		{
			_taskbarProgressState = value;
			TaskbarProgress.SetProgressState(Process.GetCurrentProcess().MainWindowHandle, value);
		}
	}

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public double TaskbarProgressValue
	{
		get
		{
			return _taskbarProgressValue;
		}
		set
		{
			_taskbarProgressValue = value;
			TaskbarProgress.SetProgressValue(Process.GetCurrentProcess().MainWindowHandle, (int)value, 100);
		}
	}

	public void ScaleWindow()
	{
		Size size = (MaximumSize = WindowScaling.GetScaledSize(base.Size));
		Size size2 = (MinimumSize = size);
		base.Size = size2;
		foreach (Control control in base.Controls)
		{
			control.Size = WindowScaling.GetScaledSize(control.Size);
			control.Location = WindowScaling.GetScaledPoint(control.Location);
			control.Padding = WindowScaling.GetScaledPadding(control.Padding);
		}
	}

	public void SetupDialog()
	{
		if (LicenseManager.UsageMode != LicenseUsageMode.Designtime)
		{
			Text = App.Settings.Prop.BootstrapperTitle;
			base.Icon = App.Settings.Prop.ActiveBootstrapperIcon.GetIcon();
			if (Locale.RightToLeft)
			{
				RightToLeft = RightToLeft.Yes;
				RightToLeftLayout = true;
			}
		}
	}

	public void ButtonCancel_Click(object? sender, EventArgs e)
	{
		Close();
	}

	public void Dialog_FormClosing(object sender, FormClosingEventArgs e)
	{
		if (!_isClosing)
		{
			try { CancelCallback?.Invoke(); } catch { }
			Bootstrapper?.Cancel();
		}
	}

	public void ShowBootstrapper()
	{
		ShowDialog();
	}

	public virtual void CloseBootstrapper()
	{
		if (base.InvokeRequired)
		{
			Invoke(CloseBootstrapper);
			return;
		}
		_isClosing = true;
		Close();
	}

	public virtual void ShowSuccess(string message, Action? callback)
	{
		BaseFunctions.ShowSuccess(message, callback);
	}
}
