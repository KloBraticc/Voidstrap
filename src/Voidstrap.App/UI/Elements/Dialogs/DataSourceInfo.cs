using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Voidstrap.UI.Elements.Dialogs;

public class DataSourceInfo : INotifyPropertyChanged
{
	private string _name = "";

	private string _url = "";

	private string _status = "";

	private int _flagCount;

	private string _lastUpdated = "";

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			_name = value;
			OnPropertyChanged(nameof(Name));
		}
	}

	public string Url
	{
		get
		{
			return _url;
		}
		set
		{
			_url = value;
			OnPropertyChanged(nameof(Url));
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
			OnPropertyChanged(nameof(Status));
		}
	}

	public int FlagCount
	{
		get
		{
			return _flagCount;
		}
		set
		{
			_flagCount = value;
			OnPropertyChanged(nameof(FlagCount));
		}
	}

	public string LastUpdated
	{
		get
		{
			return _lastUpdated;
		}
		set
		{
			_lastUpdated = value;
			OnPropertyChanged(nameof(LastUpdated));
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
