using System.Text;

namespace Voidstrap.Core.AssetProxy;

public static class HostsFileWriter
{
	private static readonly UTF8Encoding Encoding = new(false);

	public static void WriteAllLines(string path, IEnumerable<string> lines, string? staleTemporaryPath = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(lines);

		string fullPath = Path.GetFullPath(path);
		string? directory = Path.GetDirectoryName(fullPath);
		if (string.IsNullOrEmpty(directory))
		{
			throw new DirectoryNotFoundException(fullPath);
		}

		Directory.CreateDirectory(directory);
		string[] materialized = lines.ToArray();
		byte[] updated = EncodeLines(materialized);
		bool existed = File.Exists(fullPath);
		FileAttributes originalAttributes = existed ? File.GetAttributes(fullPath) : FileAttributes.Normal;
		bool readOnly = existed && originalAttributes.HasFlag(FileAttributes.ReadOnly);
		string temporaryPath = Path.Combine(directory, Path.GetFileName(fullPath) + ".voidstrap." + Guid.NewGuid().ToString("N") + ".tmp");
		string backupPath = temporaryPath + ".bak";

		if (readOnly)
		{
			File.SetAttributes(fullPath, originalAttributes & ~FileAttributes.ReadOnly);
		}

		try
		{
			WriteBytes(temporaryPath, updated);
			if (existed)
			{
				File.Replace(temporaryPath, fullPath, backupPath, true);
			}
			else
			{
				File.Move(temporaryPath, fullPath);
			}
		}
		catch (Exception writeFailure)
		{
			try
			{
				if (!File.Exists(fullPath) && File.Exists(backupPath))
				{
					File.Move(backupPath, fullPath);
				}
			}
			catch (Exception restoreFailure)
			{
				throw new AggregateException(writeFailure, restoreFailure);
			}
			throw;
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
			if (File.Exists(backupPath))
			{
				File.Delete(backupPath);
			}
			if (readOnly && File.Exists(fullPath))
			{
				File.SetAttributes(fullPath, originalAttributes);
			}
		}

		if (!string.IsNullOrWhiteSpace(staleTemporaryPath))
		{
			try
			{
				File.Delete(staleTemporaryPath);
			}
			catch
			{
			}
		}
	}

	private static byte[] EncodeLines(IReadOnlyCollection<string> lines)
	{
		if (lines.Count == 0)
		{
			return [];
		}
		return Encoding.GetBytes(string.Join(Environment.NewLine, lines) + Environment.NewLine);
	}

	private static void WriteBytes(string path, byte[] contents)
	{
		using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
		stream.Write(contents);
		stream.Flush(true);
	}
}
