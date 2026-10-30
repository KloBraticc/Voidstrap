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

	IReadOnlyList<string> Files => Array.Empty<string>();

	string? ReadFile(string relativePath) => null;

	McpEditResult WriteFile(string relativePath, string text) =>
		new(false, "This editor has a single document, edit it without a file argument.");

	McpEditResult DeleteFile(string relativePath) => new(false, "This editor has a single document, files cannot be deleted.");

	IReadOnlyList<string> Languages => Array.Empty<string>();

	McpEditResult SetLanguage(string language) => new(false, "This editor uses a single language.");
}

public static class McpEditorRegistry
{
	private static readonly object Gate = new();

	private static readonly List<KeyValuePair<string, IMcpEditor>> Editors = [];

	private static int _counter;

	public static event Action<string>? Unregistered;

	public static string? Register(IMcpEditor editor)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return null;

		string id = editor.Kind + "_" + Interlocked.Increment(ref _counter);

		lock (Gate)
			Editors.Add(new KeyValuePair<string, IMcpEditor>(id, editor));

		McpBridgeServer.EnsureStarted();
		return id;
	}

	public static void Unregister(string? id)
	{
		if (string.IsNullOrEmpty(id))
			return;

		bool removed;
		lock (Gate)
			removed = Editors.RemoveAll(entry => string.Equals(entry.Key, id, StringComparison.OrdinalIgnoreCase)) > 0;

		if (removed)
			Unregistered?.Invoke(id);
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
				return Editors[^1];

			string needle = id.Trim();
			int colon = needle.IndexOf(':');
			string? kindPart = colon > 0 ? needle[..colon] : null;
			string namePart = colon > 0 ? needle[(colon + 1)..] : needle;

			foreach (KeyValuePair<string, IMcpEditor> entry in Editors)
			{
				if (string.Equals(entry.Key, needle, StringComparison.OrdinalIgnoreCase))
					return entry;
			}

			for (int i = Editors.Count - 1; i >= 0; i--)
			{
				KeyValuePair<string, IMcpEditor> entry = Editors[i];
				if (
					kindPart != null
					&& !entry.Value.Kind.StartsWith(kindPart, StringComparison.OrdinalIgnoreCase)
					&& !kindPart.StartsWith(entry.Value.Kind, StringComparison.OrdinalIgnoreCase)
				)
					continue;

				if (
					string.Equals(entry.Value.Kind, namePart, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(entry.Value.DisplayName, namePart, StringComparison.OrdinalIgnoreCase)
				)
				{
					return entry;
				}
			}

			for (int i = Editors.Count - 1; i >= 0; i--)
			{
				KeyValuePair<string, IMcpEditor> entry = Editors[i];
				if (entry.Value.DisplayName.Contains(namePart, StringComparison.OrdinalIgnoreCase))
					return entry;
			}

			return null;
		}
	}
}
