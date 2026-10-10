using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Tmds.DBus;
using Voidstrap.Platform.Linux;
using Voidstrap.Resources;

namespace Voidstrap.Integrations;

[DBusInterface("org.a11y.Bus")]
public interface IAccessibilityBus : IDBusObject
{
    Task<string> GetAddressAsync();
}

[DBusInterface("org.a11y.atspi.Accessible")]
public interface IAccessibleNode : IDBusObject
{
    Task<(string, ObjectPath)[]> GetChildrenAsync();
    Task<string> GetRoleNameAsync();
    Task<uint[]> GetStateAsync();
    Task<T> GetAsync<T>(string prop);
}

[DBusInterface("org.a11y.atspi.Action")]
public interface IAccessibleAction : IDBusObject
{
    Task<bool> DoActionAsync(int index);
    Task<string> GetNameAsync(int index);
}

[DBusInterface("org.a11y.atspi.Hyperlink")]
public interface IAccessibleHyperlink : IDBusObject
{
    Task<string> GetURIAsync(int index);
}

internal static class SoberOnboarding
{
    private const string Tag = "SoberOnboarding";
    private const string FallbackTermsLink = "https://sober.vinegarhq.org/notice.txt";
    private const int MaxPresses = 6;
    private const int MaxNodes = 4000;
    private const uint SensitiveState = 1u << 24;
    private const uint ShowingState = 1u << 25;
    private static readonly TimeSpan PageDeadline = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BusDeadline = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DownloadStall = TimeSpan.FromSeconds(90);
    private static readonly Regex ViewHint = new(@"\s*Press here to view\.?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private enum TermsDecision
    {
        Agree,
        Review,
        Cancel
    }

    private sealed record AccessibleRef(string Service, ObjectPath Path)
    {
        public string Key => Service + Path;
    }

    private sealed class SoberPage
    {
        public AccessibleRef? Continue { get; set; }
        public string TermsHeading { get; set; } = string.Empty;
        public string TermsText { get; set; } = string.Empty;
        public string TermsLink { get; set; } = string.Empty;
        public bool Installing { get; set; }
        public bool IsTerms => TermsText.Length > 0;
    }

    public static void Install()
    {
        if (OperatingSystem.IsLinux())
            LinuxSoberRuntimeProvider.OnboardingAssist = RunAsync;
    }

    private static async Task<bool> RunAsync(CancellationToken token)
    {
        HashSet<nint> hidden = [];
        HashSet<string> pressed = [];
        using LinuxWindowInterop.SoberWindowCloak? cloak = LinuxWindowInterop.StartSoberCloak();
        Connection? bus = null;
        DateTime started = DateTime.UtcNow;
        DateTime nextConnect = DateTime.MinValue;
        DateTime deadline = DateTime.UtcNow + PageDeadline;
        long lastBytes = -1;
        DateTime lastProgress = DateTime.UtcNow;

        try
        {
            while (!token.IsCancellationRequested)
            {
                Hide(hidden);

                if (bus is null && DateTime.UtcNow >= nextConnect)
                {
                    bus = await ConnectAsync().ConfigureAwait(false);
                    nextConnect = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                    if (bus is null && DateTime.UtcNow - started > BusDeadline)
                    {
                        App.Logger.WriteLine(Tag, "The accessibility bus is unavailable, Sober setup needs a click");
                        Reveal(hidden, cloak);
                        return false;
                    }
                }

                if (bus is not null && pressed.Count < MaxPresses)
                {
                    SoberPage? page = await ReadPageAsync(bus, token).ConfigureAwait(false);
                    if (page?.Installing == true || pressed.Count > 0 && LinuxSoberRuntimeProvider.DownloadedRobloxBytes() > 0)
                    {
                        App.Logger.WriteLine(Tag, "Sober is downloading Roblox, showing its window");
                        Reveal(hidden, cloak);
                        return true;
                    }
                    if (page?.Continue is { } button && !pressed.Contains(button.Key))
                    {
                        if (page.IsTerms)
                        {
                            TermsDecision decision = ConfirmTerms(page);
                            if (decision != TermsDecision.Agree)
                            {
                                Reveal(hidden, cloak);
                                if (decision == TermsDecision.Cancel)
                                {
                                    App.Logger.WriteLine(Tag, "Sober setup was cancelled, closing Sober");
                                    LinuxSoberRuntimeProvider.TryCloseSober();
                                }
                                else
                                {
                                    App.Logger.WriteLine(Tag, "Showing the Sober setup so its terms can be reviewed there");
                                }
                                return false;
                            }
                        }

                        if (await PressAsync(bus, button, token).ConfigureAwait(false))
                        {
                            pressed.Add(button.Key);
                            deadline = DateTime.UtcNow + PageDeadline;
                            App.Logger.WriteLine(Tag, $"Pressed Continue in the hidden Sober window, step {pressed.Count}");
                            await Task.Delay(1500, token).ConfigureAwait(false);
                            continue;
                        }
                    }
                }

                long bytes = LinuxSoberRuntimeProvider.DownloadedRobloxBytes();
                if (bytes != lastBytes)
                {
                    lastBytes = bytes;
                    lastProgress = DateTime.UtcNow;
                }

                bool stuck = bytes > 0
                    ? DateTime.UtcNow - lastProgress > DownloadStall
                    : DateTime.UtcNow > deadline;
                if (stuck)
                {
                    App.Logger.WriteLine(Tag, "Sober needs a manual step, showing its window again");
                    Reveal(hidden, cloak);
                    return false;
                }

                await Task.Delay(hidden.Count == 0 ? 150 : 600, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(Tag, "Could not get past the Sober setup automatically: " + ex.Message);
            Reveal(hidden, cloak);
            return false;
        }
        finally
        {
            bus?.Dispose();
        }

        Reveal(hidden, cloak);
        return true;
    }

    private static TermsDecision ConfirmTerms(SoberPage page)
    {
        string accepted = page.TermsHeading + "\n" + page.TermsText;
        if (string.Equals(App.Settings.Prop.SoberTermsAccepted, accepted, StringComparison.Ordinal))
        {
            App.Logger.WriteLine(Tag, "These Sober terms were already accepted in Voidstrap, continuing");
            return TermsDecision.Agree;
        }

        if (App.LaunchSettings.QuietFlag.Active || App.LaunchSettings.WindowAuditFlag.Active)
            return TermsDecision.Review;

        string link = page.TermsLink.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? page.TermsLink : FallbackTermsLink;
        string heading = string.IsNullOrWhiteSpace(page.TermsHeading) ? "Sober license and terms" : page.TermsHeading;
        string message = "**Let Voidstrap finish setting up Sober?**\n\n"
            + "[Read Sober's license and terms](" + link + ")";

        MessageBoxResult result = Voidstrap.UI.Frontend.ShowMessageBox(message, MessageBoxImage.Information, "Agree and continue", "Set up in Sober", Strings.Common_Cancel);
        if (result == MessageBoxResult.Yes)
        {
            App.Settings.Prop.SoberTermsAccepted = accepted;
            App.Settings.SaveDeferred();
            App.Logger.WriteLine(Tag, "Sober's terms were accepted in Voidstrap: " + heading);
            return TermsDecision.Agree;
        }
        return result == MessageBoxResult.No ? TermsDecision.Review : TermsDecision.Cancel;
    }

    private static void Hide(HashSet<nint> hidden)
    {
        if (LinuxSteamOS.Current.IsGameMode)
            return;
        foreach (nint window in LinuxWindowInterop.FindSoberWindows())
        {
            LinuxWindowInterop.TryCloakWindow(window);
            if (hidden.Add(window))
                LinuxWindowInterop.TrySetHiddenFromShell(window);
        }
    }

    private static void Reveal(HashSet<nint> hidden, LinuxWindowInterop.SoberWindowCloak? cloak)
    {
        if (cloak is not null)
        {
            foreach (nint window in cloak.CloakedWindows)
                hidden.Add(window);
            cloak.Dispose();
        }
        foreach (nint window in hidden)
        {
            if (!LinuxWindowInterop.IsLiveWindow(window))
                continue;
            LinuxWindowInterop.TryUncloakWindow(window);
            LinuxWindowInterop.TryActivateWindow(window);
        }
        hidden.Clear();
    }

    private static async Task<Connection?> ConnectAsync()
    {
        foreach (string address in await CandidateAddressesAsync().ConfigureAwait(false))
        {
            Connection bus = new(address);
            try
            {
                await bus.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return bus;
            }
            catch (Exception ex)
            {
                bus.Dispose();
                App.Logger.WriteLine(Tag, "The accessibility bus at " + address + " could not be reached: " + ex.Message);
            }
        }
        return null;
    }

    private static async Task<List<string>> CandidateAddressesAsync()
    {
        List<string> addresses = [];
        if (!LinuxFlatpakHost.IsSandboxed && Voidstrap.Utility.LinuxSessionBus.Address is not null)
        {
            try
            {
                using Connection session = new(Voidstrap.Utility.LinuxSessionBus.RequireAddress());
                await session.ConnectAsync().ConfigureAwait(false);
                string address = await session.CreateProxy<IAccessibilityBus>("org.a11y.Bus", "/org/a11y/bus")
                    .GetAddressAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(address))
                    addresses.Add(address);
            }
            catch (Exception ex) when (ex is DBusException or TimeoutException or InvalidOperationException or ConnectException)
            {
            }
        }

        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtime))
        {
            try
            {
                string directory = Path.Combine(runtime, "at-spi");
                if (Directory.Exists(directory))
                {
                    foreach (string socket in Directory.GetFiles(directory, "bus*").Order(StringComparer.Ordinal))
                    {
                        string address = "unix:path=" + socket;
                        if (!addresses.Any(existing => existing.StartsWith(address, StringComparison.Ordinal)))
                            addresses.Add(address);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return addresses;
    }

    private static async Task<SoberPage?> ReadPageAsync(Connection bus, CancellationToken token)
    {
        try
        {
            IAccessibleNode root = bus.CreateProxy<IAccessibleNode>("org.a11y.atspi.Registry", "/org/a11y/atspi/accessible/root");
            foreach ((string service, ObjectPath path) in await ChildrenAsync(root, token).ConfigureAwait(false))
            {
                IAccessibleNode application = bus.CreateProxy<IAccessibleNode>(service, path);
                if (!await IsSoberAsync(bus, application, token).ConfigureAwait(false))
                    continue;
                SoberPage page = new();
                int visited = 0;
                await CollectAsync(bus, application, page, 0, () => ++visited, token).ConfigureAwait(false);
                if (page.Continue is not null || page.IsTerms || page.Installing)
                    return page;
            }
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException or DisconnectedException or ObjectDisposedException)
        {
        }
        return null;
    }

    private static async Task<bool> IsSoberAsync(Connection bus, IAccessibleNode application, CancellationToken token)
    {
        string name = await NameAsync(application, token).ConfigureAwait(false);
        if (name.Contains("sober", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach ((string service, ObjectPath path) in await ChildrenAsync(application, token).ConfigureAwait(false))
        {
            if (string.Equals(await NameAsync(bus.CreateProxy<IAccessibleNode>(service, path), token).ConfigureAwait(false), "Sober", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task CollectAsync(Connection bus, IAccessibleNode node, SoberPage page, int depth, Func<int> count, CancellationToken token)
    {
        if (depth > 40 || count() > MaxNodes)
            return;

        foreach ((string service, ObjectPath path) in await ChildrenAsync(node, token).ConfigureAwait(false))
        {
            IAccessibleNode child = bus.CreateProxy<IAccessibleNode>(service, path);
            string name = await NameAsync(child, token).ConfigureAwait(false);
            if (name.Length > 0)
            {
                if (name.StartsWith("Installing Roblox", StringComparison.OrdinalIgnoreCase))
                {
                    page.Installing = true;
                }
                else if (page.Continue is null
                    && string.Equals(name, "Continue", StringComparison.OrdinalIgnoreCase)
                    && await IsVisibleButtonAsync(child, token).ConfigureAwait(false))
                {
                    page.Continue = new AccessibleRef(service, path);
                }
                else if (page.TermsText.Length == 0
                    && (name.Contains("By continuing", StringComparison.OrdinalIgnoreCase) || name.Contains("EULA", StringComparison.OrdinalIgnoreCase)))
                {
                    page.TermsText = ViewHint.Replace(name, string.Empty).Trim();
                    page.TermsLink = await FindLinkAsync(bus, child, token).ConfigureAwait(false);
                }
                else if (page.TermsHeading.Length == 0
                    && name.Contains("Terms", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("By continuing", StringComparison.OrdinalIgnoreCase)
                    && name.Length <= 80)
                {
                    page.TermsHeading = name.Trim();
                }
            }
            await CollectAsync(bus, child, page, depth + 1, count, token).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsVisibleButtonAsync(IAccessibleNode node, CancellationToken token)
    {
        try
        {
            string role = await node.GetRoleNameAsync().WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (!string.Equals(role, "button", StringComparison.OrdinalIgnoreCase) && !string.Equals(role, "push button", StringComparison.OrdinalIgnoreCase))
                return false;
            uint[] state = await node.GetStateAsync().WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            return state.Length > 0 && (state[0] & SensitiveState) != 0 && (state[0] & ShowingState) != 0;
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException)
        {
            return false;
        }
    }

    private static async Task<string> FindLinkAsync(Connection bus, IAccessibleNode label, CancellationToken token)
    {
        foreach ((string service, ObjectPath path) in await ChildrenAsync(label, token).ConfigureAwait(false))
        {
            try
            {
                string uri = await bus.CreateProxy<IAccessibleHyperlink>(service, path)
                    .GetURIAsync(0)
                    .WaitAsync(TimeSpan.FromSeconds(3), token)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(uri))
                    return uri.Trim();
            }
            catch (Exception ex) when (ex is DBusException or TimeoutException)
            {
            }
        }
        return string.Empty;
    }

    private static async Task<bool> PressAsync(Connection bus, AccessibleRef button, CancellationToken token)
    {
        try
        {
            IAccessibleAction action = bus.CreateProxy<IAccessibleAction>(button.Service, button.Path);
            string actionName = await action.GetNameAsync(0).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (!string.Equals(actionName, "click", StringComparison.OrdinalIgnoreCase) && !string.Equals(actionName, "press", StringComparison.OrdinalIgnoreCase))
                return false;
            return await action.DoActionAsync(0).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException)
        {
            return false;
        }
    }

    private static async Task<(string, ObjectPath)[]> ChildrenAsync(IAccessibleNode node, CancellationToken token)
    {
        try
        {
            return await node.GetChildrenAsync().WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException)
        {
            return [];
        }
    }

    private static async Task<string> NameAsync(IAccessibleNode node, CancellationToken token)
    {
        try
        {
            return (await node.GetAsync<string>("Name").WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false) ?? string.Empty).Trim();
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException)
        {
            return string.Empty;
        }
    }
}
