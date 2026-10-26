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
		if (string.IsNullOrWhiteSpace(id))
			return null;

		lock (Gate)
			return Editors.TryGetValue(id.Trim(), out IMcpEditor? editor) ? editor : null;
	}
}
