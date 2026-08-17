using System;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed class ManagedModItem : NotifyPropertyChangedViewModel
{
	public ManagedModItem(string id, string name, bool enabled, DateTime createdUtc, int fileCount, long totalBytes, int conflictCount, string scanError)
	{
		Id = id;
		Name = name;
		Enabled = enabled;
		CreatedUtc = createdUtc;
		FileCount = fileCount;
		TotalBytes = totalBytes;
		ConflictCount = conflictCount;
		ScanError = scanError;
	}

	public string Id { get; }

	public string ShortId => Id.Length > 8 ? Id[..8] : Id;

	public string Name { get; }

	public DateTime CreatedUtc { get; }

	public int FileCount { get; }

	public long TotalBytes { get; }

	public int ConflictCount { get; }

	public bool HasConflicts => ConflictCount > 0;

	public string ScanError { get; }

	public bool HasScanError => !string.IsNullOrEmpty(ScanError);

	public bool Enabled { get; }

	public string FileSummary => FileCount + (FileCount == 1 ? " file, " : " files, ") + FormatBytes(TotalBytes);

	public string ConflictText => ConflictCount == 1 ? "1 path overlaps another active mod source" : ConflictCount + " paths overlap other active mod sources";

	private static string FormatBytes(long bytes)
	{
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		double value = Math.Max(0, bytes);
		int unit = 0;
		while (value >= 1024 && unit < units.Length - 1)
		{
			value /= 1024;
			unit++;
		}
		return unit == 0 ? value.ToString("0") + " " + units[unit] : value.ToString("0.##") + " " + units[unit];
	}
}
