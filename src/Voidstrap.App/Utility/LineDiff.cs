using System;
using System.Collections.Generic;

namespace Voidstrap.Utility;

public enum DiffKind
{
    Same,
    Added,
    Removed
}

public sealed class DiffLine
{
    public DiffKind Kind { get; set; }

    public string Text { get; set; } = "";

    public int OldNumber { get; set; }

    public int NewNumber { get; set; }

    public string Marker => Kind switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "-",
        _ => " "
    };

    public string OldLabel => OldNumber > 0 ? OldNumber.ToString() : "";

    public string NewLabel => NewNumber > 0 ? NewNumber.ToString() : "";
}

public static class LineDiff
{
    public static List<DiffLine> Compare(string before, string after)
    {
        string[] oldLines = Split(before);
        string[] newLines = Split(after);

        int[,] lengths = new int[oldLines.Length + 1, newLines.Length + 1];

        for (int i = oldLines.Length - 1; i >= 0; i--)
        {
            for (int j = newLines.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        List<DiffLine> result = new List<DiffLine>();
        int oldIndex = 0;
        int newIndex = 0;

        while (oldIndex < oldLines.Length && newIndex < newLines.Length)
        {
            if (string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal))
            {
                result.Add(new DiffLine
                {
                    Kind = DiffKind.Same,
                    Text = oldLines[oldIndex],
                    OldNumber = oldIndex + 1,
                    NewNumber = newIndex + 1
                });
                oldIndex++;
                newIndex++;
            }
            else if (lengths[oldIndex + 1, newIndex] >= lengths[oldIndex, newIndex + 1])
            {
                result.Add(new DiffLine
                {
                    Kind = DiffKind.Removed,
                    Text = oldLines[oldIndex],
                    OldNumber = oldIndex + 1
                });
                oldIndex++;
            }
            else
            {
                result.Add(new DiffLine
                {
                    Kind = DiffKind.Added,
                    Text = newLines[newIndex],
                    NewNumber = newIndex + 1
                });
                newIndex++;
            }
        }

        while (oldIndex < oldLines.Length)
        {
            result.Add(new DiffLine
            {
                Kind = DiffKind.Removed,
                Text = oldLines[oldIndex],
                OldNumber = oldIndex + 1
            });
            oldIndex++;
        }

        while (newIndex < newLines.Length)
        {
            result.Add(new DiffLine
            {
                Kind = DiffKind.Added,
                Text = newLines[newIndex],
                NewNumber = newIndex + 1
            });
            newIndex++;
        }

        return result;
    }

    public static List<DiffLine> Collapse(List<DiffLine> lines, int context = 3)
    {
        bool[] keep = new bool[lines.Count];

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Same)
                continue;

            for (int j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++)
                keep[j] = true;
        }

        List<DiffLine> result = new List<DiffLine>();
        bool skipping = false;

        for (int i = 0; i < lines.Count; i++)
        {
            if (keep[i])
            {
                result.Add(lines[i]);
                skipping = false;
                continue;
            }

            if (!skipping)
            {
                result.Add(new DiffLine { Kind = DiffKind.Same, Text = "..." });
                skipping = true;
            }
        }

        return result;
    }

    private static string[] Split(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        return text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    }
}
