using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Integrations;

namespace Voidstrap.Utility;

public sealed class SavedAccount
{
	public long UserId { get; set; }

	public string Username { get; set; } = "";

	public string DisplayName { get; set; } = "";

	public string Note { get; set; } = "";

	public string DatFile { get; set; } = "";

	public bool Pinned { get; set; }

	public bool Expired { get; set; }

	public DateTime AddedUtc { get; set; }

	public DateTime LastUsedUtc { get; set; }
}

public sealed record CurrentAccountInfo(long UserId, string Username, string DisplayName, string? AvatarUrl);

public static class SavedAccounts
{
	private const string LogIdent = "SavedAccounts";

	private static readonly SemaphoreSlim AccountStoreGate = new SemaphoreSlim(1, 1);

	private static readonly string[] RobloxProcessNames =
	{
		"RobloxPlayerBeta",
		"RobloxStudioBeta",
		"RobloxPlayer",
		"RobloxStudio",
		"Roblox",
	};

	private static readonly string[] LinuxRobloxProcessNames = { "sober", "Sober", "org.vinegarhq.Sober" };

	private static readonly Dictionary<long, string> AvatarCache = new Dictionary<long, string>();

	private static CurrentAccountInfo? _current;

	public static event Action? Changed;

	public static CurrentAccountInfo? Current => _current;

	public static bool SwitchingSupported => !(Platform.IsLinux && LinuxRuntimeChoice.PlayerUsesRosemary);

	private static string AccountMetaPath => Path.Combine(Paths.AccountBackups, "accounts.json");

	private static string PreviousSessionPath => Path.Combine(Paths.AccountBackups, "_previous_session.dat");

	internal static string LiveCookiePath =>
		Platform.IsLinux ? RobloxCookie.SoberCookiePath
		: Platform.IsMacOS ? Voidstrap.Platform.MacOS.MacOSBinaryCookies.RobloxPlayerStore
		: App.RobloxCookiesFilePath;

