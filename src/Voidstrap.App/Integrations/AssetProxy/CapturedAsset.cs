using System;
using System.ComponentModel;
using System.Windows.Media;

namespace Voidstrap.Integrations.AssetProxy;

public sealed class CapturedAsset : INotifyPropertyChanged
{
	public static bool ShowNames { get; set; }

	private string _assetId = "";

	private string _type = "Other";

	private string _creator = "";

	private long _size;

	private string _resolvedName = "";

	public string Key { get; init; } = "";

	public string AssetId
	{
		get
		{
			return _assetId;
		}
		set
		{
			if (!(_assetId == value))
			{
				_assetId = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AssetId)));
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
			}
		}
	}

	public string Hash { get; set; } = "";

	public string Type
	{
		get
		{
			return _type;
		}
		set
		{
			if (!(_type == value))
			{
				_type = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Type)));
			}
		}
	}

	public string Category { get; set; } = "Other";

	public string Extension { get; set; } = ".bin";

	public string Creator
	{
		get
		{
			return _creator;
		}
		set
		{
			if (!(_creator == value))
			{
				_creator = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Creator)));
			}
		}
	}

	public string Url { get; set; } = "";

	public string FilePath { get; set; } = "";

	public long Size
	{
		get
		{
			return _size;
		}
		set
		{
			if (_size != value)
			{
				_size = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
			}
		}
	}

	public bool IsImage { get; set; }

	public bool IsMesh { get; set; }

	public DateTime CapturedAt { get; set; } = DateTime.Now;

	public ImageSource? Thumbnail { get; set; }

	public string SizeText
	{
		get
		{
			if (Size < 1048576)
			{
				if (Size < 1024)
				{
					if (Size <= 0)
					{
						return "-";
					}
					return $"{Size} B";
				}
				return $"{(double)Size / 1024.0:0} KB";
			}
			return $"{(double)Size / 1048576.0:0.0} MB";
		}
	}

	public string ShortHash
	{
		get
		{
			if (Hash.Length <= 12)
			{
				return Hash;
			}
			return string.Concat(Hash.AsSpan(0, 12), "…");
		}
	}

	public string Display
	{
		get
		{
			if (string.IsNullOrEmpty(AssetId))
			{
				return ShortHash;
			}
			return AssetId;
		}
	}

	public string TimeText => CapturedAt.ToString("HH:mm:ss");

	public string ResolvedName
	{
		get
		{
			return _resolvedName;
		}
		set
		{
			if (!(_resolvedName == value))
			{
				_resolvedName = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResolvedName)));
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
			}
		}
	}

	public string Label
	{
		get
		{
			if (!ShowNames || string.IsNullOrEmpty(_resolvedName))
			{
				return Display;
			}
			return _resolvedName;
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public void RaiseLabelChanged()
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
	}
}
