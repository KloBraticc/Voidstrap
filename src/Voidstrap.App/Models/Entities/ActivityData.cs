using System;
using System.ComponentModel;
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

public class ActivityData : INotifyPropertyChanged
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

	[JsonIgnore]
	public string DisplayTimeJoined { get; private set; } = "Unknown";

	[JsonIgnore]
	public string DisplayTimeLeft { get; private set; } = "Unknown";

	public event PropertyChangedEventHandler? PropertyChanged;

	[JsonIgnore]
	public bool? ServerOnline { get; private set; }

	[JsonIgnore]
	public string ServerStatus { get; private set; } = Strings.ResourceManager.GetString("ContextMenu.GameHistory.StatusUnknown", Locale.CurrentCulture) ?? string.Empty;

	[JsonIgnore]
	public string ServerStatusCheckedAt { get; private set; } = string.Empty;

	public void SetServerStatus(bool? online)
	{
		ServerOnline = online;
		ServerStatus = Strings.ResourceManager.GetString("ContextMenu.GameHistory." + (online.HasValue ? online.Value ? "Online" : "Offline" : "StatusUnknown"), Locale.CurrentCulture) ?? string.Empty;
		ServerStatusCheckedAt = string.Format(Locale.CurrentCulture, Strings.ResourceManager.GetString("ContextMenu.GameHistory.CheckedAt", Locale.CurrentCulture) ?? "{0}", DateTime.Now.ToString("T", Locale.CurrentCulture));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStatus)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStatusCheckedAt)));
	}

	public long PlaceId { get; set; }

	public string JobId { get; set; } = string.Empty;

	public string AccessCode { get; set; } = string.Empty;

	public long UserId { get; set; }

	public string MachineAddress { get; set; } = string.Empty;

	public DateTimeOffset? ServerStartedUtc { get; set; }

	[JsonIgnore]
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

	[JsonIgnore]
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

	[JsonIgnore]
	public string GameHistoryDescription
	{
		get
		{
			string text = UniverseDetails?.Data?.Creator?.Name ?? string.Empty;
			if (ServerType != ServerType.Public)
			{
				text = text + " • " + ServerType.ToTranslatedString();
			}
			return text;
		}
	}

	[JsonIgnore]
	public ICommand RejoinServerCommand => new RelayCommand(RejoinServer);

	public void ComputeDisplayTimes()
	{
		DisplayTimeJoined = TimeJoined != default ? TimeJoined.ToString("g", Locale.CurrentCulture) : string.Empty;
		DisplayTimeLeft = TimeLeft?.ToString("g", Locale.CurrentCulture) ?? Voidstrap.Resources.Strings.ResourceManager.GetString("ContextMenu.GameHistory.EndUnknown", Locale.CurrentCulture) ?? string.Empty;
	}

	public string GetInviteDeeplink(bool launchData = true)
	{
		string text = $"https://www.roblox.com/games/start?placeId={PlaceId}";
		text = ((ServerType != ServerType.Private) ? (text + "&gameInstanceId=" + Uri.EscapeDataString(JobId ?? string.Empty)) : (text + "&accessCode=" + Uri.EscapeDataString(AccessCode ?? string.Empty)));
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
			string voidstrapPath = Paths.LaunchExecutable;
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
