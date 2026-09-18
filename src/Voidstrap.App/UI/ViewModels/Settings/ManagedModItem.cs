using System;
using System.Linq;
using System.Windows;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public sealed class ManagedModItem : NotifyPropertyChangedViewModel
{
	private string _name;

	private bool _enabled;

	private int _fileCount;

	private long _totalBytes;

	private int _conflictCount;

	private int _overriddenCount;

	private bool _appliedOnTop;

	private string _scanError;

	private ModPackInfo? _pack;

	private bool _editable;

	public ManagedModItem(string id, string name, bool enabled, DateTime createdUtc, int fileCount, long totalBytes, int conflictCount, string scanError, ModPackInfo? pack = null, bool editable = false, int overriddenCount = 0, bool appliedOnTop = false)
	{
		_appliedOnTop = appliedOnTop;
		_overriddenCount = overriddenCount;
		_editable = editable;
		_pack = pack;
		Id = id;
		_name = name;
		_enabled = enabled;
		CreatedUtc = createdUtc;
		_fileCount = fileCount;
		_totalBytes = totalBytes;
		_conflictCount = conflictCount;
		_scanError = scanError;
	}

	public string Id { get; }

	public string ShortId => Id.Length > 8 ? Id[..8] : Id;

	public DateTime CreatedUtc { get; }

	public string Name
	{
		get => _name;
		private set => SetProperty(ref _name, value);
	}

	public bool Enabled
	{
		get => _enabled;
		private set => SetProperty(ref _enabled, value);
	}

	public int FileCount
	{
		get => _fileCount;
		private set
		{
			if (SetProperty(ref _fileCount, value))
				OnPropertyChanged(nameof(FileSummary));
		}
	}

	public long TotalBytes
	{
		get => _totalBytes;
		private set
		{
			if (SetProperty(ref _totalBytes, value))
				OnPropertyChanged(nameof(FileSummary));
		}
	}

	public int ConflictCount
	{
		get => _conflictCount;
		private set
		{
			if (!SetProperty(ref _conflictCount, value))
				return;
			OnPropertyChanged(nameof(HasConflicts));
			OnPropertyChanged(nameof(ConflictText));
		}
	}

	public int OverriddenCount
	{
		get => _overriddenCount;
		private set
		{
			if (SetProperty(ref _overriddenCount, value))
				OnPropertyChanged(nameof(ConflictText));
		}
	}

	public bool AppliedOnTop
	{
		get => _appliedOnTop;
		private set
		{
			if (SetProperty(ref _appliedOnTop, value))
				OnPropertyChanged(nameof(ConflictText));
		}
	}

	public string ScanError
	{
		get => _scanError;
		private set
		{
			if (SetProperty(ref _scanError, value))
				OnPropertyChanged(nameof(HasScanError));
		}
	}

	public bool Editable
	{
		get => _editable;
		private set
		{
			if (SetProperty(ref _editable, value))
				OnPropertyChanged(nameof(EditVisibility));
		}
	}

	public Visibility EditVisibility => _editable ? Visibility.Visible : Visibility.Collapsed;

	public bool HasConflicts => ConflictCount > 0;

	public bool HasScanError => !string.IsNullOrEmpty(ScanError);

	public string FileSummary => FileCount + (FileCount == 1 ? " file, " : " files, ") + FormatBytes(TotalBytes);

	public string ConflictText
	{
		get
		{
			int wins = ConflictCount - OverriddenCount;
			string won = wins <= 0 ? ""
				: AppliedOnTop ? "Always applied on top, overrides " + CountPaths(wins) + " in other mods"
				: "Overrides " + CountPaths(wins) + " in lower priority mods";
			string lost = OverriddenCount > 0 ? CountPaths(OverriddenCount) + " overridden by higher priority mods" : "";
			return won.Length > 0 && lost.Length > 0 ? won + ", " + lost : won + lost;
		}
	}

	private static string CountPaths(int count)
	{
		return count == 1 ? "1 path" : count + " paths";
	}

	public ModPackInfo? Pack
	{
		get => _pack;
		private set
		{
			_pack = value;
			OnPropertyChanged(nameof(Pack));
			OnPropertyChanged(nameof(IsModPack));
			OnPropertyChanged(nameof(PackVisibility));
			OnPropertyChanged(nameof(PackIconVisibility));
			OnPropertyChanged(nameof(PackDetail));
		}
	}

	public bool IsModPack => _pack is not null && _pack.Id > 0 || !string.IsNullOrEmpty(_pack?.Slug);

	public Visibility PackVisibility => IsModPack ? Visibility.Visible : Visibility.Collapsed;

	public Visibility PackIconVisibility => IsModPack && !string.IsNullOrEmpty(_pack?.IconUrl) ? Visibility.Visible : Visibility.Collapsed;

	public string PackDetail
	{
		get
		{
			if (_pack is null)
			{
				return "";
			}
			string author = string.IsNullOrWhiteSpace(_pack.Author) ? "" : "by " + _pack.Author;
			string source = string.IsNullOrWhiteSpace(_pack.Source) ? "" : "from " + _pack.Source;
			string kind = string.IsNullOrWhiteSpace(_pack.InstallKind) ? "" : _pack.InstallKind;
			return string.Join("   ", new[] { author, source, kind }.Where(part => part.Length > 0));
		}
	}

	public void Apply(ManagedModItem other)
	{
		Pack = other.Pack;
		Name = other.Name;
		Enabled = other.Enabled;
		FileCount = other.FileCount;
		TotalBytes = other.TotalBytes;
		AppliedOnTop = other.AppliedOnTop;
		OverriddenCount = other.OverriddenCount;
		ConflictCount = other.ConflictCount;
		ScanError = other.ScanError;
		Editable = other.Editable;
	}

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
