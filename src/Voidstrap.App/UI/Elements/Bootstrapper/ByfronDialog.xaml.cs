using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.UI.Elements.Bootstrapper.Base;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.ViewModels.Bootstrapper;

namespace Voidstrap.UI.Elements.Bootstrapper;

public partial class ByfronDialog : Window,IBootstrapperDialog{
	private readonly ByfronDialogViewModel _viewModel;

	private Window? _mainWindow;

	private bool _isClosing;

	public Voidstrap.Bootstrapper? Bootstrapper { get; set; }

	public string Message
	{
		get
		{
			return _viewModel.Message;
		}
		set
		{
			string text = value;
			if (text.EndsWith("..."))
			{
				string text2 = text;
				text = text2.Substring(0, text2.Length - 3);
			}
			_viewModel.Message = text;
			_viewModel.OnPropertyChanged("Message");
		}
	}

	public ProgressBarStyle ProgressStyle
	{
		get
		{
			if (!_viewModel.ProgressIndeterminate)
			{
				return ProgressBarStyle.Continuous;
			}
			return ProgressBarStyle.Marquee;
		}
		set
		{
			_viewModel.ProgressIndeterminate = value == ProgressBarStyle.Marquee;
			_viewModel.OnPropertyChanged("ProgressIndeterminate");
		}
	}

	public int ProgressMaximum
	{
		get
		{
			return _viewModel.ProgressMaximum;
		}
		set
		{
			_viewModel.ProgressMaximum = value;
			_viewModel.OnPropertyChanged("ProgressMaximum");
		}
	}

	public int ProgressValue
	{
		get
		{
			return _viewModel.ProgressValue;
		}
		set
		{
			_viewModel.ProgressValue = value;
			_viewModel.OnPropertyChanged("ProgressValue");
		}
	}

	public TaskbarItemProgressState TaskbarProgressState
	{
		get
		{
			return _viewModel.TaskbarProgressState;
		}
		set
		{
			_viewModel.TaskbarProgressState = value;
			_viewModel.OnPropertyChanged("TaskbarProgressState");
		}
	}

	public double TaskbarProgressValue
	{
		get
		{
			return _viewModel.TaskbarProgressValue;
		}
		set
		{
			_viewModel.TaskbarProgressValue = value;
			_viewModel.OnPropertyChanged("TaskbarProgressValue");
		}
	}

	public Action? CancelCallback { get; set; }

	public bool CancelEnabled
	{
		get
		{
			return _viewModel.CancelEnabled;
		}
		set
		{
			_viewModel.CancelEnabled = value;
			_viewModel.OnPropertyChanged("CancelEnabled");
			_viewModel.OnPropertyChanged("CancelButtonVisibility");
			_viewModel.OnPropertyChanged("VersionTextVisibility");
			_viewModel.OnPropertyChanged("VersionText");
		}
	}

	public ByfronDialog()
	{
		_viewModel = new ByfronDialogViewModel(this, Utilities.GetRobloxVersion(Bootstrapper?.IsStudioLaunch ?? false));
		base.DataContext = _viewModel;
		base.Title = App.Settings.Prop.BootstrapperTitle;
		base.Icon = Voidstrap.Extensions.IconEx.GetBootstrapperWindowIcon();
		if (App.Settings.Prop.Theme2.GetFinal() == Theme.Light)
		{
			_viewModel.DialogBorder = new Thickness(1.0);
			_viewModel.Background = new SolidColorBrush(Color.FromRgb(242, 244, 245));
			_viewModel.Foreground = new SolidColorBrush(Color.FromRgb(57, 59, 61));
			_viewModel.IconColor = new SolidColorBrush(Color.FromRgb(57, 59, 61));
			_viewModel.ProgressBarBackground = new SolidColorBrush(Color.FromRgb(189, 190, 190));
			_viewModel.ByfronLogoLocation = Voidstrap.Utility.SafeImaging.FromUri(new Uri("pack://application:,,,/Resources/BootstrapperStyles/ByfronDialog/ByfronLogoLight.jpg"));
		}
		Voidstrap.UI.RoundedWindowChrome.Prepare(this);
		InitializeComponent();
		_mainWindow = System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
		if (App.Settings.Prop.BackgroundWindow)
		{
			_mainWindow?.Hide();
		}
		AudioPlayerHelper.PlayStartupAudio();
		base.Closed += OnClosed;
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		base.Closed -= OnClosed;
		_mainWindow = System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
		if (App.Settings.Prop.BackgroundWindow)
		{
			_mainWindow?.Show();
		}
		AudioPlayerHelper.StopAudio();
	}

	private void Window_Closing(object sender, CancelEventArgs e)
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

	public void CloseBootstrapper()
	{
		_isClosing = true;
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(base.Close), Array.Empty<object>());
	}

	public void ShowSuccess(string message, Action? callback)
	{
		BaseFunctions.ShowSuccess(message, callback);
	}
}
