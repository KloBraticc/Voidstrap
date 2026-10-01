using System;
using System.Threading.Tasks;
using System.Windows.Media;
#if CROSSPLAT
using Tmds.DBus;
#endif

namespace Voidstrap.Utility;

internal static class LinuxAppearancePortal
{
#if !CROSSPLAT
	public static Color? ReadAccent() => null;

	public static void WatchAccent(Action<Color> changed)
	{
	}

	public static void StopWatching()
	{
	}
#else
	private const string LogIdent = "LinuxAppearancePortal";

	private const string PortalService = "org.freedesktop.portal.Desktop";

	private const string AppearanceNamespace = "org.freedesktop.appearance";

	private const string GnomeInterfaceNamespace = "org.gnome.desktop.interface";

	private const string AccentKey = "accent-color";

	private static readonly ObjectPath PortalPath = new("/org/freedesktop/portal/desktop");

	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

	private static readonly object WatchGate = new();

	private static Connection? _watchConnection;

	private static IDisposable? _watch;

	private static bool _watching;

	public static Color? ReadAccent()
	{
		string? address = Address.Session;
		if (string.IsNullOrEmpty(address))
			return null;
		try
		{
			Task<Color?> read = Task.Run(() => ReadAccentAsync(address));
			if (read.Wait(ReadTimeout))
				return read.Result;
			App.Logger?.WriteLine(LogIdent, "The desktop portal did not answer the accent request in time");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The desktop portal accent could not be read: " + ex.GetBaseException().Message);
		}
		return null;
	}

	public static void WatchAccent(Action<Color> changed)
	{
		string? address = Address.Session;
		if (string.IsNullOrEmpty(address))
			return;
		lock (WatchGate)
		{
			if (_watching)
				return;
			_watching = true;
		}
		_ = WatchAsync(address, changed);
	}

	public static void StopWatching()
	{
		lock (WatchGate)
		{
			_watch?.Dispose();
			_watch = null;
			_watchConnection?.Dispose();
			_watchConnection = null;
			_watching = false;
		}
	}

	private static async Task<Color?> ReadAccentAsync(string address)
	{
		using Connection connection = new(address);
		await connection.ConnectAsync().ConfigureAwait(false);
		ISettingsPortal settings = connection.CreateProxy<ISettingsPortal>(PortalService, PortalPath);
		Color? exact = ToColor(AppearanceNamespace, await ReadSettingAsync(settings, AppearanceNamespace).ConfigureAwait(false));
		if (exact.HasValue)
			return exact;
		return ToColor(GnomeInterfaceNamespace, await ReadSettingAsync(settings, GnomeInterfaceNamespace).ConfigureAwait(false));
	}

	private static async Task<object?> ReadSettingAsync(ISettingsPortal settings, string ns)
	{
		try
		{
			return await settings.ReadOneAsync(ns, AccentKey).ConfigureAwait(false);
		}
		catch (DBusException ex) when (ex.ErrorName == "org.freedesktop.DBus.Error.UnknownMethod")
		{
		}
		catch (DBusException)
		{
			return null;
		}
		try
		{
			return await settings.ReadAsync(ns, AccentKey).ConfigureAwait(false);
		}
		catch (DBusException)
		{
			return null;
		}
	}

	private static async Task WatchAsync(string address, Action<Color> changed)
	{
		Connection? connection = null;
		try
		{
			connection = new Connection(address);
			await connection.ConnectAsync().ConfigureAwait(false);
			ISettingsPortal settings = connection.CreateProxy<ISettingsPortal>(PortalService, PortalPath);
			IDisposable watch = await settings.WatchSettingChangedAsync(change =>
			{
				if (change.Key != AccentKey)
					return;
				Color? color = ToColor(change.Namespace, change.Value);
				if (color.HasValue)
					changed(color.Value);
			}, ex => App.Logger?.WriteLine(LogIdent, "Accent change notifications stopped: " + ex.GetBaseException().Message)).ConfigureAwait(false);
			lock (WatchGate)
			{
				if (!_watching)
				{
					watch.Dispose();
					connection.Dispose();
					return;
				}
				_watch = watch;
				_watchConnection = connection;
			}
		}
		catch (Exception ex)
		{
			connection?.Dispose();
			App.Logger?.WriteLine(LogIdent, "Accent changes cannot be followed: " + ex.GetBaseException().Message);
		}
	}

	private static Color? ToColor(string ns, object? value)
	{
		if (ns == AppearanceNamespace && value is System.Runtime.CompilerServices.ITuple { Length: 3 } rgb
			&& TryChannel(rgb[0], out byte red) && TryChannel(rgb[1], out byte green) && TryChannel(rgb[2], out byte blue))
		{
			return Color.FromRgb(red, green, blue);
		}
		if (ns == GnomeInterfaceNamespace && value is string name)
			return SystemAccent.FromGnomeName(name);
		return null;
	}

	private static bool TryChannel(object? value, out byte channel)
	{
		channel = 0;
		if (value is not double component || double.IsNaN(component) || component < 0d || component > 1d)
			return false;
		channel = (byte)Math.Round(component * 255d);
		return true;
	}
#endif
}
#if CROSSPLAT

[DBusInterface("org.freedesktop.portal.Settings")]
public interface ISettingsPortal : IDBusObject
{
	Task<object> ReadOneAsync(string ns, string key);

	Task<object> ReadAsync(string ns, string key);

	Task<IDisposable> WatchSettingChangedAsync(Action<(string Namespace, string Key, object Value)> handler, Action<Exception>? onError = null);
}
#endif
