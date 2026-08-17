using System;
using System.Collections.Generic;
using System.Linq;
using Voidstrap.Models.Attributes;

namespace Voidstrap.Models.SettingTasks.Base;

public abstract class EnumBaseTask<T> : BaseTask where T : struct, Enum
{
	private T _originalState;

	private T _newState;

	public virtual T OriginalState
	{
		get
		{
			return _originalState;
		}
		set
		{
			_originalState = value;
			_newState = value;
		}
	}

	public virtual T NewState
	{
		get
		{
			return _newState;
		}
		set
		{
			_newState = value;
			if (Changed)
			{
				App.PendingSettingTasks[base.Name] = this;
			}
			else
			{
				App.PendingSettingTasks.Remove(base.Name);
			}
		}
	}

	public override bool Changed => !_newState.Equals(OriginalState);

	public IEnumerable<T> Selections { get; private set; } = Enum.GetValues(typeof(T)).Cast<T>().OrderBy(delegate(T x)
	{
		object[] customAttributes = x.GetType().GetMember(x.ToString())[0].GetCustomAttributes(typeof(EnumSortAttribute), inherit: false);
		return (customAttributes.Length != 0) ? ((EnumSortAttribute)customAttributes[0]).Order : 0;
	});

	public EnumBaseTask(string prefix, string name)
		: base(prefix, name)
	{
	}
}
