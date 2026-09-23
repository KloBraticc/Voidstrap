using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Integrations;

namespace Voidstrap.Utility;

public static class SavedAccounts
{
	private const string LogIdent = "SavedAccounts";

	private static readonly SemaphoreSlim AccountStoreGate = new SemaphoreSlim(1, 1);

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

	internal static void CopyLoginFile(string source, string destination, bool overwrite = true)
	{
		byte[] data = File.ReadAllBytes(source);
		if (data.Length == 0 || data.Length > 1048576 || Array.IndexOf(data, (byte)0) >= 0)
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
			if (Platform.IsLinux)
				File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			File.Move(temporary, destination, overwrite);
		}
		finally
		{
			File.Delete(temporary);
		}
	}

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
				return false;
			}
			Directory.CreateDirectory(Paths.AccountBackups);
			List<StoredAccount> stored = ReadStoredAccounts();
			StoredAccount? existing = stored.FirstOrDefault(item => item.UserId == account.UserId);
			string datName = existing != null && !string.IsNullOrWhiteSpace(existing.DatFile)
				? existing.DatFile
				: "acc_" + account.UserId + "_" + Guid.NewGuid().ToString("N")[..8] + ".dat";
			CopyLoginFile(live, Path.Combine(Paths.AccountBackups, datName));
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
}