	internal static void CopyLoginFile(string source, string destination, bool overwrite = true)
	{
		byte[] data = File.ReadAllBytes(source);
		if (data.Length == 0 || data.Length > 1048576 || (!Platform.IsMacOS && Array.IndexOf(data, (byte)0) >= 0))
			throw new InvalidDataException("The Roblox login file is damaged, so it was not copied");
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				stream.Write(data);
				stream.Flush(true);
			}
			if (!OperatingSystem.IsWindows())
				File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			File.Move(temporary, destination, overwrite);
		}
		finally
		{
			File.Delete(temporary);
		}
	}

	public static string DatPath(SavedAccount account) => Path.Combine(Paths.AccountBackups, account.DatFile);

	public static string? ReadCookie(SavedAccount account) => RobloxCookie.ExtractCookieFromDat(DatPath(account));

	public static string? ReadCurrentCookie(SavedAccount account)
	{
		if (_current != null && _current.UserId == account.UserId && SwitchingSupported)
		{
			string? live = RobloxCookie.ExtractCookieFromDat(LiveCookiePath);
			if (!string.IsNullOrEmpty(live))
				return live;
		}
		return ReadCookie(account);
	}

	public static async Task RecordRotatedCookieAsync(string previous, string rotated)
	{
		if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(rotated))
			return;
		await AccountStoreGate.WaitAsync().ConfigureAwait(false);
		try
		{
			int updated = 0;
			foreach (SavedAccount account in ReadStoredAccounts())
			{
				string path = DatPath(account);
				if (
					string.Equals(RobloxCookie.ExtractCookieFromDat(path), previous, StringComparison.Ordinal)
					&& RobloxCookie.WriteLoginFile(rotated, path, path)
				)
					updated++;
			}
			string live = LiveCookiePath;
			if (
				!string.IsNullOrEmpty(live)
				&& string.Equals(RobloxCookie.ExtractCookieFromDat(live), previous, StringComparison.Ordinal)
				&& RobloxCookie.WriteLoginFile(rotated, live, live)
			)
				updated++;
			RobloxCookie.InvalidateCache();
			App.Logger?.WriteLine(LogIdent, "Saved a rotated Roblox session in " + updated + " login file(s)");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "A rotated Roblox session could not be saved: " + ex.Message);
		}
		finally
		{
			AccountStoreGate.Release();
		}
	}

	public static void RaiseChanged() => Changed?.Invoke();

	public static async Task<List<SavedAccount>> LoadAsync(CancellationToken token = default)
	{
		await AccountStoreGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			EnsureFolder();
			List<SavedAccount> stored = ReadStoredAccounts()
				.Where(a => !string.IsNullOrEmpty(a.DatFile) && File.Exists(DatPath(a)))
				.ToList();
			bool changed = await MigrateLooseBackupsAsync(stored, token).ConfigureAwait(false);
			List<SavedAccount> deduped = stored
				.GroupBy(a => a.UserId == 0 ? -stored.IndexOf(a) - 1 : a.UserId)
				.Select(g => g.OrderByDescending(a => a.LastUsedUtc).First())
				.ToList();
			if (changed || deduped.Count != stored.Count)
				Write(deduped);
			return deduped;
		}
		finally
		{
			AccountStoreGate.Release();
		}
	}

	public static async Task UpdateAsync(SavedAccount account)
	{
		await AccountStoreGate.WaitAsync().ConfigureAwait(false);
		try
		{
			List<SavedAccount> stored = ReadStoredAccounts();
			int index = stored.FindIndex(a => a.DatFile == account.DatFile);
			if (index >= 0)
				stored[index] = account;
			else
				stored.Insert(0, account);
			Write(stored);
		}
		finally
		{
			AccountStoreGate.Release();
		}
		RaiseChanged();
	}

	public static async Task RemoveAsync(SavedAccount account)
	{
		await AccountStoreGate.WaitAsync().ConfigureAwait(false);
		try
		{
			List<SavedAccount> stored = ReadStoredAccounts();
			stored.RemoveAll(a => a.DatFile == account.DatFile);
			Write(stored);
			try
			{
				File.Delete(DatPath(account));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "The saved login file could not be deleted: " + ex.Message);
			}
		}
		finally
		{
			AccountStoreGate.Release();
		}
		RaiseChanged();
	}

	public static async Task<SavedAccount?> AddFromCookieAsync(string cookie, CancellationToken token = default)
	{
		cookie = cookie.Trim();
		if (cookie.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase))
			cookie = cookie[".ROBLOSECURITY=".Length..].Trim();
		RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, token).ConfigureAwait(false);
		if (account == null)
			return null;
		await AccountStoreGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			EnsureFolder();
			List<SavedAccount> stored = ReadStoredAccounts();
			SavedAccount? existing = stored.FirstOrDefault(a => a.UserId == account.UserId);
			string datName = existing?.DatFile is { Length: > 0 } name ? name : NewDatName(account.UserId);
			string template = File.Exists(LiveCookiePath) ? LiveCookiePath : stored.Select(DatPath).FirstOrDefault(File.Exists) ?? "";
			if (!RobloxCookie.WriteLoginFile(cookie, template, Path.Combine(Paths.AccountBackups, datName)))
				return null;
			SavedAccount result =
				existing
				?? new SavedAccount
				{
					UserId = account.UserId,
					AddedUtc = DateTime.UtcNow,
					DatFile = datName,
				};
			result.Username = account.Username;
			result.DisplayName = account.DisplayName;
			result.Expired = false;
			if (existing == null)
				stored.Insert(0, result);
			Write(stored);
			App.Logger?.WriteLine(LogIdent, "Saved a Roblox account from a sign in cookie");
			return result;
		}
		finally
		{
			AccountStoreGate.Release();
			RaiseChanged();
		}
	}

	public static async Task<bool> EnsureCurrentAccountSavedAsync(CancellationToken token = default)
	{
		string live = LiveCookiePath;
		if (!SwitchingSupported || string.IsNullOrEmpty(live) || !File.Exists(live))
			return false;
		try
		{
			await AccountStoreGate.WaitAsync(token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		bool added = false;
		try
		{
			string? cookie = RobloxCookie.ExtractCookieFromDat(live);
			if (string.IsNullOrEmpty(cookie))
				return false;
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(TimeSpan.FromSeconds(10.0));
			RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, timeout.Token).ConfigureAwait(false);
			if (account == null || account.UserId == 0)
				return false;
			EnsureFolder();
			List<SavedAccount> stored = ReadStoredAccounts();
			SavedAccount? existing = stored.FirstOrDefault(item => item.UserId == account.UserId);
			string datName =
				existing != null && !string.IsNullOrWhiteSpace(existing.DatFile) ? existing.DatFile : NewDatName(account.UserId);
			CopyLoginFile(live, Path.Combine(Paths.AccountBackups, datName));
			if (existing != null)
			{
				existing.Username = account.Username;
				existing.DisplayName = account.DisplayName;
				existing.DatFile = datName;
				existing.Expired = false;
				App.Logger?.WriteLine(LogIdent, "Refreshed the saved login for the signed in account");
			}
			else
			{
				DateTime now = DateTime.UtcNow;
				stored.Insert(
					0,
					new SavedAccount
					{
						UserId = account.UserId,
						Username = account.Username,
						DisplayName = account.DisplayName,
						DatFile = datName,
						AddedUtc = now,
						LastUsedUtc = now,
					}
				);
				added = true;
				App.Logger?.WriteLine(LogIdent, "Added the signed in account to the saved accounts");
			}
			Write(stored);
			return added;
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
			if (added)
				RaiseChanged();
		}
	}

	public static async Task SwitchAsync(SavedAccount target)
	{
		if (!File.Exists(DatPath(target)))
			throw new FileNotFoundException(DatPath(target));
		await AccountStoreGate.WaitAsync().ConfigureAwait(false);
		try
		{
			string live = LiveCookiePath;
			bool backedUp = false;
			if (File.Exists(live))
			{
				try
				{
					await CopyWithRetryAsync(live, PreviousSessionPath).ConfigureAwait(false);
					backedUp = true;
					string? liveCookie = RobloxCookie.ExtractCookieFromDat(live);
					using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6.0));
					RobloxAccount? outgoingAccount = string.IsNullOrEmpty(liveCookie)
						? null
						: await RobloxCookie.GetAccountAsync(liveCookie, timeout.Token).ConfigureAwait(false);
					SavedAccount? outgoing =
						outgoingAccount == null
							? null
							: ReadStoredAccounts().FirstOrDefault(a => a.UserId == outgoingAccount.UserId && a.DatFile != target.DatFile);
					if (outgoing != null)
						await CopyWithRetryAsync(live, DatPath(outgoing)).ConfigureAwait(false);
				}
				catch (InvalidDataException)
				{
					App.Logger?.WriteLine(LogIdent, "The current Roblox login file is damaged, switching without saving it");
				}
			}
			RoReviewService.InvalidateSession();
			try
			{
				if (!await SwapLoginCookieAsync(target, live).ConfigureAwait(false))
					await CopyWithRetryAsync(DatPath(target), live).ConfigureAwait(false);
			}
			catch when (backedUp && !File.Exists(live))
			{
				await CopyWithRetryAsync(PreviousSessionPath, live).ConfigureAwait(false);
				throw;
			}
			RobloxCookie.InvalidateCache();
			RoReviewService.InvalidateSession();
			List<SavedAccount> stored = ReadStoredAccounts();
			SavedAccount? saved = stored.FirstOrDefault(a => a.DatFile == target.DatFile);
			target.LastUsedUtc = DateTime.UtcNow;
			if (saved != null)
				saved.LastUsedUtc = target.LastUsedUtc;
			Write(stored);
			AvatarCache.TryGetValue(target.UserId, out string? avatar);
			_current = new CurrentAccountInfo(target.UserId, target.Username, target.DisplayName, avatar);
			App.Logger?.WriteLine(LogIdent, "Switched the Roblox login to a saved account");
		}
		finally
		{
			AccountStoreGate.Release();
		}
		RaiseChanged();
	}

	private static async Task<bool> SwapLoginCookieAsync(SavedAccount target, string live)
	{
		if (!File.Exists(live))
			return false;
		string? cookie = ReadCookie(target);
		if (string.IsNullOrEmpty(cookie))
			return false;
		string staged = Path.Combine(Paths.AccountBackups, "switch-" + Guid.NewGuid().ToString("N") + ".tmp");
		try
		{
			if (!RobloxCookie.WriteLoginFile(cookie, live, staged))
				return false;
			await CopyWithRetryAsync(staged, live).ConfigureAwait(false);
			bool swapped = string.Equals(RobloxCookie.ExtractCookieFromDat(live), cookie, StringComparison.Ordinal);
			if (swapped)
				App.Logger?.WriteLine(LogIdent, "Swapped only the login cookie so Roblox keeps its other saved accounts");
			return swapped;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The login cookie could not be swapped in place, using the saved login file: " + ex.Message);
			return false;
		}
		finally
		{
			try
			{
				if (File.Exists(staged))
					File.Delete(staged);
			}
			catch (IOException) { }
		}
	}

	public static async Task SignOutAsync()
	{
		await AccountStoreGate.WaitAsync().ConfigureAwait(false);
		try
		{
			string live = LiveCookiePath;
			if (File.Exists(live))
			{
				await CopyWithRetryAsync(live, PreviousSessionPath).ConfigureAwait(false);
				RoReviewService.InvalidateSession();
				File.Delete(live);
				RobloxCookie.InvalidateCache();
				RoReviewService.InvalidateSession();
			}
			_current = null;
		}
		finally
		{
			AccountStoreGate.Release();
		}
		RaiseChanged();
	}

	public static async Task<CurrentAccountInfo?> RefreshCurrentAsync(CancellationToken token = default)
	{
		string? cookie = SwitchingSupported ? RobloxCookie.ExtractCookieFromDat(LiveCookiePath) : RobloxCookie.Get();
		RobloxAccount? account = string.IsNullOrEmpty(cookie)
			? null
			: await RobloxCookie.GetAccountAsync(cookie, token).ConfigureAwait(false);
		if (account == null)
		{
			bool hadOne = _current != null;
			_current = null;
			if (hadOne)
				RaiseChanged();
			return null;
		}
		Dictionary<long, string> avatars = await GetAvatarsAsync(new[] { account.UserId }, token).ConfigureAwait(false);
		avatars.TryGetValue(account.UserId, out string? avatar);
		CurrentAccountInfo info = new CurrentAccountInfo(account.UserId, account.Username, account.DisplayName, avatar);
		if (info != _current)
		{
			_current = info;
			RaiseChanged();
		}
		return info;
	}

	public static async Task<Dictionary<long, string>> GetAvatarsAsync(IEnumerable<long> userIds, CancellationToken token = default)
	{
		Dictionary<long, string> result = new Dictionary<long, string>();
		List<long> missing = new List<long>();
		lock (AvatarCache)
		{
			foreach (long id in userIds.Where(id => id > 0).Distinct())
			{
				if (AvatarCache.TryGetValue(id, out string? url))
					result[id] = url;
				else
					missing.Add(id);
			}
		}
		for (int i = 0; i < missing.Count; i += 100)
		{
			List<long> chunk = missing.GetRange(i, Math.Min(100, missing.Count - i));
			try
			{
				string url =
					"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds="
					+ string.Join(",", chunk)
					+ "&size=150x150&format=Png&isCircular=false";
				using JsonDocument doc = JsonDocument.Parse(await Http.GetString(url, token).ConfigureAwait(false));
				if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
					continue;
				foreach (JsonElement el in data.EnumerateArray())
				{
					long id = el.TryGetProperty("targetId", out JsonElement t) && t.TryGetInt64(out long l) ? l : 0;
					string img = el.TryGetProperty("imageUrl", out JsonElement iu) ? iu.GetString() ?? "" : "";
					if (id == 0 || string.IsNullOrEmpty(img))
						continue;
					result[id] = img;
					lock (AvatarCache)
						AvatarCache[id] = img;
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Avatar fetch failed: " + ex.Message);
			}
		}
		return result;
	}

	public static bool IsRobloxRunning()
	{
		if (Platform.IsMacOS)
		{
			MacRobloxProcesses.Snapshot snapshot = MacRobloxProcesses.Scan();
			if (!snapshot.GameRunning && snapshot.MenuBarHelpers.Count > 0)
				MacRobloxProcesses.CloseIdleMenuBarHelpers();
			return snapshot.GameRunning || ProcessesRunning("RobloxStudio");
		}
		if (Platform.IsLinux && Voidstrap.Platform.Linux.LinuxSoberProcessProbe.IsRunningNow())
			return true;
		IEnumerable<string> names = Platform.IsLinux ? RobloxProcessNames.Concat(LinuxRobloxProcessNames) : RobloxProcessNames;
		foreach (string name in names)
		{
			Process[] processes = Array.Empty<Process>();
			try
			{
				processes = Process.GetProcessesByName(name);
				if (processes.Any(IsAlive))
					return true;
			}
			catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
			finally
			{
				foreach (Process process in processes)
					process.Dispose();
			}
		}
		return false;
	}

	public static async Task<bool> CloseRobloxAsync()
	{
		IEnumerable<string> names = Platform.IsLinux ? RobloxProcessNames.Concat(LinuxRobloxProcessNames) : RobloxProcessNames;
		foreach (string name in names)
		{
			Process[] processes = Array.Empty<Process>();
			try
			{
				processes = Process.GetProcessesByName(name);
				foreach (Process process in processes.Where(IsAlive))
				{
					try
					{
						process.Kill(true);
					}
					catch (Exception ex)
						when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
					{
						App.Logger?.WriteLine(LogIdent, "A Roblox process could not be closed: " + ex.Message);
					}
				}
			}
			finally
			{
				foreach (Process process in processes)
					process.Dispose();
			}
		}
		for (int i = 0; i < 20 && IsRobloxRunning(); i++)
			await Task.Delay(250).ConfigureAwait(false);
		return !IsRobloxRunning();
	}

	private static bool ProcessesRunning(string name)
	{
		Process[] processes = Array.Empty<Process>();
		try
		{
			processes = Process.GetProcessesByName(name);
			return processes.Any(IsAlive);
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
		finally
		{
			foreach (Process process in processes)
				process.Dispose();
		}
	}

	internal static (IReadOnlyList<string> Names, IReadOnlyList<long> UserIds) KnownIdentities()
	{
		List<SavedAccount> stored = ReadStoredAccounts();
		List<string> names = stored
			.SelectMany(static account => new[] { account.Username, account.DisplayName })
			.Where(static name => !string.IsNullOrWhiteSpace(name) && name.Length >= 3)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		List<long> ids = stored.Select(static account => account.UserId).Where(static id => id > 0).Distinct().ToList();
		return (names, ids);
	}

	private static bool IsAlive(Process process)
	{
		try
		{
			return !process.HasExited;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	private static string NewDatName(long userId) => "acc_" + userId + "_" + Guid.NewGuid().ToString("N")[..8] + ".dat";

	private static void EnsureFolder()
	{
		Directory.CreateDirectory(Paths.AccountBackups);
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(Paths.AccountBackups, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
	}

	private static async Task CopyWithRetryAsync(string source, string destination)
	{
		for (int i = 0; ; i++)
		{
			try
			{
				CopyLoginFile(source, destination);
				return;
			}
			catch (IOException) when (i < 5)
			{
				await Task.Delay(150).ConfigureAwait(false);
			}
		}
	}

	private static async Task<bool> MigrateLooseBackupsAsync(List<SavedAccount> stored, CancellationToken token)
	{
		HashSet<string> known = new HashSet<string>(stored.Select(a => a.DatFile), StringComparer.OrdinalIgnoreCase);
		bool changed = false;
		foreach (string file in Directory.EnumerateFiles(Paths.AccountBackups, "*.dat", SearchOption.TopDirectoryOnly))
		{
			string name = Path.GetFileName(file);
			if (known.Contains(name) || name.StartsWith("_", StringComparison.Ordinal))
				continue;
			string? cookie = RobloxCookie.ExtractCookieFromDat(file);
			RobloxAccount? account = null;
			if (!string.IsNullOrEmpty(cookie))
			{
				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
				timeout.CancelAfter(TimeSpan.FromSeconds(10.0));
				account = await RobloxCookie.GetAccountAsync(cookie, timeout.Token).ConfigureAwait(false);
			}
			if (account != null && stored.Any(a => a.UserId == account.UserId))
				continue;
			stored.Add(
				new SavedAccount
				{
					UserId = account?.UserId ?? 0,
					Username = account?.Username ?? Path.GetFileNameWithoutExtension(name),
					DisplayName = account?.DisplayName ?? "",
					DatFile = name,
					Expired = account == null,
					AddedUtc = File.GetCreationTimeUtc(file),
				}
			);
			changed = true;
		}
		return changed;
	}

	private static void Write(List<SavedAccount> stored)
	{
		JsonFile.SerializeAtomic(AccountMetaPath, stored, JsonOptions.Indented);
	}

	private static List<SavedAccount> ReadStoredAccounts()
	{
		if (!File.Exists(AccountMetaPath))
			return [];
		try
		{
			return JsonFile.Deserialize<List<SavedAccount>>(AccountMetaPath, JsonOptions.Tolerant, 4194304) ?? [];
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The saved accounts could not be read: " + ex.Message);
			return [];
		}
	}
}
