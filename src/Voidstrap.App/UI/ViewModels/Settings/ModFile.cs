namespace Voidstrap.UI.ViewModels.Settings;

public class ModFile : NotifyPropertyChangedViewModel
{
	private string _name = string.Empty;

	private string _relativePath = string.Empty;

	private string _fullPath = string.Empty;

	private string _type = string.Empty;

	private string _sizeText = string.Empty;

	private string _modifiedTime = string.Empty;

	private bool _isFolder;

	private string _status = "";

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			_name = value;
			OnPropertyChanged("Name");
		}
	}

	public string RelativePath
	{
		get
		{
			return _relativePath;
		}
		set
		{
			_relativePath = value;
			OnPropertyChanged("RelativePath");
		}
	}

	public string FullPath
	{
		get
		{
			return _fullPath;
		}
		set
		{
			_fullPath = value;
			OnPropertyChanged("FullPath");
		}
	}

	public string Type
	{
		get
		{
			return _type;
		}
		set
		{
			_type = value;
			OnPropertyChanged("Type");
		}
	}

	public string SizeText
	{
		get
		{
			return _sizeText;
		}
		set
		{
			_sizeText = value;
			OnPropertyChanged("SizeText");
		}
	}

	public string ModifiedTime
	{
		get
		{
			return _modifiedTime;
		}
		set
		{
			_modifiedTime = value;
			OnPropertyChanged("ModifiedTime");
		}
	}

	public bool IsFolder
	{
		get
		{
			return _isFolder;
		}
		set
		{
			_isFolder = value;
			OnPropertyChanged("IsFolder");
		}
	}

	public string Status
	{
		get
		{
			return _status;
		}
		set
		{
			_status = value;
			OnPropertyChanged("Status");
			OnPropertyChanged("StatusColor");
		}
	}

	public string StatusColor
	{
		get
		{
			switch (Status)
			{
			case "Modded":
			case "Overwriting":
				return "Warning";
			case "Original":
			case "New File":
				return "Success";
			case "Orphaned":
				return "Danger";
			default:
				return "Secondary";
			}
		}
	}

	public string Icon
	{
		get
		{
			if (!IsFolder)
			{
				return GetIconForType(Type);
			}
			return "Folder24";
		}
	}

	public bool IsImage
	{
		get
		{
			switch (Type.ToLower())
			{
			case "png":
			case "jpg":
			case "jpeg":
			case "bmp":
				return true;
			default:
				return false;
			}
		}
	}

	private static string GetIconForType(string type)
	{
		switch (type.ToLower())
		{
		case "png":
		case "jpg":
		case "bmp":
		case "jpeg":
			return "Image24";
		case "ogg":
		case "mp3":
		case "wav":
			return "Speaker224";
		case "mesh":
			return "Box24";
		case "rbxm":
		case "rbxmx":
			return "PuzzlePiece24";
		case "json":
			return "DocumentJson24";
		default:
			return "Document24";
		}
	}
}
