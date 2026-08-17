using System;

namespace Voidstrap.Models;

public class LaunchFlag
{
	private bool _active;

	public string Identifiers { get; }

	public bool Active
	{
		get
		{
			return _active;
		}
		set
		{
			_active = value;
		}
	}

	public string? Data { get; set; }

	public LaunchFlag(string identifiers)
	{
		Identifiers = identifiers ?? throw new ArgumentNullException("identifiers");
		_active = false;
	}

	public void Activate()
	{
		_active = true;
	}

	public void Deactivate()
	{
		_active = false;
	}

	public void Toggle()
	{
		_active = !_active;
	}
}
