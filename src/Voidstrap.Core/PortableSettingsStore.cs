using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed class SettingsDocument
{
	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	private readonly JsonObject _root;

	internal SettingsDocument(JsonObject root)
	{
		_root = root;
	}

	public JsonObject Root => _root;

	public static SettingsDocument CreateDefault()
	{
		return new SettingsDocument(new JsonObject());
	}

	public T Get<T>(string key, T defaultValue)
	{
		if (string.IsNullOrWhiteSpace(key) || _root[key] is null)
		{
			return defaultValue;
		}

		try
		{
			T? value = _root[key]!.Deserialize<T>(SerializerOptions);
			return value is null ? defaultValue : value;
		}
		catch (JsonException)
		{
			return defaultValue;
		}
	}

	public bool TryGet<T>(string key, out T? value)
	{
		value = default;
		if (string.IsNullOrWhiteSpace(key) || _root[key] is null)
		{
			return false;
		}

		try
		{
			value = _root[key]!.Deserialize<T>(SerializerOptions);
			return value is not null;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	public void Set<T>(string key, T value)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			throw new ArgumentException("A settings key is required", nameof(key));
		}

		_root[key] = JsonSerializer.SerializeToNode(value, SerializerOptions);
	}

	public void Remove(string key)
	{
		if (!string.IsNullOrWhiteSpace(key))
		{
			_root.Remove(key);
		}
	}
}

public sealed record SettingsLoadResult(
	SettingsDocument Document,
	string? SourcePath,
	bool Migrated);

public sealed class PortableSettingsStore
{
	private const int MaximumSettingsBytes = 4 * 1024 * 1024;

	private static readonly SemaphoreSlim SettingsGate = new SemaphoreSlim(1, 1);
	private readonly IPlatformPaths _paths;

	public PortableSettingsStore(IPlatformPaths paths)
	{
		_paths = paths;
	}

	public string FilePath => Path.Combine(_paths.Storage.Configuration, "AppSettings.json");

