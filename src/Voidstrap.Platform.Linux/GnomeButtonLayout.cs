namespace Voidstrap.Platform.Linux;

public sealed class GnomeButtonLayout
{
	private static readonly string[] KnownButtons = ["minimize", "maximize", "close"];

	public static GnomeButtonLayout Default { get; } = new([], KnownButtons);

	private GnomeButtonLayout(IReadOnlyList<string> left, IReadOnlyList<string> right)
	{
		Left = left;
		Right = right;
	}

	public IReadOnlyList<string> Left { get; }

	public IReadOnlyList<string> Right { get; }

	public bool OnLeft => Left.Count > 0;

	public IReadOnlyList<string> Order => OnLeft ? Left : Right;

	public static GnomeButtonLayout Parse(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return Default;
		}

		string trimmed = raw.Trim().Trim('\'', '"');
		int separator = trimmed.IndexOf(':');
		if (separator < 0)
		{
			return Default;
		}

		string[] left = ParseSide(trimmed[..separator]);
		string[] right = ParseSide(trimmed[(separator + 1)..]);
		return left.Length == 0 && right.Length == 0
			? Default
			: new GnomeButtonLayout(left, right);
	}

	private static string[] ParseSide(string side)
	{
		return side
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(entry => entry.ToLowerInvariant())
			.Where(entry => KnownButtons.Contains(entry))
			.Distinct()
			.ToArray();
	}
}
