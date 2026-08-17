using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public class TrackItem : INotifyPropertyChanged
{
	private string _title = string.Empty;

	private string _artist = string.Empty;

	private string _filePath = string.Empty;

	private string _fileType = string.Empty;

	private TimeSpan _duration;

	private ImageSource? _icon;

	public string Title
	{
		get
		{
			return _title;
		}
		set
		{
			if (_title != value)
			{
				_title = value;
				OnPropertyChanged("Title");
			}
		}
	}

	public string Artist
	{
		get
		{
			return _artist;
		}
		set
		{
			if (_artist != value)
			{
				_artist = value;
				OnPropertyChanged("Artist");
			}
		}
	}

	public string FilePath
	{
		get
		{
			return _filePath;
		}
		set
		{
			if (_filePath != value)
			{
				_filePath = value;
				OnPropertyChanged("FilePath");
			}
		}
	}

	public string FileType
	{
		get
		{
			return _fileType;
		}
		set
		{
			if (_fileType != value)
			{
				_fileType = value;
				OnPropertyChanged("FileType");
			}
		}
	}

	public TimeSpan Duration
	{
		get
		{
			return _duration;
		}
		set
		{
			if (_duration != value)
			{
				_duration = value;
				OnPropertyChanged("Duration");
			}
		}
	}

	public ImageSource? Icon
	{
		get
		{
			return _icon;
		}
		set
		{
			if (_icon != value)
			{
				_icon = value;
				OnPropertyChanged("Icon");
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged([CallerMemberName] string? name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
