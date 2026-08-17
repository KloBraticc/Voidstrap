namespace Voidstrap.Models.SettingTasks.Base;

public abstract class BoolBaseTask : BaseTask
{
	private bool _originalState;

	private bool _newState;

	public virtual bool OriginalState
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

	public virtual bool NewState
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

	public override bool Changed => _newState != OriginalState;

	public BoolBaseTask(string prefix, string name)
		: base(prefix, name)
	{
	}

	public BoolBaseTask(string name)
		: base(name)
	{
	}
}
