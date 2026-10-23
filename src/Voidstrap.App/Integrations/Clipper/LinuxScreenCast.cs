using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
#if CROSSPLAT
using Tmds.DBus;
#endif

namespace Voidstrap.Integrations.Clipper;

public sealed class LinuxScreenCastStream
{
    public int Fd { get; init; } = -1;
    public uint Node { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
}

public static partial class LinuxScreenCast
{
    private const string LogIdent = "LinuxScreenCast";

    public static bool Required =>
        Voidstrap.Utility.Platform.IsLinux
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string TokenPath => Path.Combine(Paths.Config, "ScreenCastToken");

    private static readonly SemaphoreSlim Gate = new(1, 1);

#if !CROSSPLAT
    public static Task<LinuxScreenCastStream?> AcquireAsync(bool cursor) => Task.FromResult<LinuxScreenCastStream?>(null);

    public static void Release()
    {
    }
#else
    private const string PortalService = "org.freedesktop.portal.Desktop";
    private static readonly ObjectPath PortalPath = new("/org/freedesktop/portal/desktop");

    private static Connection? _connection;
    private static ObjectPath? _session;
    private static CloseSafeHandle? _remote;
    private static LinuxScreenCastStream? _stream;
    private static bool _cursor;

    public static async Task<LinuxScreenCastStream?> AcquireAsync(bool cursor)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stream != null && _cursor == cursor && _remote is { IsInvalid: false, IsClosed: false })
                return _stream;

