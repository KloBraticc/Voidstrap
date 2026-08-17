using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.Utility;

namespace Voidstrap;

public class JsonManager<T> where T : class, new()
{
	public T OriginalProp { get; set; } = new T();

	public T Prop { get; set; } = new T();

	public virtual string ClassName => typeof(T).Name;

	public string? LastFileHash { get; private set; }

	public virtual string BackupsLocation => Path.Combine(Paths.Config, "Backup.json");

	public virtual string FileLocation => Path.Combine(Paths.Config, ClassName + ".json");

	public virtual string LOG_IDENT_CLASS => "JsonManager<" + ClassName + ">";

	public virtual void Load(bool alertFailure = true)
	{
		string identifier = LOG_IDENT_CLASS + "::Load";
		App.Logger.WriteLine(identifier, "Loading from " + FileLocation + "...");
		if (!File.Exists(FileLocation))
		{
			App.Logger.WriteLine(identifier, "File does not exist, saving defaults.");
			Save();
			return;
		}
		if (JsonFile.TryLoad<T>(FileLocation, JsonOptions.Tolerant, out T? loaded, out bool recovered, out Exception? failure) && loaded != null)
		{
			Prop = loaded;
			LastFileHash = SafeGetFileHash(FileLocation);
			App.Logger.WriteLine(identifier, recovered ? "Recovered from the last valid backup" : "Loaded successfully");
			if (recovered && alertFailure)
				Frontend.ShowMessageBox(ClassName + " contained invalid JSON and was recovered from the last valid backup.", MessageBoxImage.Exclamation);
			return;
		}
		if (failure != null)
			App.Logger.WriteException(identifier, failure);
		if (failure != null && Voidstrap.Utility.CloudFiles.IsCloudFailure(failure, FileLocation))
		{
			App.Logger.WriteLine(identifier, "The file could not be read from the cloud provider, keeping the stored copy untouched");
			if (alertFailure)
			{
				Frontend.ShowMessageBox(ClassName + " could not be read because its folder is synced to the cloud and the file is not available offline. Voidstrap is running on defaults for now and will not overwrite your saved copy. Make the Voidstrap folder always available on this device, then restart.", MessageBoxImage.Exclamation);
			}
			Prop = new T();
			return;
		}
		App.Logger.WriteLine(identifier, "No valid JSON copy was available, defaults will be restored");
		if (alertFailure)
		{
			Frontend.ShowMessageBox(ClassName + " contained invalid JSON and no valid backup was available. Safe defaults were restored. The damaged file was preserved for recovery.", MessageBoxImage.Exclamation);
		}
		Prop = new T();
		Save();
	}

	private readonly System.Threading.Lock _saveLock = new();

	private readonly System.Threading.Lock _deferredTimerLock = new();

	private Timer? _deferredSaveTimer;

	private int _savePending;

	public void SaveDeferred()
	{
		Interlocked.Exchange(ref _savePending, 1);
		lock (_deferredTimerLock)
		{
			if (_deferredSaveTimer == null)
			{
				_deferredSaveTimer = new Timer(OnDeferredSave, null, 500, Timeout.Infinite);
			}
			else
			{
				_deferredSaveTimer.Change(500, Timeout.Infinite);
			}
		}
	}

	private void OnDeferredSave(object? state)
	{
		if (Interlocked.Exchange(ref _savePending, 0) == 0)
		{
			return;
		}
		try
		{
			Save();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException(LOG_IDENT_CLASS + "::SaveDeferred", ex);
		}
	}

	public void FlushDeferred()
	{
		if (Interlocked.Exchange(ref _savePending, 0) == 0)
		{
			return;
		}
		try
		{
			Save();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException(LOG_IDENT_CLASS + "::FlushDeferred", ex);
		}
	}

	public virtual void Save()
	{
		string identifier = LOG_IDENT_CLASS + "::Save";
		lock (_saveLock)
		{
			try
			{
				JsonFile.SerializeAtomic(FileLocation, Prop, JsonOptions.Indented);
				LastFileHash = SafeGetFileHash(FileLocation);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(identifier, "Failed to save");
				App.Logger.WriteException(identifier, ex);
				Frontend.ShowMessageBox(string.Format(Strings.Bootstrapper_JsonManagerSaveFailed, ClassName, ex.Message), MessageBoxImage.Exclamation);
			}
		}
	}

	private static string? SafeGetFileHash(string path)
	{
		try
		{
			using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			{
				return MD5Hash.FromFile(path);
			}
		}
		catch
		{
			return null;
		}
	}

	public bool HasFileOnDiskChanged()
	{
		try
		{
			string text = SafeGetFileHash(FileLocation);
			return LastFileHash != text;
		}
		catch
		{
			return true;
		}
	}

	public void SaveBackup(string name)
	{
		string savedBackups = Paths.SavedBackups;
		try
		{
			if (!string.IsNullOrWhiteSpace(name))
			{
				Directory.CreateDirectory(savedBackups);
				string safeName = Path.GetFileName(name);
				if (string.IsNullOrWhiteSpace(safeName))
					throw new InvalidDataException("Backup name is invalid");
				string path = Path.Combine(savedBackups, safeName);
				JsonFile.SerializeAtomic(path, Prop, JsonOptions.Indented, false);
				App.Logger.WriteLine("SaveBackup::Backups", "Backup '" + name + "' saved successfully.");
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Failed to save backup:\n" + ex.Message, MessageBoxImage.Hand);
		}
	}

	public void LoadBackup(string? name, bool? clearFlags)
	{
		string savedBackups = Paths.SavedBackups;
		try
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return;
			}
			string safeName = Path.GetFileName(name);
			if (string.IsNullOrWhiteSpace(safeName))
				throw new InvalidDataException("Backup name is invalid");
			string path = Path.Combine(savedBackups, safeName);
			if (!File.Exists(path))
			{
				throw new FileNotFoundException("Backup file '" + name + "' not found.");
			}
			T val = JsonFile.Deserialize<T>(path, JsonOptions.Tolerant);
			if (clearFlags == true)
			{
				Prop = val;
			}
			else if (val is IDictionary<string, object> dictionary && Prop is IDictionary<string, object> dictionary2)
			{
				foreach (KeyValuePair<string, object> item in dictionary)
				{
					if (item.Value != null)
					{
						dictionary2[item.Key] = item.Value;
					}
				}
			}
			App.Logger.WriteLine("LoadBackup::Backups", "Backup '" + name + "' loaded successfully.");
			App.FastFlags.Save();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("LoadBackup::Backups", ex);
			Frontend.ShowMessageBox("Failed to load backup:\n" + ex.Message, MessageBoxImage.Hand);
		}
	}
}
