using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Voidstrap.UI.Elements.Dialogs;

public class FlagValidationResult : INotifyPropertyChanged
{
	private string _name = "";

	private string _inputValue = "";

	private string _status = "";

	private string _validValue = "";

	private string _notes = "";

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

	public string InputValue
	{
		get
		{
			return _inputValue;
		}
		set
		{
			_inputValue = value;
			OnPropertyChanged("InputValue");
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

	public string ValidValue
	{
		get
		{
			return _validValue;
		}
		set
		{
			_validValue = value;
			OnPropertyChanged("ValidValue");
		}
	}

	public string Notes
	{
		get
		{
			return _notes;
		}
		set
		{
			_notes = value;
			OnPropertyChanged("Notes");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
