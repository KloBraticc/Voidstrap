using System;
using System.Collections.Generic;
using System.Linq;

namespace Voidstrap.UI;

public enum RestartTarget
{
	Application,
	RobloxPlayer,
	RobloxStudio
}

public sealed record RestartRequirement(
	string Key,
	string Title,
	string Message,
	string ActionText,
	RestartTarget Target,
	long Sequence,
	object? Value,
	Action? Apply);

public sealed class RestartRequirementsChangedEventArgs : EventArgs
{
	public RestartRequirement? Current { get; }

	public int Count { get; }

	public RestartRequirementsChangedEventArgs(RestartRequirement? current, int count)
	{
		Current = current;
		Count = count;
	}
}

public static class RestartNotificationService
{
	private static readonly object Sync = new();

	private static readonly Dictionary<string, object?> Baselines = new(StringComparer.Ordinal);

	private static readonly Dictionary<string, RestartRequirement> Requirements = new(StringComparer.Ordinal);

	private static long _sequence;

	public static event EventHandler<RestartRequirementsChangedEventArgs>? Changed;

	public static RestartRequirement? Current
	{
		get
		{
			lock (Sync)
			{
				return GetCurrentLocked();
			}
		}
	}

	public static int Count
	{
		get
		{
			lock (Sync)
			{
				return Requirements.Count;
			}
		}
	}

	public static bool TryGetPendingValue<T>(string key, out T value)
	{
		lock (Sync)
		{
			if (Requirements.TryGetValue(key, out RestartRequirement? requirement) && requirement.Value is T typed)
			{
				value = typed;
				return true;
			}
		}

		value = default!;
		return false;
	}

	public static void RegisterSetting<T>(string key, T currentValue)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			throw new ArgumentException("A restart setting key is required", nameof(key));
		}

		lock (Sync)
		{
			Baselines.TryAdd(key, currentValue);
		}
	}

	public static void TrackApplicationSetting<T>(string key, T currentValue, string title, string message, Action? apply = null)
	{
		TrackSetting(key, currentValue, title, message, "Restart now", RestartTarget.Application, apply);
	}

	public static void TrackRobloxPlayerSetting<T>(string key, T currentValue, string title, string message, Action? apply = null)
	{
		TrackSetting(key, currentValue, title, message, "Restart Roblox", RestartTarget.RobloxPlayer, apply);
	}

	public static void TrackRobloxStudioSetting<T>(string key, T currentValue, string title, string message, Action? apply = null)
	{
		TrackSetting(key, currentValue, title, message, "Restart Studio", RestartTarget.RobloxStudio, apply);
	}

	public static void TrackSetting<T>(string key, T currentValue, string title, string message, string actionText, RestartTarget target, Action? apply = null)
	{
		RestartRequirementsChangedEventArgs? args = null;
		lock (Sync)
		{
			if (!Baselines.TryGetValue(key, out object? baseline))
			{
				baseline = currentValue;
				Baselines[key] = baseline;
			}

			bool matchesBaseline = baseline is T typed
				? EqualityComparer<T>.Default.Equals(typed, currentValue)
				: Equals(baseline, currentValue);

			if (matchesBaseline)
			{
				if (Requirements.Remove(key))
				{
					args = CreateChangedArgsLocked();
				}
			}
			else
			{
				Requirements[key] = new RestartRequirement(
					key,
					NormalizeText(title, "Settings changed"),
					NormalizeText(message, "Restart to apply this change."),
					NormalizeText(actionText, "Restart now"),
					target,
					++_sequence,
					currentValue,
					apply);
				args = CreateChangedArgsLocked();
			}
		}

		RaiseChanged(args);
	}

	public static void Require(string key, string title, string message, string actionText, RestartTarget target)
	{
		RestartRequirementsChangedEventArgs args;
		lock (Sync)
		{
			Requirements[key] = new RestartRequirement(
				key,
				NormalizeText(title, "Settings changed"),
				NormalizeText(message, "Restart to apply this change."),
				NormalizeText(actionText, "Restart now"),
				target,
				++_sequence,
				null,
				null);
			args = CreateChangedArgsLocked();
		}

		RaiseChanged(args);
	}

	public static void Clear(string key)
	{
		RestartRequirementsChangedEventArgs? args = null;
		lock (Sync)
		{
			if (Requirements.Remove(key))
			{
				args = CreateChangedArgsLocked();
			}
		}

		RaiseChanged(args);
	}

	public static void ClearAll()
	{
		RestartRequirementsChangedEventArgs? args = null;
		lock (Sync)
		{
			if (Requirements.Count > 0)
			{
				Requirements.Clear();
				args = CreateChangedArgsLocked();
			}
		}

		RaiseChanged(args);
	}

	private static RestartRequirement? GetCurrentLocked()
	{
		return Requirements.Values.OrderByDescending(requirement => requirement.Sequence).FirstOrDefault();
	}

	private static RestartRequirementsChangedEventArgs CreateChangedArgsLocked()
	{
		return new RestartRequirementsChangedEventArgs(GetCurrentLocked(), Requirements.Count);
	}

	private static void RaiseChanged(RestartRequirementsChangedEventArgs? args)
	{
		if (args != null)
		{
			Changed?.Invoke(null, args);
		}
	}

	private static string NormalizeText(string value, string fallback)
	{
		return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
	}
}
