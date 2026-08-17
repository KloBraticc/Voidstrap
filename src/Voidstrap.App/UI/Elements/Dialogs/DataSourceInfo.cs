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
			OnPropertyChanged("Name");
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
			OnPropertyChanged("Url");
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
			OnPropertyChanged("FlagCount");
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
			OnPropertyChanged("LastUpdated");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
