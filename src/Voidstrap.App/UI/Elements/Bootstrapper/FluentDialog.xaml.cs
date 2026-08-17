using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Voidstrap.Extensions;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Bootstrapper.Base;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.ViewModels.Bootstrapper;
using Wpf.Ui.Appearance;

namespace Voidstrap.UI.Elements.Bootstrapper;

public partial class FluentDialog : WpfUiWindow,IBootstrapperDialog{
	private readonly FluentDialogViewModel _viewModel;

	private bool _isClosing;

	private Window? _mainWindow;

	public Voidstrap.Bootstrapper? Bootstrapper { get; set; }

	public string? CustomBackgroundPath { get; set; }

	public string Message
	{
		get
		{
			return _viewModel.Message;
		}
		set
		{
			SetProperty("Message", value, delegate(string v)
			{
				_viewModel.Message = v;
			});
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
			SetProperty("ProgressIndeterminate", value == ProgressBarStyle.Marquee, delegate(bool v)
			{
				_viewModel.ProgressIndeterminate = v;
			});
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
			SetProperty("ProgressMaximum", value, delegate(int v)
			{
				_viewModel.ProgressMaximum = v;
			});
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
			SetProperty("ProgressValue", value, delegate(int v)
			{
				_viewModel.ProgressValue = v;
			});
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
			SetProperty("TaskbarProgressState", value, delegate(TaskbarItemProgressState v)
			{
				_viewModel.TaskbarProgressState = v;
			});
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
			SetProperty("TaskbarProgressValue", value, delegate(double v)
			{
				_viewModel.TaskbarProgressValue = v;
			});
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
		}
	}

	private void ApplyScale()
	{
		double f = App.Settings.Prop.BootstrapperScale switch
		{
			Voidstrap.Enums.BootstrapperScale.Compact => 0.85,
			Voidstrap.Enums.BootstrapperScale.Large => 1.18,
			_ => 1.0
		};
		ContentScale.ScaleX = f;
		ContentScale.ScaleY = f;
		base.Width = 500.0 * f;
		base.Height = 280.0 * f;
	}

	public FluentDialog(bool aero)
	{
		InitializeComponent();
		_viewModel = new FluentDialogViewModel(this, aero);
		base.DataContext = _viewModel;
		ApplyScale();
		_ = _viewModel.LoadProfileAsync();
		_mainWindow = System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
		if (App.Settings.Prop.BackgroundWindow)
		{
			_mainWindow?.Hide();
		}
		AudioPlayerHelper.PlayStartupAudio();
		base.Title = App.Settings.Prop.BootstrapperTitle;
		base.Icon = Voidstrap.Extensions.IconEx.GetBootstrapperWindowIcon();
		if (aero)
		{
			if (Environment.OSVersion.Version.Build >= 22000)
			{
				base.AllowsTransparency = true;
			}
			base.WindowBackdropType = Wpf.Ui.Appearance.BackgroundType.Acrylic;
			base.LocationChanged += OnLocationChanged;
			base.SizeChanged += OnSizeChanged;
			base.Loaded += OnLoaded;
		}
		else
		{
			base.WindowBackdropType = Wpf.Ui.Appearance.BackgroundType.Mica;
		}
		string text = Directory.Exists(Paths.Media) ? Directory.GetFiles(Paths.Media, "bootstrapper_bg.*").FirstOrDefault() : null;
		if (text != null)
		{
			CustomBackgroundPath = text;
		}
		SetBackgroundImage();
		BackgroundEvents.BackgroundChanged += OnBackgroundEventsChanged;
		base.Closed += OnFluentDialogClosed;
	}

	private void OnBackgroundEventsChanged(string path)
	{
		((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
		{
			_ = ChangeBackgroundAsync(path);
		});
	}

	private void OnFluentDialogClosed(object? sender, EventArgs e)
	{
		BackgroundEvents.BackgroundChanged -= OnBackgroundEventsChanged;
		base.Closed -= OnFluentDialogClosed;
		base.LocationChanged -= OnLocationChanged;
		base.SizeChanged -= OnSizeChanged;
		base.Loaded -= OnLoaded;
		BackgroundManager.Cancel(BackgroundImage);
		_mainWindow = System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
		if (App.Settings.Prop.BackgroundWindow)
		{
			_mainWindow?.Show();
		}
		AudioPlayerHelper.StopAudio();
	}

	private void OnLocationChanged(object? sender, EventArgs e)
	{
		UpdateGlassParallax();
	}

	private void OnSizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateGlassParallax();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		UpdateGlassParallax();
	}

	private void UpdateGlassParallax()
	{
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		if (GlassTintTransform == null)
		{
			return;
		}
		try
		{
			Rect workArea = SystemParameters.WorkArea;
			double num = base.Left + base.Width / 2.0;
			double num2 = base.Top + base.Height / 2.0;
			double num3 = workArea.Left + workArea.Width / 2.0;
			double num4 = workArea.Top + workArea.Height / 2.0;
			GlassTintTransform.X = Math.Clamp((num3 - num) * 1.0, -280.0, 280.0);
			GlassTintTransform.Y = Math.Clamp((num4 - num2) * 1.0, -280.0, 280.0);
		}
		catch
		{
		}
	}

	private void SetBackgroundImage()
	{
		BackgroundManager.SetBackgroundAsync(BackgroundImage, CustomBackgroundPath);
	}

	public async Task ChangeBackgroundAsync(string? newPath)
	{
		CustomBackgroundPath = newPath;
		await BackgroundManager.SetBackgroundAsync(BackgroundImage, CustomBackgroundPath);
	}

	private void UiWindow_Closing(object sender, CancelEventArgs e)
	{
		if (!_isClosing)
		{
			try { CancelCallback?.Invoke(); } catch { }
			Bootstrapper?.Cancel();
			e.Cancel = true;
		}
	}

	public void ShowBootstrapper()
	{
		ShowDialog();
	}

	public void CloseBootstrapper()
	{
		_isClosing = true;
		((DispatcherObject)this).Dispatcher.InvokeAsync((Action)base.Close, (DispatcherPriority)4);
	}

	public void ShowSuccess(string message, Action? callback = null)
	{
		BaseFunctions.ShowSuccess(message, callback);
	}

	private void SetProperty<T>(string propertyName, T value, Action<T> setter)
	{
		setter(value);
		_viewModel.OnPropertyChanged(propertyName);
	}
}
