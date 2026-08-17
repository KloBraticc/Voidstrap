using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoidstrapClient.Common;

public class SecureSettings
{
	private const long MaxEncryptedSettingsBytes = 4194304;

	private static readonly object SettingsLock = new object();

	private static SecureSettings? _default;

	public static SecureSettings Default
	{
		get
		{
			lock (SettingsLock)
			{
				return _default ??= Load();
			}
		}
	}

	public static bool Initialized => _default != null;

	public string RobloxCookie { get; set; } = "";

	private static string? DecryptData(byte[] encryptedData)
	{
		byte[]? bytes = null;
		try
		{
			bytes = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
			return Encoding.UTF8.GetString(bytes);
		}
		catch (CryptographicException value)
		{
			Logger.Instance.Error($"Failed to decrypt SecureSettings: {value}");
			return null;
		}
		finally
		{
			if (bytes is not null)
				CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private byte[] EncryptData()
	{
		string s = JsonSerializer.Serialize(this);
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		try
		{
			return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static SecureSettings Load()
	{
		try
		{
			FileInfo file = new FileInfo(PathHelper.SecureSettings);
			if (!file.Exists)
				return new SecureSettings();
			if (file.Length <= 0 || file.Length > MaxEncryptedSettingsBytes)
			{
				Logger.Instance.Error("SecureSettings has an invalid size");
				return new SecureSettings();
			}

			string? text = DecryptData(File.ReadAllBytes(file.FullName));
			if (text == null)
				return new SecureSettings();

			SecureSettings? settings = JsonSerializer.Deserialize<SecureSettings>(text);
			return settings ?? new SecureSettings();
		}
		catch (Exception value) when (value is IOException || value is UnauthorizedAccessException || value is JsonException)
		{
			Logger.Instance.Error($"Failed to load SecureSettings: {value}");
			return new SecureSettings();
		}
	}

	public static void Save()
	{
		lock (SettingsLock)
		{
			_default?.SaveInternal();
		}
	}

	private void SaveInternal()
	{
		byte[] data = EncryptData();
		if (data.Length > MaxEncryptedSettingsBytes)
			throw new InvalidDataException("SecureSettings exceeds the maximum size");

		string destination = PathHelper.SecureSettings;
		string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllBytes(temporary, data);
			File.Move(temporary, destination, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}
}
