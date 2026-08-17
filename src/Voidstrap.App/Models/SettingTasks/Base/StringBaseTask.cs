namespace Voidstrap.Models.SettingTasks.Base;

public abstract class StringBaseTask : BaseTask
{
	private string _originalState = "";

	private string _newState = "";

	public virtual string OriginalState
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

	public virtual string NewState
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

	public StringBaseTask(string prefix, string name)
		: base(prefix, name)
	{
	}
}
