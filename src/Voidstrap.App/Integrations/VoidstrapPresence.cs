using System;
using System.Text;

namespace Voidstrap.Integrations;

internal sealed record VoidstrapPresenceContext(
	string Owner,
	string Details,
	string State,
	string ImageUrl = "",
	string ImageText = "",
	string ButtonLabel = "",
	string ButtonUrl = "");

internal static class VoidstrapPresence
{
	private static readonly object Sync = new();

	private static VoidstrapPresenceContext? _context;

	public static VoidstrapPresenceContext? Current
	{
		get
		{
			lock (Sync)
			{
				return _context;
			}
		}
	}

	public static void Set(VoidstrapPresenceContext context)
	{
		lock (Sync)
		{
			_context = context;
		}
	}

	public static void Clear(string owner)
	{
		lock (Sync)
		{
			if (_context != null && string.Equals(_context.Owner, owner, StringComparison.Ordinal))
			{
				_context = null;
			}
		}
	}

	public static string Clip(string? value, int maxBytes)
	{
		string text = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
		while (text.Contains("  ", StringComparison.Ordinal))
		{
			text = text.Replace("  ", " ", StringComparison.Ordinal);
		}
		if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
		{
			return text;
		}
		const string ellipsis = "...";
		int budget = maxBytes - Encoding.UTF8.GetByteCount(ellipsis);
		StringBuilder builder = new StringBuilder();
		int used = 0;
		System.Globalization.TextElementEnumerator elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
		while (elements.MoveNext())
		{
			string element = elements.GetTextElement();
			int size = Encoding.UTF8.GetByteCount(element);
			if (used + size > budget)
			{
				break;
			}
			builder.Append(element);
			used += size;
		}
		return builder.ToString().TrimEnd() + ellipsis;
	}

	public static bool IsWebUrl(string? value)
	{
		return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
			&& uri.Scheme == Uri.UriSchemeHttps
			&& (value?.Length ?? 0) <= 512;
	}
}
