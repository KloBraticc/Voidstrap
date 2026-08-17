using System;
using System.Windows.Input;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public sealed class RelayCommand : ICommand
{
	private readonly Action<object?> _execute;

	private readonly Predicate<object?>? _canExecute;

	public event EventHandler? CanExecuteChanged
	{
		add
		{
			CommandManager.RequerySuggested += value;
		}
		remove
		{
			CommandManager.RequerySuggested -= value;
		}
	}

	public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException("execute");
		_canExecute = canExecute;
	}

	public RelayCommand(Action execute, Func<bool>? canExecute = null)
	{
		if (execute == null)
		{
			throw new ArgumentNullException("execute");
		}
		_execute = delegate
		{
			execute();
		};
		_canExecute = ((canExecute == null) ? null : ((Predicate<object>)((object? _) => canExecute())));
	}

	public bool CanExecute(object? parameter)
	{
		return _canExecute?.Invoke(parameter) ?? true;
	}

	public void Execute(object? parameter)
	{
		_execute(parameter);
	}

	public void RaiseCanExecuteChanged()
	{
		CommandManager.InvalidateRequerySuggested();
	}
}
