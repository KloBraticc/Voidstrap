using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Voidstrap.UI.Elements.Dialogs;

public class FlagSearchResult : INotifyPropertyChanged
{
	private string _name = "";

	private string _value = "";

	private string _source = "";

	private string _dateAdded = "";

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

	public string Value
	{
		get
		{
			return _value;
		}
		set
		{
			_value = value;
			OnPropertyChanged("Value");
		}
	}

	public string Source
	{
		get
		{
			return _source;
		}
		set
		{
			_source = value;
			OnPropertyChanged("Source");
		}
	}

	public string DateAdded
	{
		get
		{
			return _dateAdded;
		}
		set
		{
			_dateAdded = value;
			OnPropertyChanged("DateAdded");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
