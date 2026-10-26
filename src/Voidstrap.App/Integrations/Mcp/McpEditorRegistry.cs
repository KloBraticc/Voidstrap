using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Voidstrap.Integrations.Mcp;

public sealed record McpEditResult(bool Success, string Message);

public interface IMcpEditor
{
	string Kind { get; }

	string DisplayName { get; }

	string Language { get; }

	string Guidance { get; }

	string GetCode();

	McpEditResult SetCode(string code);

	McpEditResult Save();
}

public static class McpEditorRegistry
{
	private static readonly object Gate = new();

	private static readonly Dictionary<string, IMcpEditor> Editors = new(StringComparer.OrdinalIgnoreCase);

	private static int _counter;

	public static string? Register(IMcpEditor editor)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return null;

		string id = editor.Kind + "_" + Interlocked.Increment(ref _counter);

		lock (Gate)
			Editors[id] = editor;

		McpBridgeServer.EnsureStarted();
		return id;
	}

	public static void Unregister(string? id)
	{
		if (string.IsNullOrEmpty(id))
			return;

		lock (Gate)
			Editors.Remove(id);
	}

	public static IReadOnlyList<KeyValuePair<string, IMcpEditor>> Snapshot()
	{
		lock (Gate)
			return Editors.ToList();
	}

	public static IMcpEditor? Find(string? id)
	{
		return FindEntry(id)?.Value;
	}

	public static KeyValuePair<string, IMcpEditor>? FindEntry(string? id)
	{
		lock (Gate)
		{
			if (Editors.Count == 0)
				return null;

			if (string.IsNullOrWhiteSpace(id))
				return Editors.Last();

			string needle = id.Trim();
			if (Editors.TryGetValue(needle, out IMcpEditor? exact))
				return new KeyValuePair<string, IMcpEditor>(needle, exact);

			foreach (KeyValuePair<string, IMcpEditor> kv in Editors.Reverse())
			{
				if (string.Equals(kv.Value.Kind, needle, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(kv.Value.DisplayName, needle, StringComparison.OrdinalIgnoreCase)
					|| kv.Value.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
				{
					return kv;
				}
			}

			return null;
		}
	}
}
