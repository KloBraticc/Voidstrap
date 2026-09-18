using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Voidstrap.KeyRouting;

public enum OpposingKeyPriority
{
	LastPressed = 0,
	Neutral = 1,
	FirstListed = 2
}

public enum KeyRouteDecision
{
	Forward,
	Swallow
}

public readonly record struct RoutedKey(int VirtualKey, bool Down);

public sealed class OpposingKeyResolver
{
	public const string DefaultKeys = "A D, W S";

	private const int KeyCount = 256;

	private static readonly char[] GroupSeparators = [',', ';', '|'];

	private static readonly char[] KeySeparators = [' ', '\t', '/', '+'];

	private readonly int[] _groupIndex = new int[KeyCount];
	private readonly bool[] _held = new bool[KeyCount];
	private readonly bool[] _sent = new bool[KeyCount];
	private readonly long[] _order = new long[KeyCount];
	private int[][] _groups = [];
	private OpposingKeyPriority _priority;
	private long _clock;

	public int GroupCount => _groups.Length;

	public void Configure(IReadOnlyList<int[]> groups, OpposingKeyPriority priority)
	{
		Array.Clear(_groupIndex);
		var accepted = new List<int[]>(groups.Count);
		foreach (int[] group in groups)
		{
			var keys = new List<int>(group.Length);
			foreach (int key in group)
			{
				if (IsAllowedKey(key) && _groupIndex[key] == 0 && !keys.Contains(key))
					keys.Add(key);
			}
			if (keys.Count < 2)
				continue;
			accepted.Add([.. keys]);
			foreach (int key in keys)
				_groupIndex[key] = accepted.Count;
		}
		_groups = [.. accepted];
		_priority = Enum.IsDefined(priority) ? priority : OpposingKeyPriority.LastPressed;
	}

	public void Reset()
	{
		Array.Clear(_held);
		Array.Clear(_sent);
		Array.Clear(_order);
		_clock = 0;
	}

	public bool IsMapped(int virtualKey)
	{
		return (uint)virtualKey < KeyCount && _groupIndex[virtualKey] != 0;
	}

	public KeyRouteDecision Route(int virtualKey, bool down, bool takeOver, List<RoutedKey> output)
	{
		if (!IsMapped(virtualKey))
			return KeyRouteDecision.Forward;

		bool held = _held[virtualKey];
		if (!down && !held)
		{
			_sent[virtualKey] = false;
			return KeyRouteDecision.Forward;
		}
		if (down && held)
		{
			if (!takeOver)
			{
				_sent[virtualKey] = true;
				return KeyRouteDecision.Forward;
			}
			if (_sent[virtualKey])
				output.Add(new RoutedKey(virtualKey, true));
			return KeyRouteDecision.Swallow;
		}

		_held[virtualKey] = down;
		if (down)
			_order[virtualKey] = ++_clock;
		if (!takeOver)
		{
			_sent[virtualKey] = down;
			return KeyRouteDecision.Forward;
		}

		int[] group = _groups[_groupIndex[virtualKey] - 1];
		int active = PickActive(group);
		foreach (int key in group)
		{
			if (key != active && _sent[key])
			{
				_sent[key] = false;
				output.Add(new RoutedKey(key, false));
			}
		}
		if (active >= 0 && !_sent[active])
		{
			_sent[active] = true;
			output.Add(new RoutedKey(active, true));
		}
		return KeyRouteDecision.Swallow;
	}

	private int PickActive(int[] group)
	{
		int active = -1;
		int heldCount = 0;
		foreach (int key in group)
		{
			if (!_held[key])
				continue;
			heldCount++;
			if (active < 0 || (_priority == OpposingKeyPriority.LastPressed && _order[key] > _order[active]))
				active = key;
		}
		return heldCount > 1 && _priority == OpposingKeyPriority.Neutral ? -1 : active;
	}

	public static List<int[]> ParseGroups(string? text)
	{
		var groups = new List<int[]>();
		if (string.IsNullOrWhiteSpace(text))
			return groups;
		var used = new HashSet<int>();
		foreach (string part in text.Split(GroupSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var keys = new List<int>();
			foreach (string token in part.Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries))
			{
				int key = ParseKey(token);
				if (key != 0 && !used.Contains(key) && !keys.Contains(key))
					keys.Add(key);
			}
			if (keys.Count < 2)
				continue;
			used.UnionWith(keys);
			groups.Add([.. keys]);
		}
		return groups;
	}

	public static string FormatGroups(IEnumerable<int[]> groups)
	{
		var parts = new List<string>();
		foreach (int[] group in groups)
			parts.Add(string.Join(" ", Array.ConvertAll(group, KeyName)));
		return string.Join(", ", parts);
	}

	public static int ParseKey(string token)
	{
		if (token.Length == 1)
		{
			char c = char.ToUpperInvariant(token[0]);
			return c is >= 'A' and <= 'Z' or >= '0' and <= '9' ? c : 0;
		}
		if (int.TryParse(token, out _) || !Enum.TryParse(token, ignoreCase: true, out Keys parsed))
			return 0;
		int key = (int)parsed;
		return IsAllowedKey(key) ? key : 0;
	}

	public static string KeyName(int virtualKey)
	{
		return virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9'
			? ((char)virtualKey).ToString()
			: ((Keys)virtualKey).ToString();
	}

	private static bool IsAllowedKey(int key)
	{
		return key is > 0x07 and < KeyCount
			and not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
			and not (>= 0xA0 and <= 0xA5);
	}
}
