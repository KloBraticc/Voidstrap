using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Extensions;

namespace Voidstrap.UI.ViewModels.Bootstrapper;

public class BootstrapperDialogViewModel : NotifyPropertyChangedViewModel
{
    private readonly IBootstrapperDialog? _dialog;

    public ICommand CancelInstallCommand => new RelayCommand(CancelInstall);

    public string Title => App.Settings.Prop.BootstrapperTitle;

    public ImageSource Icon { get; set; } = Voidstrap.Extensions.IconEx.GetBootstrapperWindowIcon();

    public string Message { get; set; } = "Please wait..";

    public bool ProgressIndeterminate { get; set; } = true;

    public int ProgressMaximum { get; set; }

    public int ProgressValue { get; set; }

    public TaskbarItemProgressState TaskbarProgressState { get; set; } = TaskbarItemProgressState.Indeterminate;

    public double TaskbarProgressValue { get; set; }

    public bool CancelEnabled { get; set; }

    public Visibility CancelButtonVisibility
    {
        get
        {
            if (!CancelEnabled)
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }
    }

    [Obsolete("Do not use this! This is for the designer only.", true)]
    public BootstrapperDialogViewModel()
    {
        _dialog = null;
    }

    public BootstrapperDialogViewModel(IBootstrapperDialog dialog)
    {
        _dialog = dialog;
        StartIconAnimation();
    }

    private IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> _iconFrames = [];

    private DispatcherTimer? _iconTimer;

    private int _iconFrame;

    private void StartIconAnimation()
    {
        if (App.Settings.Prop.ActiveBootstrapperIcon != Voidstrap.Enums.BootstrapperIcon.IconCustom)
            return;
        string path = App.Settings.Prop.BootstrapperIconCustomLocation;
        if (!Voidstrap.Utility.AnimatedIcon.IsAnimatable(path))
            return;
        _iconFrames = Voidstrap.Utility.AnimatedIcon.Load(path, 256);
        if (_iconFrames.Count < 2)
        {
            if (_iconFrames.Count == 1)
                Icon = _iconFrames[0].Frame;
            return;
        }
        Icon = _iconFrames[0].Frame;
        _iconTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_iconFrames[0].DelayMilliseconds) };
        _iconTimer.Tick += OnIconTick;
        _iconTimer.Start();
        if (_dialog is Window window)
            window.Closed += OnDialogClosed;
    }

    private void OnIconTick(object? sender, EventArgs e)
    {
        if (_iconTimer == null || _iconFrames.Count == 0)
            return;
        _iconFrame = (_iconFrame + 1) % _iconFrames.Count;
        Icon = _iconFrames[_iconFrame].Frame;
        OnPropertyChanged(nameof(Icon));
        _iconTimer.Interval = TimeSpan.FromMilliseconds(_iconFrames[_iconFrame].DelayMilliseconds);
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        if (sender is Window window)
            window.Closed -= OnDialogClosed;
        if (_iconTimer != null)
        {
            _iconTimer.Stop();
            _iconTimer.Tick -= OnIconTick;
            _iconTimer = null;
        }
        _iconFrames = [];
    }

    private void CancelInstall()
    {
        if (_dialog == null) return;
        try { _dialog.CancelCallback?.Invoke(); } catch { }
        _dialog.Bootstrapper?.Cancel();
        _dialog.CloseBootstrapper();
    }
}
