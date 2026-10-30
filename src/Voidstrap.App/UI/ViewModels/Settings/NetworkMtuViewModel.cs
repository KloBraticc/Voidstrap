using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Models.Persistable;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed class NetworkMtuViewModel : NotifyPropertyChangedViewModel
{
    public sealed record Adapter(Guid Id, int Index, bool IsIPv6, string Name, uint Mtu, bool Preferred)
    {
        public string Key => Id.ToString("D") + (IsIPv6 ? "/IPv6" : "/IPv4");
        public string DisplayName => Name + (IsIPv6 ? " (IPv6)" : " (IPv4)");
        public uint Minimum => IsIPv6 ? 1280u : 576u;
    }

    private Adapter? _selectedAdapter;
    private string _customMtu = string.Empty;
    private string _status = string.Empty;
    private bool _busy;

    public IReadOnlyList<Adapter> Adapters { get; private set; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }

    public NetworkMtuViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApply);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, CanRestore);
    }

    public bool IsIdle => !_busy;

    public Adapter? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (!SetProperty(ref _selectedAdapter, value))
                return;
            CustomMtu = value?.Mtu.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Status = string.Empty;
            OnPropertyChanged(nameof(CurrentSummary));
            OnPropertyChanged(nameof(ValidationMessage));
            NotifyCommands();
        }
    }

    public string CustomMtu
    {
        get => _customMtu;
        set
        {
            if (!SetProperty(ref _customMtu, value))
                return;
            OnPropertyChanged(nameof(ValidationMessage));
            NotifyCommands();
        }
    }

    public string CurrentSummary =>
        SelectedAdapter is { } adapter
            ? string.Format(CultureInfo.CurrentCulture, Strings.Mtu_Current, adapter.Mtu, adapter.Minimum)
            : Strings.Mtu_NoAdapters;

    public string ValidationMessage =>
        SelectedAdapter is { } adapter && !TryValidateMtu(CustomMtu, adapter.IsIPv6, out _)
            ? string.Format(CultureInfo.CurrentCulture, Strings.Mtu_Invalid, adapter.Minimum, uint.MaxValue)
            : string.Empty;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public static bool TryValidateMtu(string value, bool isIPv6, out uint mtu) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out mtu) && mtu >= (isIPv6 ? 1280u : 576u);

    public static List<Adapter> ReadAdapters()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return [];
        List<Adapter> adapters = [];
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback || !Guid.TryParse(network.Id, out Guid id))
                continue;
            try
            {
                IPInterfaceProperties properties = network.GetIPProperties();
                bool preferred = network.OperationalStatus == OperationalStatus.Up && properties.GatewayAddresses.Count > 0;
                if (network.Supports(NetworkInterfaceComponent.IPv4))
                {
                    IPv4InterfaceProperties? ipv4 = properties.GetIPv4Properties();
                    if (ipv4 is { Index: > 0, Mtu: >= 576 })
                        adapters.Add(new(id, ipv4.Index, false, network.Name, (uint)ipv4.Mtu, preferred));
                }
                if (network.Supports(NetworkInterfaceComponent.IPv6))
                {
                    IPv6InterfaceProperties? ipv6 = properties.GetIPv6Properties();
                    if (ipv6 is { Index: > 0, Mtu: >= 1280 })
                        adapters.Add(new(id, ipv6.Index, true, network.Name, (uint)ipv6.Mtu, preferred));
                }
            }
            catch (NetworkInformationException) { }
        }
        return
        [
            .. adapters
                .OrderByDescending(adapter => adapter.Preferred)
                .ThenBy(adapter => adapter.IsIPv6)
                .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    public static ProcessStartInfo CreateStartInfo(Adapter adapter, uint mtu)
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            throw new PlatformNotSupportedException();
        if (adapter.Id == Guid.Empty || adapter.Index <= 0 || mtu < adapter.Minimum)
            throw new ArgumentOutOfRangeException(nameof(mtu));
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (
            string argument in new[]
            {
                "interface",
                adapter.IsIPv6 ? "ipv6" : "ipv4",
                "set",
                "subinterface",
                "interface=" + adapter.Index.ToString(CultureInfo.InvariantCulture),
                "mtu=" + mtu.ToString(CultureInfo.InvariantCulture),
                "store=persistent",
            }
        )
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private bool CanRefresh() => Voidstrap.Utility.Platform.IsWindows && IsIdle;

    private bool CanApply() =>
        CanRefresh() && SelectedAdapter is { } adapter && TryValidateMtu(CustomMtu, adapter.IsIPv6, out uint mtu) && mtu != adapter.Mtu;

    private bool CanRestore() =>
        CanRefresh()
        && SelectedAdapter is { } adapter
        && App.Settings.Prop.OriginalNetworkMtuValues.TryGetValue(adapter.Key, out uint original)
        && original >= adapter.Minimum
        && original != adapter.Mtu;

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(IsIdle));
        NotifyCommands();
    }

    private async Task ReloadAsync(CancellationToken token)
    {
        string? key = SelectedAdapter?.Key;
        List<Adapter> adapters = await Task.Run(ReadAdapters, token);
        token.ThrowIfCancellationRequested();
        Adapters = adapters;
        OnPropertyChanged(nameof(Adapters));
        SelectedAdapter = adapters.Find(adapter => adapter.Key == key) ?? adapters.FirstOrDefault();
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        if (!CanRefresh())
            return;
        SetBusy(true);
        try
        {
            Status = Strings.Mtu_Loading;
            await ReloadAsync(token);
            Status = Adapters.Count == 0 ? Strings.Mtu_NoAdapters : string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = string.Empty;
        }
        catch (Exception exception)
        {
            App.Logger?.WriteException("NetworkMtu", exception);
            Status = Strings.Mtu_Error;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task ApplyAsync() => ChangeAsync(false);

    private Task RestoreAsync() => ChangeAsync(true);

    private async Task ChangeAsync(bool restore)
    {
        Adapter? selected = SelectedAdapter;
        if (!Voidstrap.Utility.Platform.IsWindows || selected == null || !IsIdle)
            return;
        uint requested;
        if (restore)
        {
            if (!App.Settings.Prop.OriginalNetworkMtuValues.TryGetValue(selected.Key, out requested))
                return;
        }
        else if (!TryValidateMtu(CustomMtu, selected.IsIPv6, out requested))
        {
            return;
        }
        SetBusy(true);
        try
        {
            Adapter? current = (await Task.Run(ReadAdapters)).Find(adapter => adapter.Key == selected.Key);
            if (current == null)
            {
                Status = Strings.Mtu_AdapterUnavailable;
                return;
            }
            ProcessStartInfo startInfo = CreateStartInfo(current, requested);
            if (!App.Settings.Prop.OriginalNetworkMtuValues.ContainsKey(current.Key))
            {
                App.Settings.Prop.OriginalNetworkMtuValues[current.Key] = current.Mtu;
            }
            App.Settings.Save();
            AppSettings saved = JsonFile.Deserialize<AppSettings>(App.Settings.FileLocation, JsonOptions.Tolerant);
            if (
                saved.OriginalNetworkMtuValues == null
                || !saved.OriginalNetworkMtuValues.TryGetValue(current.Key, out uint original)
                || original != App.Settings.Prop.OriginalNetworkMtuValues[current.Key]
            )
                throw new InvalidDataException(Strings.Mtu_Error);
            Status = Strings.Mtu_Applying;
            using Process process = await Task.Run(() => Process.Start(startInfo)) ?? throw new InvalidOperationException();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill();
                throw;
            }
            await ReloadAsync(CancellationToken.None);
            if (process.ExitCode != 0)
            {
                Status = string.Format(CultureInfo.CurrentCulture, Strings.Mtu_Rejected, process.ExitCode);
                return;
            }
            Adapter? applied = Adapters.FirstOrDefault(adapter => adapter.Key == current.Key);
            Status =
                applied?.Mtu == requested
                    ? string.Format(CultureInfo.CurrentCulture, restore ? Strings.Mtu_Restored : Strings.Mtu_Applied, requested)
                    : Strings.Mtu_VerificationFailed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            Status = Strings.Mtu_Cancelled;
        }
        catch (Exception exception)
        {
            App.Logger?.WriteException("NetworkMtu", exception);
            try
            {
                await ReloadAsync(CancellationToken.None);
            }
            catch (Exception refreshException)
            {
                App.Logger?.WriteException("NetworkMtu", refreshException);
            }
            Status = Strings.Mtu_Error;
        }
        finally
        {
            SetBusy(false);
        }
    }
}
