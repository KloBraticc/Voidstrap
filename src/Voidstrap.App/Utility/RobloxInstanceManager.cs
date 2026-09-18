using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Integrations;

namespace Voidstrap.Utility;

public sealed class RobloxInstanceInfo
{
	public int ProcessId { get; init; }

	public string Product { get; init; } = "Roblox";

	public DateTime StartedAt { get; init; }

	public long MemoryBytes { get; init; }

	public string WindowTitle { get; init; } = "";

	public string Uptime
	{
		get
		{
			TimeSpan elapsed = DateTime.Now - StartedAt;
			if (elapsed.TotalSeconds < 0)
			{
				return "just now";
			}
			if (elapsed.TotalHours >= 1.0)
			{
				return (int)elapsed.TotalHours + "h " + elapsed.Minutes + "m";
			}
			if (elapsed.TotalMinutes >= 1.0)
			{
				return (int)elapsed.TotalMinutes + "m";
			}
			return (int)elapsed.TotalSeconds + "s";
		}
	}

	public string MemoryDisplay => MemoryBytes <= 0 ? "" : (MemoryBytes / 1048576L) + " MB";

	public string Detail
	{
		get
		{
			string memory = MemoryDisplay;
			string started = "started " + Uptime + " ago";
			return memory.Length == 0 ? "PID " + ProcessId + ", " + started : "PID " + ProcessId + ", " + memory + ", " + started;
		}
	}
}

public sealed class RobloxInstanceAccount : System.ComponentModel.INotifyPropertyChanged
{
	private string? _avatarUrl;

	public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

	public string? AvatarUrl
	{
		get => _avatarUrl;
		set
		{
			if (!string.Equals(_avatarUrl, value, StringComparison.Ordinal))
			{
				_avatarUrl = value;
				PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(AvatarUrl)));
			}
		}
	}

	public long UserId { get; init; }

	public long AvatarUserId { get; init; }

	public string Username { get; init; } = "";

	public string DisplayName { get; init; } = "";

	public string DatFile { get; init; } = "";

	public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

	public string Subtitle => UserId == 0 ? "The account currently signed in" : "@" + Username;
}

public static partial class RobloxInstanceManager
{
	private const string LogIdent = "RobloxInstanceManager";

	private static readonly string[] ProcessNames = ["RobloxPlayerBeta", "RobloxStudioBeta"];

	private static readonly SemaphoreSlim LaunchGate = new SemaphoreSlim(1, 1);