            ReleaseCore();
            _cursor = cursor;
            _stream = await OpenAsync(cursor).ConfigureAwait(false);
            if (_stream == null)
                ReleaseCore();
            return _stream;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Screen capture could not start: " + ex.GetBaseException().Message);
            ReleaseCore();
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Release()
    {
        Gate.Wait();
        try
        {
            ReleaseCore();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void ReleaseCore()
    {
        Connection? connection = _connection;
        ObjectPath? session = _session;
        _connection = null;
        _session = null;
        _stream = null;
        try
        {
            if (connection != null && session.HasValue)
                connection.CreateProxy<IPortalSession>(PortalService, session.Value).CloseAsync().Wait(2000);
        }
        catch
        {
        }
        try
        {
            _remote?.Dispose();
        }
        catch
        {
        }
        _remote = null;
        connection?.Dispose();
    }

    private static async Task<LinuxScreenCastStream?> OpenAsync(bool cursor)
    {
        string? address = Voidstrap.Utility.LinuxSessionBus.Address;
        if (string.IsNullOrEmpty(address))
        {
            App.Logger.WriteLine(LogIdent, "No session bus is available for screen capture");
            return null;
        }

        Connection connection = new(address);
        _connection = connection;
        ConnectionInfo info = await connection.ConnectAsync().ConfigureAwait(false);
        string sender = info.LocalName.TrimStart(':').Replace('.', '_');
        IScreenCastPortal portal = connection.CreateProxy<IScreenCastPortal>(PortalService, PortalPath);

        uint version = 0;
        uint cursorModes = 0;
        uint sourceTypes = 1;
        try
        {
            version = await portal.GetAsync<uint>("version").ConfigureAwait(false);
            cursorModes = await portal.GetAsync<uint>("AvailableCursorModes").ConfigureAwait(false);
            sourceTypes = await portal.GetAsync<uint>("AvailableSourceTypes").ConfigureAwait(false);
        }
        catch (DBusException ex)
        {
            App.Logger.WriteLine(LogIdent, "The screen capture portal is unavailable: " + ex.ErrorName);
            return null;
        }

        string sessionToken = "voidstrap" + Guid.NewGuid().ToString("N");
        IDictionary<string, object>? created = await RequestAsync(connection, sender, token => portal.CreateSessionAsync(new Dictionary<string, object>
        {
            ["handle_token"] = token,
            ["session_handle_token"] = sessionToken
        })).ConfigureAwait(false);
        if (created == null || !created.TryGetValue("session_handle", out object? handleValue) || handleValue is not string handleText)
        {
            App.Logger.WriteLine(LogIdent, "The screen capture session could not be created");
            return null;
        }
        ObjectPath session = new(handleText);
        _session = session;

        Dictionary<string, object> select = new()
        {
            ["types"] = sourceTypes & 3u,
            ["multiple"] = false
        };
        uint wantedCursor = cursor ? 2u : 1u;
        if ((cursorModes & wantedCursor) != 0)
            select["cursor_mode"] = wantedCursor;
        if (version >= 4)
        {
            select["persist_mode"] = 2u;
            string? restore = ReadToken();
            if (!string.IsNullOrEmpty(restore))
                select["restore_token"] = restore;
        }

        IDictionary<string, object>? selected = await RequestAsync(connection, sender, token =>
        {
            select["handle_token"] = token;
            return portal.SelectSourcesAsync(session, select);
        }).ConfigureAwait(false);
        if (selected == null)
        {
            App.Logger.WriteLine(LogIdent, "Screen capture source selection was declined");
            return null;
        }

        IDictionary<string, object>? started = await RequestAsync(connection, sender, token => portal.StartAsync(session, string.Empty, new Dictionary<string, object>
        {
            ["handle_token"] = token
        })).ConfigureAwait(false);
        if (started == null)
        {
            App.Logger.WriteLine(LogIdent, "Screen capture was not allowed");
            WriteToken(null);
            return null;
        }

        if (started.TryGetValue("restore_token", out object? restoreValue) && restoreValue is string restoreToken)
            WriteToken(restoreToken);

        if (!TryReadStream(started, out uint node, out uint width, out uint height))
        {
            App.Logger.WriteLine(LogIdent, "Screen capture returned no stream");
            return null;
        }

        CloseSafeHandle remote = await portal.OpenPipeWireRemoteAsync(session, new Dictionary<string, object>()).ConfigureAwait(false);
        _remote = remote;
        int fd = (int)remote.DangerousGetHandle();
        if (fd < 0 || SetDescriptorFlags(fd, 2, 0) < 0)
        {
            App.Logger.WriteLine(LogIdent, "The screen capture stream could not be shared with the encoder");
            return null;
        }

        App.Logger.WriteLine(LogIdent, $"Screen capture ready on PipeWire node {node} at {width}x{height}");
        return new LinuxScreenCastStream { Fd = fd, Node = node, Width = width, Height = height };
    }

    private static bool TryReadStream(IDictionary<string, object> results, out uint node, out uint width, out uint height)
    {
        node = 0;
        width = 0;
        height = 0;
        if (!results.TryGetValue("streams", out object? streams) || streams is not IEnumerable list)
            return false;
        foreach (object? entry in list)
        {
            if (entry is not ITuple { Length: 2 } stream || stream[0] is not uint id)
                continue;
            node = id;
            if (stream[1] is IDictionary<string, object> properties
                && properties.TryGetValue("size", out object? size)
                && size is ITuple { Length: 2 } pair
                && pair[0] is int w && pair[1] is int h && w > 0 && h > 0)
            {
                width = (uint)w;
                height = (uint)h;
            }
            return true;
        }
        return false;
    }

    private static async Task<IDictionary<string, object>?> RequestAsync(Connection connection, string sender, Func<string, Task<ObjectPath>> call)
    {
        string token = "voidstrap" + Guid.NewGuid().ToString("N");
        ObjectPath expected = new("/org/freedesktop/portal/desktop/request/" + sender + "/" + token);
        TaskCompletionSource<(uint Response, IDictionary<string, object> Results)> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable watch = await connection.CreateProxy<Voidstrap.UI.IPortalRequest>(PortalService, expected).WatchResponseAsync(
            value => completion.TrySetResult(value),
            error => completion.TrySetException(error)).ConfigureAwait(false);
        try
        {
            ObjectPath handle = await call(token).ConfigureAwait(false);
            if (handle.ToString() != expected.ToString())
            {
                watch.Dispose();
                watch = await connection.CreateProxy<Voidstrap.UI.IPortalRequest>(PortalService, handle).WatchResponseAsync(
                    value => completion.TrySetResult(value),
                    error => completion.TrySetException(error)).ConfigureAwait(false);
            }
            Task finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromMinutes(3))).ConfigureAwait(false);
            if (finished != completion.Task)
                return null;
            (uint response, IDictionary<string, object> results) = await completion.Task.ConfigureAwait(false);
            return response == 0 ? results : null;
        }
        finally
        {
            watch.Dispose();
        }
    }

    private static string? ReadToken()
    {
        try
        {
            return File.Exists(TokenPath) ? File.ReadAllText(TokenPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteToken(string? token)
    {
        try
        {
            if (string.IsNullOrEmpty(token))
            {
                if (File.Exists(TokenPath))
                    File.Delete(TokenPath);
                return;
            }
            Directory.CreateDirectory(Paths.Config);
            File.WriteAllText(TokenPath, token);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "The screen capture choice could not be saved: " + ex.Message);
        }
    }

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int SetDescriptorFlags(int fd, int command, int flags);
#endif
}

#if CROSSPLAT
[DBusInterface("org.freedesktop.portal.ScreenCast")]
public interface IScreenCastPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);

    Task<ObjectPath> SelectSourcesAsync(ObjectPath session, IDictionary<string, object> options);

    Task<ObjectPath> StartAsync(ObjectPath session, string parentWindow, IDictionary<string, object> options);

    Task<CloseSafeHandle> OpenPipeWireRemoteAsync(ObjectPath session, IDictionary<string, object> options);

    Task<T> GetAsync<T>(string prop);
}

[DBusInterface("org.freedesktop.portal.Session")]
public interface IPortalSession : IDBusObject
{
    Task CloseAsync();
}
#endif
