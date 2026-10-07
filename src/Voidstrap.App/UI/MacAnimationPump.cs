using System;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class MacAnimationPump
{
	private const int IdleFramesBeforeStop = 3;

	private static Func<TimeSpan>? _nextTickNeeded;
	private static bool _installed;
	private static bool _pumping;
	private static int _idleFrames;

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.IsMacOS)
			return;
		_installed = true;

		Type? mediaContextType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext");
		object? mediaContext = mediaContextType?
			.GetMethod("From", BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(Dispatcher)], null)?
			.Invoke(null, [Dispatcher.CurrentDispatcher]);
		object? timeManager = mediaContextType?
			.GetProperty("TimeManager", BindingFlags.NonPublic | BindingFlags.Instance)?
			.GetValue(mediaContext);
		Type? timeManagerType = timeManager?.GetType();
		MethodInfo? nextTick = timeManagerType?.GetMethod("GetNextTickNeeded", BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
		MethodInfo? addNeedTick = timeManagerType?.GetEvent("NeedTickSooner", BindingFlags.NonPublic | BindingFlags.Instance)?.GetAddMethod(true);
		if (timeManager is null || nextTick is null || addNeedTick is null)
		{
			App.Logger.WriteLine("MacAnimationPump", "The animation clock could not be located, animations only advance on input");
			return;
		}

		_nextTickNeeded = (Func<TimeSpan>)Delegate.CreateDelegate(typeof(Func<TimeSpan>), timeManager, nextTick);
		addNeedTick.Invoke(timeManager, [new EventHandler(OnNeedTickSooner)]);
		Dispatcher.CurrentDispatcher.Hooks.DispatcherInactive += OnDispatcherInactive;
		_nextTickNeeded();
		App.Logger.WriteLine("MacAnimationPump", "Animations now drive their own frames");
	}

	private static void OnDispatcherInactive(object? sender, EventArgs e)
	{
		if (!_pumping && _nextTickNeeded is not null && _nextTickNeeded() >= TimeSpan.Zero)
			OnNeedTickSooner(sender, e);
	}

	private static void OnNeedTickSooner(object? sender, EventArgs e)
	{
		_idleFrames = 0;
		if (_pumping)
			return;
		_pumping = true;
		CompositionTarget.Rendering += OnRendering;
	}

	private static void OnRendering(object? sender, EventArgs e)
	{
		if (_nextTickNeeded is not null && _nextTickNeeded() >= TimeSpan.Zero)
		{
			_idleFrames = 0;
			return;
		}
		if (++_idleFrames < IdleFramesBeforeStop)
			return;
		CompositionTarget.Rendering -= OnRendering;
		_pumping = false;
		_nextTickNeeded?.Invoke();
	}
}
