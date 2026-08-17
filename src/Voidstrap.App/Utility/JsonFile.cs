using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class JsonFile
{
	private const long DefaultMaximumBytes = 67108864;
	private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

	public static string ReadText(string path, long maximumBytes = DefaultMaximumBytes)
	{
		using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		int length = ValidateLength(stream.Length, maximumBytes);
		byte[] contents = new byte[length];
		int offset = 0;
		while (offset < contents.Length)
		{
			int read = stream.Read(contents, offset, contents.Length - offset);
			if (read == 0)
				throw new InvalidDataException("JSON file changed while it was being read");
			offset += read;
		}
		if (stream.ReadByte() != -1)
			throw new InvalidDataException("JSON file exceeds the allowed size");
		return Decode(contents);
	}

	public static async Task<string> ReadTextAsync(string path, long maximumBytes = DefaultMaximumBytes, CancellationToken cancellationToken = default)
	{
		await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
		int length = ValidateLength(stream.Length, maximumBytes);
		byte[] contents = new byte[length];
		int offset = 0;
		while (offset < contents.Length)
		{
			int read = await stream.ReadAsync(contents.AsMemory(offset), cancellationToken).ConfigureAwait(false);
			if (read == 0)
				throw new InvalidDataException("JSON file changed while it was being read");
			offset += read;
		}
		byte[] extra = new byte[1];
		if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
			throw new InvalidDataException("JSON file exceeds the allowed size");
		return Decode(contents);
	}

	public static T Deserialize<T>(string path, JsonSerializerOptions? options = null, long maximumBytes = DefaultMaximumBytes)
	{
		return JsonSerializer.Deserialize<T>(ReadText(path, maximumBytes), options) ?? throw new InvalidDataException("JSON deserialization returned no data");
	}

	public static bool TryLoad<T>(string path, JsonSerializerOptions? options, out T? value, out bool recovered, out Exception? failure, long maximumBytes = DefaultMaximumBytes)
	{
		value = default;
		recovered = false;
		failure = null;
		bool primaryCorrupt = false;
		try
		{
			value = Deserialize<T>(path, options, maximumBytes);
			return true;
		}
		catch (Exception ex) when (ex is JsonException or InvalidDataException)
		{
			failure = ex;
			primaryCorrupt = true;
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			failure = ex;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			failure = ex;
			return false;
		}
		string backup = path + ".bak";
		T backupValue;
		try
		{
			backupValue = Deserialize<T>(backup, options, maximumBytes);
		}
		catch (Exception ex) when (ex is JsonException or InvalidDataException)
		{
			failure = failure == null ? ex : new AggregateException(failure, ex);
			if (primaryCorrupt)
				Quarantine(path);
			return false;
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			failure = failure == null ? ex : new AggregateException(failure, ex);
			if (primaryCorrupt)
				Quarantine(path);
			return false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			failure = failure == null ? ex : new AggregateException(failure, ex);
			return false;
		}
		try
		{
			if (primaryCorrupt)
				Quarantine(path);
			WriteAtomicText(path, JsonSerializer.Serialize(backupValue, options), false);
			value = backupValue;
			recovered = true;
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			failure = failure == null ? ex : new AggregateException(failure, ex);
			value = default;
			return false;
		}
	}

	public static void SerializeAtomic<T>(string path, T value, JsonSerializerOptions? options = null, bool createBackup = true)
	{
		WriteAtomicText(path, JsonSerializer.Serialize(value, options), createBackup);
	}

	private static void ReplaceWithRetry(string temporary, string destination)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				File.Move(temporary, destination, true);
				return;
			}
			catch (Exception ex) when (attempt < 5 && (ex is IOException || ex is UnauthorizedAccessException))
			{
				Thread.Sleep(120 * (attempt + 1));
			}
		}
	}

	public static void WriteAtomicText(string path, string contents, bool createBackup = true)
	{
		if (Encoding.UTF8.GetByteCount(contents) > DefaultMaximumBytes)
			throw new InvalidDataException("JSON content exceeds the allowed size");
		using (JsonDocument.Parse(contents, new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip,
			MaxDepth = 128
		}))
		{
		}
		string fullPath = Path.GetFullPath(path);
		lock (Gates.GetOrAdd(fullPath, static _ => new object()))
		{
			string? directory = Path.GetDirectoryName(fullPath);
			if (string.IsNullOrEmpty(directory))
				throw new InvalidOperationException("JSON file has no parent directory");
			Directory.CreateDirectory(directory);
			string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			try
			{
				using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
				using (StreamWriter writer = new(stream, new UTF8Encoding(false)))
				{
					writer.Write(contents);
					writer.Flush();
					stream.Flush(true);
				}
				if (createBackup && File.Exists(fullPath) && IsValid(fullPath))
					WriteAtomicText(fullPath + ".bak", ReadText(fullPath), false);
				ReplaceWithRetry(temporary, fullPath);
			}
			finally
			{
				try
				{
					File.Delete(temporary);
				}
				catch
				{
				}
			}
		}
	}

	private static int ValidateLength(long length, long maximumBytes)
	{
		if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(maximumBytes));
		if (length <= 0)
			throw new InvalidDataException("JSON file is empty");
		if (length > maximumBytes)
			throw new InvalidDataException("JSON file exceeds the allowed size");
		return checked((int)length);
	}

	private static string Decode(byte[] contents)
	{
		ReadOnlySpan<byte> bytes = contents;
		Encoding encoding;
		if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
		{
			encoding = new UTF32Encoding(true, true, true);
			bytes = bytes[4..];
		}
		else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
		{
			encoding = new UTF32Encoding(false, true, true);
			bytes = bytes[4..];
		}
		else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
		{
			encoding = new UTF8Encoding(false, true);
			bytes = bytes[3..];
		}
		else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
		{
			encoding = new UnicodeEncoding(true, true, true);
			bytes = bytes[2..];
		}
		else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
		{
			encoding = new UnicodeEncoding(false, true, true);
			bytes = bytes[2..];
		}
		else
		{
			encoding = new UTF8Encoding(false, true);
		}
		return encoding.GetString(bytes);
	}

	private static bool IsValid(string path)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(ReadText(path), new JsonDocumentOptions
			{
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
				MaxDepth = 128
			});
			return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
		}
		catch
		{
			return false;
		}
	}

	private static void Quarantine(string path)
	{
		try
		{
			if (!File.Exists(path))
				return;
			string quarantine = path + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
			File.Move(path, quarantine, false);
		}
		catch
		{
		}
	}
}
