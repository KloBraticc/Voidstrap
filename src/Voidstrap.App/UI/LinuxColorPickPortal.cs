#if CROSSPLAT
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using RinColor;
using Tmds.DBus;

namespace Voidstrap.UI;

internal sealed class LinuxColorPickPortal : IRinScreenColorPicker
{
	private const string PortalService = "org.freedesktop.portal.Desktop";
	private const int ProbeTimeoutMs = 1500;

	private static readonly ObjectPath PortalPath = new("/org/freedesktop/portal/desktop");

	private readonly Task<bool> _probe = Task.Run(ProbeAsync);

	public bool IsAvailable
	{
		get
		{
			try
			{
				return _probe.Wait(ProbeTimeoutMs) && _probe.Result;
			}
			catch (AggregateException)
			{
				return false;
			}
		}
	}

	public async Task<Color?> PickAsync()
	{
		try
		{
			return await Task.Run(PickWithPortalAsync).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxColorPickPortal::PickAsync", $"The colour picker portal failed: {ex.GetBaseException().Message}");
			return null;
		}
	}

	private static async Task<bool> ProbeAsync()
	{
		try
		{
			string? address = Voidstrap.Utility.LinuxSessionBus.Address;
			if (address == null)
				return false;
			using Connection connection = new(address);
			await connection.ConnectAsync().ConfigureAwait(false);
			IScreenshotPortal portal = connection.CreateProxy<IScreenshotPortal>(PortalService, PortalPath);
			uint version = await portal.GetAsync<uint>("version").ConfigureAwait(false);
			return version >= 2;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxColorPickPortal::Probe", $"The colour picker portal is unavailable: {ex.GetBaseException().Message}");
			return false;
		}
	}

	private static async Task<Color?> PickWithPortalAsync()
	{
		using Connection connection = new(Voidstrap.Utility.LinuxSessionBus.RequireAddress());
		ConnectionInfo info = await connection.ConnectAsync().ConfigureAwait(false);

		string token = "voidstrap" + Guid.NewGuid().ToString("N");
		string sender = info.LocalName.TrimStart(':').Replace('.', '_');
		ObjectPath expected = new("/org/freedesktop/portal/desktop/request/" + sender + "/" + token);
		TaskCompletionSource<(uint Response, IDictionary<string, object> Results)> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

		IPortalRequest pending = connection.CreateProxy<IPortalRequest>(PortalService, expected);
		IDisposable watch = await pending.WatchResponseAsync(
			value => completion.TrySetResult(value),
			error => completion.TrySetException(error)).ConfigureAwait(false);
		try
		{
			IScreenshotPortal portal = connection.CreateProxy<IScreenshotPortal>(PortalService, PortalPath);
			Dictionary<string, object> options = new() { ["handle_token"] = token };
			ObjectPath handle = await portal.PickColorAsync(string.Empty, options).ConfigureAwait(false);
			if (handle.ToString() != expected.ToString())
			{
				watch.Dispose();
				watch = await connection.CreateProxy<IPortalRequest>(PortalService, handle).WatchResponseAsync(
					value => completion.TrySetResult(value),
					error => completion.TrySetException(error)).ConfigureAwait(false);
			}

			(uint response, IDictionary<string, object> results) = await completion.Task.ConfigureAwait(false);
			if (response != 0 || !results.TryGetValue("color", out object? value))
				return null;
			return TryReadColor(value, out Color color) ? color : null;
		}
		finally
		{
			watch.Dispose();
		}
	}

	private static bool TryReadColor(object value, out Color color)
	{
		color = default;
		List<double> parts = [];
		if (value is ITuple tuple)
		{
			for (int i = 0; i < tuple.Length; i++)
				if (tuple[i] is double part)
					parts.Add(part);
		}
		else if (value is IEnumerable items)
		{
			foreach (object? item in items)
				if (item is double part)
					parts.Add(part);
		}
		if (parts.Count < 3)
			return false;
		color = Color.FromRgb(ToByte(parts[0]), ToByte(parts[1]), ToByte(parts[2]));
		return true;
	}

	private static byte ToByte(double component)
	{
		return (byte)Math.Round(Math.Clamp(component, 0, 1) * 255);
	}
}

[DBusInterface("org.freedesktop.portal.Screenshot")]
public interface IScreenshotPortal : IDBusObject
{
	Task<ObjectPath> PickColorAsync(string parentWindow, IDictionary<string, object> options);

	Task<T> GetAsync<T>(string prop);
}
#endif