	[LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetForegroundWindow(IntPtr hWnd);

	[LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ShowWindow(IntPtr hWnd, int command);

	[LibraryImport("user32.dll", EntryPoint = "IsIconic")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsIconic(IntPtr hWnd);

	private static readonly SemaphoreSlim AccountStoreGate = new SemaphoreSlim(1, 1);

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> AvatarCache = new();

	private static RobloxAccount? _liveAccount;

	public static RobloxAccount? LiveAccount => Volatile.Read(ref _liveAccount);

	public static async Task LoadAvatarsAsync(IEnumerable<RobloxInstanceAccount> accounts, CancellationToken token = default)
	{
		List<RobloxInstanceAccount> list = [.. accounts.Where(account => account.AvatarUserId != 0)];
		List<long> missing = [.. list.Select(account => account.AvatarUserId).Where(id => !AvatarCache.ContainsKey(id)).Distinct()];
		for (int i = 0; i < missing.Count; i += 100)
		{
			List<long> chunk = missing.GetRange(i, Math.Min(100, missing.Count - i));
			string url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + string.Join(",", chunk) + "&size=150x150&format=Png&isCircular=false";
			try
			{
				using JsonDocument document = JsonDocument.Parse(await Http.GetString(url, token).ConfigureAwait(false));
				if (document.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement item in data.EnumerateArray())
					{
						long id = item.TryGetProperty("targetId", out JsonElement target) && target.TryGetInt64(out long parsed) ? parsed : 0;
						string image = item.TryGetProperty("imageUrl", out JsonElement imageUrl) ? imageUrl.GetString() ?? "" : "";
						if (id != 0 && image.Length > 0)
						{
							AvatarCache[id] = image;
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Account avatars could not be loaded: " + ex.Message);
			}
		}
		foreach (RobloxInstanceAccount account in list)
		{
			if (AvatarCache.TryGetValue(account.AvatarUserId, out string? image))
			{
				account.AvatarUrl = image;
			}
		}
	}

	private sealed class StoredAccount
	{
		public long UserId { get; set; }

		public string Username { get; set; } = "";

		public string DisplayName { get; set; } = "";

		public string Note { get; set; } = "";

		public string DatFile { get; set; } = "";

		public DateTime AddedUtc { get; set; }

		public DateTime LastUsedUtc { get; set; }
	}

	private static string AccountMetaPath => Path.Combine(Paths.AccountBackups, "accounts.json");

	private static string LiveCookiePath => Platform.IsLinux ? RobloxCookie.SoberCookiePath : App.RobloxCookiesFilePath;

	public static async Task<bool> EnsureCurrentAccountSavedAsync(CancellationToken token = default)
	{
		string live = LiveCookiePath;
		if (string.IsNullOrEmpty(live) || !File.Exists(live))
		{
			return false;
		}
		try
		{
			await AccountStoreGate.WaitAsync(token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		try
		{
			RobloxCookie.InvalidateCache();
			string? cookie = RobloxCookie.Get();
			if (string.IsNullOrEmpty(cookie))
			{
				return false;
			}
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(TimeSpan.FromSeconds(10.0));
			RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, timeout.Token).ConfigureAwait(false);
			if (account == null || account.UserId == 0)
			{
				Volatile.Write(ref _liveAccount, null);
				return false;
			}
			Volatile.Write(ref _liveAccount, account);
			Directory.CreateDirectory(Paths.AccountBackups);
			List<StoredAccount> stored = ReadStoredAccounts();
			StoredAccount? existing = stored.FirstOrDefault(item => item.UserId == account.UserId);
			string datName = existing != null && !string.IsNullOrWhiteSpace(existing.DatFile)
				? existing.DatFile
				: "acc_" + account.UserId + "_" + Guid.NewGuid().ToString("N")[..8] + ".dat";
			File.Copy(live, Path.Combine(Paths.AccountBackups, datName), overwrite: true);
			if (existing != null)
			{
				existing.Username = account.Username;
				existing.DisplayName = account.DisplayName;
				existing.DatFile = datName;
				App.Logger?.WriteLine(LogIdent, "Refreshed the saved login for " + account.Username);
			}
			else
			{
				DateTime now = DateTime.UtcNow;
				stored.Insert(0, new StoredAccount
				{
					UserId = account.UserId,
					Username = account.Username,
					DisplayName = account.DisplayName,
					DatFile = datName,
					AddedUtc = now,
					LastUsedUtc = now
				});
				App.Logger?.WriteLine(LogIdent, "Added the signed in account " + account.Username + " to the saved accounts");
			}
			JsonFile.SerializeAtomic(AccountMetaPath, stored, JsonOptions.Indented);
			return existing == null;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The signed in account could not be saved: " + ex.Message);
			return false;
		}
		finally
		{
			AccountStoreGate.Release();
		}
	}

	private static List<StoredAccount> ReadStoredAccounts()
	{
		if (!File.Exists(AccountMetaPath))
		{
			return [];
		}
		try
		{
			return JsonSerializer.Deserialize<List<StoredAccount>>(File.ReadAllText(AccountMetaPath)) ?? [];
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The saved accounts could not be read: " + ex.Message);
			return [];
		}
	}

	public static IReadOnlyList<RobloxInstanceInfo> GetRunningInstances()
	{
		List<RobloxInstanceInfo> instances = [];
		foreach (string name in ProcessNames)
		{
			Process[] processes;
			try
			{
				processes = Process.GetProcessesByName(name);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Could not inspect " + name + ": " + ex.Message);
				continue;
			}
			foreach (Process process in processes)
			{
				using (process)
				{
					try
					{
						instances.Add(new RobloxInstanceInfo
						{
							ProcessId = process.Id,
							Product = name == "RobloxStudioBeta" ? "Roblox Studio" : "Roblox",
							StartedAt = process.StartTime,
							MemoryBytes = process.WorkingSet64,
							WindowTitle = process.MainWindowTitle ?? ""
						});
					}
					catch
					{
					}
				}
			}
		}
		return [.. instances.OrderBy(instance => instance.StartedAt)];
	}

	public static IReadOnlyList<RobloxInstanceAccount> GetAccounts()
	{
		RobloxAccount? live = LiveAccount;
		string liveName = live == null ? "" : string.IsNullOrWhiteSpace(live.DisplayName) ? live.Username : live.DisplayName;
		List<RobloxInstanceAccount> accounts =
		[
			new RobloxInstanceAccount
			{
				Username = "Current account",
				DisplayName = liveName.Length == 0 ? "" : liveName + " (signed in)",
				AvatarUserId = live?.UserId ?? 0,
				AvatarUrl = live != null && AvatarCache.TryGetValue(live.UserId, out string? liveAvatar) ? liveAvatar : null
			}
		];
		try
		{
			foreach (StoredAccount account in ReadStoredAccounts())
			{
				if (account.UserId == 0 || string.IsNullOrWhiteSpace(account.DatFile))
				{
					continue;
				}
				accounts.Add(new RobloxInstanceAccount
				{
					UserId = account.UserId,
					Username = account.Username,
					DisplayName = account.DisplayName,
					DatFile = account.DatFile,
					AvatarUserId = account.UserId,
					AvatarUrl = AvatarCache.TryGetValue(account.UserId, out string? avatar) ? avatar : null
				});
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The saved accounts could not be read: " + ex.Message);
		}
		return accounts;
	}

	public static async Task<string?> LaunchAsync(RobloxInstanceAccount? account, bool studio)
	{
		if (!await LaunchGate.WaitAsync(0).ConfigureAwait(false))
		{
			return "Another instance is already starting, give it a moment.";
		}
		try
		{
			if (!MultiInstanceLock.Enabled)
			{
				return "Multi instance launching is off. Turn it on from the Deployment page first.";
			}
			if (!MultiInstanceLock.IsHeld)
			{
				MultiInstanceLock.Acquire();
			}
			if (account != null && account.UserId != 0)
			{
				string? error = await ApplyAccountAsync(account).ConfigureAwait(false);
				if (error != null)
				{
					return error;
				}
			}
			return StartProcess(studio);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The instance could not be started: " + ex.Message);
			return ex.Message;
		}
		finally
		{
			LaunchGate.Release();
		}
	}

	private static async Task<string?> ApplyAccountAsync(RobloxInstanceAccount account)
	{
		string source = Path.Combine(Paths.AccountBackups, account.DatFile);
		if (!File.Exists(source))
		{
			return "The saved login for " + account.Title + " is missing, add the account again from the account switcher.";
		}
		string target = App.RobloxCookiesFilePath;
		if (string.IsNullOrEmpty(target))
		{
			return "The Roblox login file location is unknown.";
		}
		await EnsureCurrentAccountSavedAsync().ConfigureAwait(false);
		for (int attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Copy(source, target, overwrite: true);
				RobloxCookie.InvalidateCache();
				App.Logger?.WriteLine(LogIdent, "Signed in as " + account.Username + " for the next instance");
				return null;
			}
			catch (IOException) when (attempt < 4)
			{
				await Task.Delay(200).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				return "The account could not be applied: " + ex.Message;
			}
		}
		return "The Roblox login file is in use and could not be replaced.";
	}

	private static string? StartProcess(bool studio)
	{
		string executable = File.Exists(Paths.Application) ? Paths.Application : Paths.Process;
		if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
		{
			return "Voidstrap could not find its own executable to start another instance.";
		}
		try
		{
			App.Settings.FlushDeferred();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The settings could not be flushed before starting the instance: " + ex.Message);
		}
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
		};
		startInfo.ArgumentList.Add(studio ? "-studio" : "-player");
		startInfo.Environment[MultiInstanceLock.EnvironmentFlag] = "1";
		try
		{
			using Process? started = Process.Start(startInfo);
			App.Logger?.WriteLine(LogIdent, "Started another " + (studio ? "Roblox Studio" : "Roblox") + " instance");
			return null;
		}
		catch (Exception ex)
		{
			return "The instance could not be started: " + ex.Message;
		}
	}

	public static bool Focus(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			IntPtr handle = process.MainWindowHandle;
			if (handle == IntPtr.Zero)
			{
				return false;
			}
			if (IsIconic(handle))
			{
				ShowWindow(handle, 9);
			}
			return SetForegroundWindow(handle);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The instance could not be focused: " + ex.Message);
			return false;
		}
	}

	public static bool Close(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			process.Kill();
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The instance could not be closed: " + ex.Message);
			return false;
		}
	}

	public static int CloseAll()
	{
		int closed = 0;
		foreach (RobloxInstanceInfo instance in GetRunningInstances())
		{
			if (Close(instance.ProcessId))
			{
				closed++;
			}
		}
		return closed;
	}
}
