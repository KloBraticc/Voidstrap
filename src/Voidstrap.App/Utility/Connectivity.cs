using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Voidstrap.Utility;

internal static class Connectivity
{
	private const string ProbeUrl = "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer";

	private const int ProbeCacheMs = 15000;

	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

	private static readonly ConcurrentDictionary<string, byte> Warned = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

	private static long _lastProbeTicks;

	private static int _lastOnline = 1;

	public static bool IsNetworkAvailable => NetworkInterface.GetIsNetworkAvailable();

	public static bool LastKnownOnline => Volatile.Read(ref _lastOnline) == 1;

	public static async Task<bool> IsOnlineAsync(CancellationToken token = default)
	{
		if (!IsNetworkAvailable)
		{
			Volatile.Write(ref _lastOnline, 0);
			return false;
		}
		if (Environment.TickCount64 - Interlocked.Read(ref _lastProbeTicks) < ProbeCacheMs)
		{
			return LastKnownOnline;
		}
		bool online;
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(ProbeTimeout);
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, ProbeUrl);
			using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
			online = true;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Connectivity::Probe", "Offline: " + ex.Message);
			online = false;
		}
		Interlocked.Exchange(ref _lastProbeTicks, Environment.TickCount64);
		Volatile.Write(ref _lastOnline, online ? 1 : 0);
		return online;
	}

	public static bool IsConnectionFailure(Exception? ex)
	{
		for (int i = 0; i < 8 && ex != null; i++)
		{
			if (ex is HttpRequestException { StatusCode: null } || ex is System.Net.Sockets.SocketException || ex is System.Net.WebException)
			{
				return true;
			}
			ex = ex.InnerException;
		}
		return !IsNetworkAvailable;
	}

	public static void WarnOffline(string feature)
	{
		if (!Warned.TryAdd(feature, 0))
		{
			return;
		}
		Volatile.Write(ref _lastOnline, 0);
		App.Logger.WriteLine("Connectivity::WarnOffline", feature + " could not be saved because Voidstrap is offline");
		try
		{
			Frontend.ShowBalloonTip("Voidstrap is offline", feature + " could not be saved. Everything else keeps working, and it will sync again once you are back online.", ToolTipIcon.Warning, 8);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Connectivity::WarnOffline", ex.Message);
		}
	}

	public static void ResetWarnings()
	{
		Warned.Clear();
	}
}
