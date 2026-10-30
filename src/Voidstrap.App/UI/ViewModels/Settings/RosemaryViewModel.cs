using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Platform.Linux;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed class RosemaryViewModel : NotifyPropertyChangedViewModel
{
    public sealed record Choice(string Value, string Display);

    private bool _busy;
    private string _progress = "";

    public RosemaryViewModel()
    {
        UpdateCommand = new AsyncRelayCommand(UpdateAsync);
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public ICommand UpdateCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public IReadOnlyList<Choice> SwapIntervalChoices =>
    [
        new Choice("", Strings.Menu_Rosemary_VSync_Default),
        new Choice("0", Strings.Menu_Rosemary_VSync_Off),
        new Choice("1", Strings.Menu_Rosemary_VSync_On),
        new Choice("-1", Strings.Menu_Rosemary_VSync_Adaptive)
    ];

    public IReadOnlyList<Choice> DecorationChoices =>
    [
        new Choice("client", Strings.Menu_Rosemary_TitleBar_Mac),
        new Choice("server", Strings.Menu_Rosemary_TitleBar_Desktop)
    ];

    public IReadOnlyList<Choice> CommandKeyChoices { get; } =
    [
        new Choice("super", "Super"),
        new Choice("ctrl", "Ctrl"),
        new Choice("alt", "Alt")
    ];

    public string RuntimeStatus => _busy && _progress.Length > 0
        ? _progress
        : LinuxRosemary.IsRuntimeInstalled
            ? string.Format(Strings.Menu_Rosemary_Installed, LinuxRosemary.InstalledRuntimeVersion)
            : Strings.Menu_Rosemary_NotInstalled;

    public string PlayerStatus => LinuxRosemary.IsApplicationInstalled(RosemaryApplication.Player)
        ? string.Format(Strings.Menu_Rosemary_RobloxInstalled, LinuxRosemary.InstalledRobloxVersion(RosemaryApplication.Player))
        : Strings.Menu_Rosemary_RobloxNotInstalled;

    public string StudioStatus => LinuxRosemary.IsApplicationInstalled(RosemaryApplication.Studio)
        ? string.Format(Strings.Menu_Rosemary_StudioInstalled, LinuxRosemary.InstalledRobloxVersion(RosemaryApplication.Studio))
        : Strings.Menu_Rosemary_StudioNotInstalled;

    public bool CanUpdate => !_busy;

    public string UpdateLabel => _busy ? Strings.Menu_Rosemary_Updating : Strings.Menu_Rosemary_Update;

    public Choice SwapIntervalChoice
    {
        get => SwapIntervalChoices.FirstOrDefault(choice => choice.Value == App.Settings.Prop.RosemarySwapInterval) ?? SwapIntervalChoices[0];
        set
        {
            if (value == null || value.Value == App.Settings.Prop.RosemarySwapInterval)
                return;
            App.Settings.Prop.RosemarySwapInterval = value.Value;
            App.Settings.Save();
            OnPropertyChanged(nameof(SwapIntervalChoice));
        }
    }

    public Choice DecorationChoice
    {
        get => DecorationChoices.FirstOrDefault(choice => choice.Value == App.Settings.Prop.RosemaryDecorations) ?? DecorationChoices[0];
        set
        {
            if (value == null || value.Value == App.Settings.Prop.RosemaryDecorations)
                return;
            App.Settings.Prop.RosemaryDecorations = value.Value;
            App.Settings.Save();
            OnPropertyChanged(nameof(DecorationChoice));
        }
    }

    public Choice CommandKeyChoice
    {
        get => CommandKeyChoices.FirstOrDefault(choice => choice.Value == App.Settings.Prop.RosemaryCommandKey) ?? CommandKeyChoices[0];
        set
        {
            if (value == null || value.Value == App.Settings.Prop.RosemaryCommandKey)
                return;
            App.Settings.Prop.RosemaryCommandKey = value.Value;
            App.Settings.Save();
            OnPropertyChanged(nameof(CommandKeyChoice));
        }
    }

    public bool ConfinePointer
    {
        get => App.Settings.Prop.RosemaryConfinePointer;
        set
        {
            if (value == App.Settings.Prop.RosemaryConfinePointer)
                return;
            App.Settings.Prop.RosemaryConfinePointer = value;
            App.Settings.Save();
            OnPropertyChanged(nameof(ConfinePointer));
        }
    }

    public bool PlainControlClick
    {
        get => App.Settings.Prop.RosemaryPlainControlClick;
        set
        {
            if (value == App.Settings.Prop.RosemaryPlainControlClick)
                return;
            App.Settings.Prop.RosemaryPlainControlClick = value;
            App.Settings.Save();
            OnPropertyChanged(nameof(PlainControlClick));
        }
    }

    public bool ForceOpenGl
    {
        get => App.Settings.Prop.RosemaryForceOpenGl;
        set
        {
            if (value == App.Settings.Prop.RosemaryForceOpenGl)
                return;
            App.Settings.Prop.RosemaryForceOpenGl = value;
            App.Settings.Save();
            OnPropertyChanged(nameof(ForceOpenGl));
        }
    }

    private async Task UpdateAsync()
    {
        if (_busy)
            return;
        SetBusy(true, "");
        try
        {
            Voidstrap.Platform.OperationResult<string> runtime = await LinuxRosemary.EnsureRuntimeAsync(ReportProgress);
            if (!runtime.Succeeded)
            {
                Frontend.ShowMessageBox(runtime.Failure?.Message ?? "Rosemary could not be updated.", MessageBoxImage.Error);
                return;
            }
            RosemaryRobloxOptions options = new(App.Settings.Prop.IsChannelEnabled ? App.Settings.Prop.Channel : null);
            foreach (RosemaryApplication application in new[] { RosemaryApplication.Player, RosemaryApplication.Studio })
            {
                if (!LinuxRosemary.IsApplicationInstalled(application) || LinuxRosemary.RunningProcessIds(application).Count > 0)
                    continue;
                Voidstrap.Platform.OperationResult<string> roblox = await LinuxRosemary.EnsureRobloxAsync(application, options, ReportProgress);
                if (!roblox.Succeeded)
                    App.Logger.WriteLine("RosemaryViewModel::Update", roblox.Failure?.Message ?? "Roblox could not be updated");
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("RosemaryViewModel::Update", ex);
            Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private void ReportProgress(string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            _progress = message;
            OnPropertyChanged(nameof(RuntimeStatus));
        }));
    }

    private void SetBusy(bool busy, string progress)
    {
        _busy = busy;
        _progress = progress;
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(UpdateLabel));
        OnPropertyChanged(nameof(RuntimeStatus));
        OnPropertyChanged(nameof(PlayerStatus));
        OnPropertyChanged(nameof(StudioStatus));
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(LinuxRosemary.Root);
            Utilities.ShellExecute(LinuxRosemary.Root);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("RosemaryViewModel::OpenFolder", "The Rosemary folder could not be opened: " + ex.Message);
        }
    }
}