	public async Task<OperationResult<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			await SettingsGate.WaitAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return OperationResult<SettingsLoadResult>.Fail("OperationCanceled", "Settings loading was canceled");
		}
		try
		{
			return await LoadCoreAsync(cancellationToken);
		}
		finally
		{
			SettingsGate.Release();
		}
	}

	private async Task<OperationResult<SettingsLoadResult>> LoadCoreAsync(CancellationToken cancellationToken)
	{
		OperationResult directoryResult = await _paths.EnsureDirectoriesAsync(cancellationToken);
		if (!directoryResult.Succeeded)
		{
			return CopyFailure<SettingsLoadResult>(directoryResult.Failure);
		}

		OperationResult<IReadOnlyCollection<SettingsDefaultDefinition>> schemaResult = await SettingsSchemaImporter.LoadAsync(cancellationToken: cancellationToken);
		IReadOnlyCollection<SettingsDefaultDefinition> defaults = schemaResult.Succeeded && schemaResult.Value is not null
			? schemaResult.Value
			: Array.Empty<SettingsDefaultDefinition>();
		bool primaryCorrupt = false;
		IOException? readFailure = null;

		foreach (string candidate in GetCandidatePaths())
		{
			if (!File.Exists(candidate))
			{
				continue;
			}

			try
			{
				string text = await ReadSettingsTextAsync(candidate, cancellationToken);
				JsonNode? node = JsonNode.Parse(text);
				if (node is not JsonObject root)
				{
					primaryCorrupt |= string.Equals(candidate, FilePath, StringComparison.OrdinalIgnoreCase);
					continue;
				}

				bool defaultsApplied = ApplyDefaults(root, defaults);
				SettingsDocument document = new(root);
				bool migrated = !string.Equals(candidate, FilePath, StringComparison.OrdinalIgnoreCase);
				if (migrated || defaultsApplied)
				{
					if (migrated && primaryCorrupt && !QuarantinePrimary())
						return OperationResult<SettingsLoadResult>.Fail("SettingsRecoveryFailed", "The damaged settings file could not be preserved");
					OperationResult saveResult = await SaveCoreAsync(document, cancellationToken);
					if (!saveResult.Succeeded)
					{
						return CopyFailure<SettingsLoadResult>(saveResult.Failure);
					}
				}

				return OperationResult<SettingsLoadResult>.Success(new SettingsLoadResult(
					document,
					candidate,
					migrated));
			}
			catch (OperationCanceledException)
			{
				return OperationResult<SettingsLoadResult>.Fail("OperationCanceled", "Settings loading was canceled");
			}
			catch (Exception exception) when (exception is JsonException or InvalidDataException)
			{
				primaryCorrupt |= string.Equals(candidate, FilePath, StringComparison.OrdinalIgnoreCase);
				continue;
			}
			catch (IOException exception)
			{
				if (string.Equals(candidate, FilePath, StringComparison.OrdinalIgnoreCase))
					return OperationResult<SettingsLoadResult>.Fail("SettingsReadFailed", exception.Message);
				readFailure ??= exception;
				continue;
			}
			catch (UnauthorizedAccessException exception)
			{
				return OperationResult<SettingsLoadResult>.Fail("SettingsAccessDenied", exception.Message, CapabilityState.RequiresPermission);
			}
		}
		if (readFailure is not null)
		{
			return OperationResult<SettingsLoadResult>.Fail("SettingsReadFailed", readFailure.Message);
		}
		if (primaryCorrupt)
		{
			if (!QuarantinePrimary())
				return OperationResult<SettingsLoadResult>.Fail("SettingsRecoveryFailed", "The damaged settings file could not be preserved");
		}

		SettingsDocument initialDocument = SettingsDocument.CreateDefault();
		ApplyDefaults(initialDocument.Root, defaults);
		OperationResult initializationResult = await SaveCoreAsync(initialDocument, cancellationToken);
		if (!initializationResult.Succeeded)
		{
			return CopyFailure<SettingsLoadResult>(initializationResult.Failure);
		}

		return OperationResult<SettingsLoadResult>.Success(new SettingsLoadResult(initialDocument, null, false));
	}

	public async Task<OperationResult> SaveAsync(SettingsDocument document, CancellationToken cancellationToken = default)
	{
		try
		{
			await SettingsGate.WaitAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return OperationResult.Fail("OperationCanceled", "Settings saving was canceled");
		}
		try
		{
			return await SaveCoreAsync(document, cancellationToken);
		}
		finally
		{
			SettingsGate.Release();
		}
	}

	public async Task<OperationResult<SettingsDocument>> UpdateAsync(Func<SettingsDocument, bool> update, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(update);
		try
		{
			await SettingsGate.WaitAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return OperationResult<SettingsDocument>.Fail("OperationCanceled", "Settings updating was canceled");
		}
		try
		{
			OperationResult<SettingsLoadResult> loadResult = await LoadCoreAsync(cancellationToken);
			if (!loadResult.Succeeded || loadResult.Value is null)
			{
				return CopyFailure<SettingsDocument>(loadResult.Failure);
			}

			SettingsDocument document = loadResult.Value.Document;
			if (update(document))
			{
				OperationResult saveResult = await SaveCoreAsync(document, cancellationToken);
				if (!saveResult.Succeeded)
				{
					return CopyFailure<SettingsDocument>(saveResult.Failure);
				}
			}

			return OperationResult<SettingsDocument>.Success(document);
		}
		finally
		{
			SettingsGate.Release();
		}
	}

	private async Task<OperationResult> SaveCoreAsync(SettingsDocument document, CancellationToken cancellationToken)
	{
		if (document is null)
		{
			throw new ArgumentNullException(nameof(document));
		}

		OperationResult directoryResult = await _paths.EnsureDirectoriesAsync(cancellationToken);
		if (!directoryResult.Succeeded)
		{
			return CopyFailure(directoryResult.Failure);
		}

		string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			string text = document.Root.ToJsonString(new JsonSerializerOptions
			{
				WriteIndented = true
			});
			if (Encoding.UTF8.GetByteCount(text) > MaximumSettingsBytes)
				return OperationResult.Fail("SettingsTooLarge", "Settings exceed the allowed size");
			if (File.Exists(FilePath))
			{
				try
				{
					string current = await ReadSettingsTextAsync(FilePath, cancellationToken);
					if (JsonNode.Parse(current) is JsonObject)
					{
						string backupTemporary = FilePath + ".bak." + Guid.NewGuid().ToString("N") + ".tmp";
						try
						{
							await File.WriteAllTextAsync(backupTemporary, current, cancellationToken);
							File.Move(backupTemporary, FilePath + ".bak", true);
						}
						finally
						{
							try
							{
								File.Delete(backupTemporary);
							}
							catch (IOException)
							{
							}
							catch (UnauthorizedAccessException)
							{
							}
						}
					}
				}
				catch (Exception exception) when (exception is JsonException or InvalidDataException)
				{
				}
			}
			await File.WriteAllTextAsync(temporaryPath, text, cancellationToken);
			File.Move(temporaryPath, FilePath, true);
			return OperationResult.Success();
		}
		catch (OperationCanceledException)
		{
			return OperationResult.Fail("OperationCanceled", "Settings saving was canceled");
		}
		catch (IOException exception)
		{
			return OperationResult.Fail("SettingsWriteFailed", exception.Message);
		}
		catch (UnauthorizedAccessException exception)
		{
			return OperationResult.Fail("SettingsAccessDenied", exception.Message, CapabilityState.RequiresPermission);
		}
		finally
		{
			try
			{
				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	private static async Task<string> ReadSettingsTextAsync(string path, CancellationToken cancellationToken)
	{
		await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaximumSettingsBytes)
			throw new InvalidDataException("Settings file size is invalid");
		byte[] data = new byte[checked((int)stream.Length)];
		int offset = 0;
		while (offset < data.Length)
		{
			int read = await stream.ReadAsync(data.AsMemory(offset), cancellationToken);
			if (read == 0)
				throw new EndOfStreamException();
			offset += read;
		}
		if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
			throw new InvalidDataException("Settings file changed while it was being read");
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, Encoding.UTF8, true);
		return await reader.ReadToEndAsync(cancellationToken);
	}

	private IEnumerable<string> GetCandidatePaths()
	{
		yield return FilePath;
		yield return FilePath + ".bak";
		yield return Path.Combine(_paths.Storage.Configuration, "Settings.json");
		yield return Path.Combine(_paths.Storage.ApplicationSupport, "AppSettings.json");
	}

	private bool QuarantinePrimary()
	{
		try
		{
			if (File.Exists(FilePath))
				File.Move(FilePath, FilePath + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
			return true;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool ApplyDefaults(JsonObject root, IReadOnlyCollection<SettingsDefaultDefinition> defaults)
	{
		bool changed = false;
		foreach (SettingsDefaultDefinition definition in defaults)
		{
			if (!root.ContainsKey(definition.Name))
			{
				root[definition.Name] = definition.DefaultValue.DeepClone();
				changed = true;
			}
		}

		return changed;
	}

	private static OperationResult CopyFailure(OperationFailure? failure)
	{
		return failure is null
			? OperationResult.Fail("SettingsInitializationFailed", "Settings directory initialization failed")
			: OperationResult.Fail(failure.Code, failure.Message, failure.State);
	}

	private static OperationResult<T> CopyFailure<T>(OperationFailure? failure)
	{
		return failure is null
			? OperationResult<T>.Fail("SettingsInitializationFailed", "Settings directory initialization failed")
			: OperationResult<T>.Fail(failure.Code, failure.Message, failure.State);
	}
}
