using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.AppData;
using Voidstrap.Enums;
using Voidstrap.Exceptions;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Models.APIs;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.Utility;

namespace Voidstrap.Models.Entities;

public class ActivityData
{
	public class UserLog
	{
		public string UserId { get; set; } = "Unknown";

		public string Username { get; set; } = "Unknown";

		public string Type { get; set; } = "Unknown";

		public DateTime Time { get; set; } = DateTime.Now;
	}

	public class UserMessage
	{
		public string Sender { get; set; } = "Unknown";

		public string Channel { get; set; } = "Game";

		public string Message { get; set; } = "Unknown";

		public DateTime Time { get; set; } = DateTime.Now;
	}

	private long _universeId;

	[JsonIgnore]
	public ActivityData? RootActivity;

	private SemaphoreSlim serverQuerySemaphore = new SemaphoreSlim(1, 1);

	public long UniverseId
	{
		get
		{
			return _universeId;
		}
		set
		{
			if (_universeId != value)
			{
				_universeId = value;
				if (UniverseDetails == null)
				{
					UniverseDetails = Voidstrap.Models.Entities.UniverseDetails.LoadFromCache(value);
				}
			}
		}
	}

	public string DisplayTimeJoined { get; private set; } = "Unknown";

	public string DisplayTimeLeft { get; private set; } = "Unknown";

	public string ServerStatus { get; private set; } = "Offline";

	public long PlaceId { get; set; }

	public string JobId { get; set; } = string.Empty;

	public string AccessCode { get; set; } = string.Empty;

	public long UserId { get; set; }

	public string MachineAddress { get; set; } = string.Empty;

	public bool MachineAddressValid
	{
		get
		{
			return !string.IsNullOrWhiteSpace(MachineAddress) && !VoidstrapMatchmaker.IsPrivateIp(MachineAddress);
		}
	}

	public bool IsTeleport { get; set; }

	public ServerType ServerType { get; set; }

	public DateTime TimeJoined { get; set; }

	public DateTime? TimeLeft { get; set; }

	public string RPCLaunchData { get; set; } = string.Empty;

	public UniverseDetails? UniverseDetails { get; set; }

	public string GameName
	{
		get
		{
			string? name = UniverseDetails?.Data?.Name;
			if (!string.IsNullOrWhiteSpace(name))
			{
				return name;
			}
			if (PlaceId != 0)
			{
				return "Place " + PlaceId;
			}
			return "Unknown";
		}
	}

	[JsonIgnore]
	public Dictionary<int, UserLog> PlayerLogs { get; internal set; } = new Dictionary<int, UserLog>();

	[JsonIgnore]
	public Dictionary<int, UserMessage> MessageLogs { get; internal set; } = new Dictionary<int, UserMessage>();

	public string GameHistoryDescription
	{
		get
		{
			InlineArray4<object> buffer = default(InlineArray4<object>);
			buffer[0] = UniverseDetails?.Data?.Creator?.Name ?? "Unknown creator";
			buffer[1] = TimeJoined.ToString("t");
			buffer[2] = (Locale.CurrentCulture.Name.StartsWith("ja") ? '~' : '-');
			buffer[3] = TimeLeft?.ToString("t") ?? "?";
			string text = string.Format("{0} • {1} {2} {3}", (ReadOnlySpan<object?>)buffer);
			if (ServerType != ServerType.Public)
			{
				text = text + " • " + ServerType.ToTranslatedString();
			}
			return text;
		}
	}

	public ICommand RejoinServerCommand => new RelayCommand(RejoinServer);

