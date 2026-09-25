using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;
using Voidstrap.Platform.Linux;

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
    Task<T> GetAsync<T>(string prop);
}

[DBusInterface("org.a11y.atspi.Action")]
public interface IAccessibleAction : IDBusObject
{
    Task<bool> DoActionAsync(int index);
    Task<string> GetNameAsync(int index);
}

internal static class SoberOnboarding
{
    private const string Tag = "SoberOnboarding";
    private const int ContinuePages = 2;
    private static readonly TimeSpan PageDeadline = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadStall = TimeSpan.FromSeconds(90);

    public static void Install()
    {
        if (OperatingSystem.IsLinux())
            LinuxSoberRuntimeProvider.OnboardingAssist = RunAsync;
    }

    private static async Task<bool> RunAsync(CancellationToken token)
    {
        HashSet<nint> hidden = [];
        HashSet<string> pressedButtons = [];
        Connection? bus = null;
        bool busFailed = false;
        int pressed = 0;
        DateTime deadline = DateTime.UtcNow + PageDeadline;
        long lastBytes = -1;
        DateTime lastProgress = DateTime.UtcNow;

        try
        {
            while (!token.IsCancellationRequested)
            {
                Hide(hidden);

                if (pressed < ContinuePages && hidden.Count > 0 && !busFailed)
                {
                    bus ??= await ConnectAsync().ConfigureAwait(false);
                    busFailed = bus is null;
                    if (bus is not null && await TryPressContinueAsync(bus, pressedButtons, token).ConfigureAwait(false))
                    {
                        pressed++;
                        deadline = DateTime.UtcNow + PageDeadline;
                        App.Logger.WriteLine(Tag, "Pressed Continue in the hidden Sober window, page " + pressed);
                        await Task.Delay(1500, token).ConfigureAwait(false);
                        continue;
                    }
                }

                long bytes = LinuxSoberRuntimeProvider.DownloadedRobloxBytes();
                if (bytes != lastBytes)
                {
                    lastBytes = bytes;
                    lastProgress = DateTime.UtcNow;
                }

                bool stuck = pressed < ContinuePages && bytes == 0
                    ? busFailed || DateTime.UtcNow > deadline
                    : DateTime.UtcNow - lastProgress > DownloadStall;
                if (stuck)
                {
                    App.Logger.WriteLine(Tag, "Sober needs a manual step, showing its window again");
                    Reveal(hidden);
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
            Reveal(hidden);
            return false;
        }
        finally
        {
            bus?.Dispose();
        }

        Reveal(hidden);
        return true;
    }

    private static void Hide(HashSet<nint> hidden)
    {
        foreach (nint window in LinuxWindowInterop.FindSoberWindows())
        {
            LinuxWindowInterop.TrySetInvisible(window);
            nint frame = LinuxWindowInterop.GetFrameWindow(window);
            if (frame != 0 && frame != window)
                LinuxWindowInterop.TrySetInvisible(frame);
            if (hidden.Add(window))
                LinuxWindowInterop.TrySetHiddenFromShell(window);
        }
    }

    private static void Reveal(HashSet<nint> hidden)
    {
        foreach (nint window in hidden)
        {
            if (!LinuxWindowInterop.IsLiveWindow(window))
                continue;
            nint frame = LinuxWindowInterop.GetFrameWindow(window);
            foreach (nint target in frame != 0 && frame != window ? new[] { window, frame } : new[] { window })
            {
                LinuxWindowInterop.TryClearShape(target);
                LinuxWindowInterop.TryResetInputShape(target);
            }
            LinuxWindowInterop.TryActivateWindow(window);
        }
        hidden.Clear();
    }

    private static async Task<Connection?> ConnectAsync()
    {
        try
        {
            using Connection session = new(Address.Session);
            await session.ConnectAsync().ConfigureAwait(false);
            string address = await session.CreateProxy<IAccessibilityBus>("org.a11y.Bus", "/org/a11y/bus")
                .GetAddressAsync()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Connection bus = new(address);
            await bus.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return bus;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(Tag, "The accessibility bus is unavailable, Sober setup needs a click: " + ex.Message);
            return null;
        }
    }

    private static async Task<bool> TryPressContinueAsync(Connection bus, HashSet<string> pressed, CancellationToken token)
    {
        try
        {
            IAccessibleNode root = bus.CreateProxy<IAccessibleNode>("org.a11y.atspi.Registry", "/org/a11y/atspi/accessible/root");
            foreach ((string service, ObjectPath path) in await root.GetChildrenAsync().WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
            {
                IAccessibleNode application = bus.CreateProxy<IAccessibleNode>(service, path);
                if (!await IsSoberAsync(bus, application, token).ConfigureAwait(false))
                    continue;
                if (await PressInAsync(bus, application, pressed, 0, token).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false))
                    return true;
            }
        }
        catch (Exception ex) when (ex is DBusException or TimeoutException)
        {
        }
        return false;
    }

    private static async Task<bool> IsSoberAsync(Connection bus, IAccessibleNode application, CancellationToken token)
    {
        string name = await NameAsync(application, token).ConfigureAwait(false);
        if (name.Contains("sober", StringComparison.OrdinalIgnoreCase) || name.Contains("vinegar", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach ((string service, ObjectPath path) in await ChildrenAsync(application, token).ConfigureAwait(false))
        {
            if (string.Equals(await NameAsync(bus.CreateProxy<IAccessibleNode>(service, path), token).ConfigureAwait(false), "Sober", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<bool> PressInAsync(Connection bus, IAccessibleNode node, HashSet<string> pressed, int depth, CancellationToken token)
    {
        if (depth > 30)
            return false;

        foreach ((string service, ObjectPath path) in await ChildrenAsync(node, token).ConfigureAwait(false))
        {
            string key = service + path;
            if (pressed.Contains(key))
                continue;
            IAccessibleNode child = bus.CreateProxy<IAccessibleNode>(service, path);
            if (string.Equals(await NameAsync(child, token).ConfigureAwait(false), "Continue", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    IAccessibleAction action = bus.CreateProxy<IAccessibleAction>(service, path);
                    if (await action.GetNameAsync(0).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false) == "click"
                        && await action.DoActionAsync(0).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
                    {
                        pressed.Add(key);
                        return true;
                    }
                }
                catch (Exception ex) when (ex is DBusException or TimeoutException)
                {
                }
            }
            if (await PressInAsync(bus, child, pressed, depth + 1, token).ConfigureAwait(false))
                return true;
        }
        return false;
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
