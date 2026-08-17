using System.Collections.Generic;
using System.ComponentModel;

namespace Voidstrap.Models;

public class CustomIntegration : INotifyPropertyChanged
{
	private string _name = "";

	private string _location = "";

	private string _launchArgs = "";

	private bool _autoClose = true;

	private bool _specifyGame;

	private string _gameId = "";

	private bool _autoCloseOnGame = true;

	private int _delay;

	private bool _preLaunch;

	private bool _runMinimized;

	private bool _runAsAdmin;

	public string Name
	{
		get
		{
			return _name;
		}
		set
		{
			SetField(ref _name, value, "Name");
		}
	}

	public string Location
	{
		get
		{
			return _location;
		}
		set
		{
			SetField(ref _location, value, "Location");
		}
	}

	public string LaunchArgs
	{
		get
		{
			return _launchArgs;
		}
		set
		{
			SetField(ref _launchArgs, value, "LaunchArgs");
		}
	}

	public bool AutoClose
	{
		get
		{
			return _autoClose;
		}
		set
		{
			SetField(ref _autoClose, value, "AutoClose");
		}
	}

	public bool SpecifyGame
	{
		get
		{
			return _specifyGame;
		}
		set
		{
			SetField(ref _specifyGame, value, "SpecifyGame");
		}
	}

	public string GameID
	{
		get
		{
			return _gameId;
		}
		set
		{
			SetField(ref _gameId, value, "GameID");
		}
	}

	public bool AutoCloseOnGame
	{
		get
		{
			return _autoCloseOnGame;
		}
		set
		{
			SetField(ref _autoCloseOnGame, value, "AutoCloseOnGame");
		}
	}

	public int Delay
	{
		get
		{
			return _delay;
		}
		set
		{
			SetField(ref _delay, (value >= 0) ? value : 0, "Delay");
		}
	}

	public bool PreLaunch
	{
		get
		{
			return _preLaunch;
		}
		set
		{
			SetField(ref _preLaunch, value, "PreLaunch");
		}
	}

	public bool RunMinimized
	{
		get
		{
			return _runMinimized;
		}
		set
		{
			SetField(ref _runMinimized, value, "RunMinimized");
		}
	}

	public bool RunAsAdmin
	{
		get
		{
			return _runAsAdmin;
		}
		set
		{
			SetField(ref _runAsAdmin, value, "RunAsAdmin");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void SetField<T>(ref T field, T value, string name)
	{
		if (!EqualityComparer<T>.Default.Equals(field, value))
		{
			field = value;
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		}
	}
}