	public void ComputeDisplayTimes()
	{
		DisplayTimeJoined = ((TimeJoined != default(DateTime)) ? TimeJoined.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown");
		bool flag = !TimeLeft.HasValue || (DateTime.Now - TimeLeft.Value).TotalHours < 24.0;
		DisplayTimeLeft = (TimeLeft.HasValue ? TimeLeft.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Still Online");
		ServerStatus = (flag ? "Online" : "Offline");
	}

	public string GetInviteDeeplink(bool launchData = true)
	{
		string text = $"https://www.roblox.com/games/start?placeId={PlaceId}";
		text = ((ServerType != ServerType.Private) ? (text + "&gameInstanceId=" + JobId) : (text + "&accessCode=" + AccessCode));
		if (launchData && !string.IsNullOrEmpty(RPCLaunchData))
		{
			text = text + "&launchData=" + HttpUtility.UrlEncode(RPCLaunchData);
		}
		return text;
	}

	public string GetNativeJoinUri(bool launchData = true)
	{
		string uri = $"roblox://experiences/start?placeId={PlaceId}";
		if (ServerType == ServerType.Private && !string.IsNullOrEmpty(AccessCode))
		{
			uri += "&accessCode=" + Uri.EscapeDataString(AccessCode);
		}
		else if (!string.IsNullOrEmpty(JobId))
		{
			uri += "&gameInstanceId=" + Uri.EscapeDataString(JobId);
		}
		if (launchData && !string.IsNullOrEmpty(RPCLaunchData))
		{
			uri += "&launchData=" + Uri.EscapeDataString(RPCLaunchData);
		}
		return uri;
	}

	public async Task<string?> QueryServerLocation(CancellationToken token = default)
	{
		await serverQuerySemaphore.WaitAsync(token);
		try
		{
			string address = MachineAddress;
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			(string Ip, int Port)? resolved = await VoidstrapMatchmaker.ResolveJobServerAsync(PlaceId, JobId, timeout.Token).ConfigureAwait(false);
			if (resolved.HasValue && !VoidstrapMatchmaker.IsPrivateIp(resolved.Value.Ip))
			{
				address = resolved.Value.Ip;
				MachineAddress = address;
			}
			if (string.IsNullOrWhiteSpace(address) || VoidstrapMatchmaker.IsPrivateIp(address))
				return null;
			if (GlobalCache.TryGetServerLocation(address, out string? value))
			{
				return UnpackCachedLocation(value);
			}
			RobloxDatacenter? dc = await VoidstrapMatchmaker.LookupUnknownIpAsync(address, timeout.Token).ConfigureAwait(false);
			if (dc == null || string.IsNullOrEmpty(dc.Country))
			{
				App.Logger.WriteLine("ActivityData::QueryServerLocation", "Failed to get server location for " + address);
				return null;
			}
			string iso = VoidstrapMatchmaker.CountryToIso2(dc.Country);
			string country = VoidstrapMatchmaker.CountryToDisplayName(dc.Country);
			string location = string.IsNullOrEmpty(dc.City)
				? country
				: string.IsNullOrEmpty(dc.Region) || string.Equals(dc.City, dc.Region, StringComparison.OrdinalIgnoreCase) || string.Equals(dc.City, country, StringComparison.OrdinalIgnoreCase)
					? dc.City + ", " + country
					: dc.City + ", " + dc.Region + ", " + country;
			ServerCountryCode = iso;
			GlobalCache.SetServerLocation(address, iso + LocationCacheSeparator + location);
			ServerFetchStore.RecordSighting(address, dc.City, dc.Region, dc.Country, dc.Lat, dc.Lon);
			return location;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ActivityData::QueryServerLocation", "Failed to get server location for " + MachineAddress);
			App.Logger.WriteException("ActivityData::QueryServerLocation", ex);
			return null;
		}
		finally
		{
			serverQuerySemaphore.Release();
		}
	}

	private const char LocationCacheSeparator = '\u001F';

	[JsonIgnore]
	public string ServerCountryCode { get; private set; } = string.Empty;

	private string? UnpackCachedLocation(string? cached)
	{
		if (string.IsNullOrEmpty(cached))
			return cached;
		int sep = cached.IndexOf(LocationCacheSeparator);
		if (sep < 0)
			return cached;
		ServerCountryCode = cached.Substring(0, sep);
		return cached.Substring(sep + 1);
	}

	public override string ToString()
	{
		return $"{PlaceId}/{JobId}";
	}

	private void RejoinServer()
	{
		try
		{
			string voidstrapPath = Paths.Process;
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = voidstrapPath,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = System.IO.Path.GetDirectoryName(voidstrapPath) ?? ""
			};
			startInfo.ArgumentList.Add("-player");
			startInfo.ArgumentList.Add(GetNativeJoinUri(launchData: false));
			Process.Start(startInfo);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ActivityData::RejoinServer", ex);
		}
	}
}
