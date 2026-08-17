using System;
using System.Collections.Generic;

namespace Voidstrap.Models;

public class QuestProgressLine
{
    public string Title { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public long UniverseId { get; init; }

    public int Goal { get; init; }

    public int Progress { get; init; }

    public bool Complete { get; init; }

    public int Xp { get; init; }

    public QuestLiveSession? Session { get; set; }

    public int LiveProgress => Session is null
        ? Progress
        : Session.LiveProgress(Kind, UniverseId, Goal, Progress, Complete);

    public bool LiveComplete => Complete || (Goal > 0 && LiveProgress >= Goal);

    public double Percent => Goal > 0 ? Math.Max(0.0, Math.Min(100.0, LiveProgress * 100.0 / Goal)) : 0.0;

    public string Display => LiveComplete
        ? Title + "  earned " + Xp + " XP"
        : Title + "  " + LiveProgress + " / " + Goal + "  worth " + Xp + " XP";

}

public class QuestProgressSnapshot
{
    public long UniverseId { get; init; }

    public int MinutesToday { get; init; }

    public QuestLiveSession? Session { get; set; }

    public List<QuestProgressLine> Lines { get; } = new List<QuestProgressLine>();

    public string Signature
    {
        get
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append(UniverseId).Append(':').Append(MinutesToday);
            foreach (QuestProgressLine line in Lines)
                builder.Append('|').Append(line.Title).Append('=').Append(line.LiveProgress).Append('/').Append(line.Goal);
            return builder.ToString();
        }
    }
}
