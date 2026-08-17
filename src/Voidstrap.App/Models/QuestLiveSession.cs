using System;
using System.Text.Json;

namespace Voidstrap.Models;

public class QuestLiveSession
{
    public long UniverseId { get; init; }

    public int CreditedMinutes { get; init; }

    public long CarryMs { get; init; }

    public long LastBeatMs { get; init; }

    public long GraceMs { get; init; }

    public long SkewMs { get; init; }

    public int PlayMultiplier { get; init; } = 1;

    public bool IsBeating
    {
        get
        {
            long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + SkewMs - LastBeatMs;
            return since >= 0L && since <= GraceMs;
        }
    }

    public int LiveProgress(string kind, long questUniverseId, int goal, int serverProgress, bool claimed)
    {
        if (claimed || kind != "playtime")
            return serverProgress;
        if (questUniverseId != 0L && UniverseId != 0L && questUniverseId != UniverseId)
            return serverProgress;

        long since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + SkewMs - LastBeatMs;
        if (since > GraceMs)
            return serverProgress;

        int multiplier = PlayMultiplier < 1 ? 1 : PlayMultiplier;
        long elapsed = CreditedMinutes * 60000L + CarryMs + Math.Max(0L, since) * multiplier;
        int minutes = (int)Math.Min(int.MaxValue, elapsed / 60000L);
        return Math.Max(serverProgress, Math.Min(goal, minutes));
    }

    public static QuestLiveSession? FromResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("session", out JsonElement session) || session.ValueKind != JsonValueKind.Object)
            return null;

        long serverNow = ReadLong(root, "serverNow");
        if (serverNow <= 0L)
            return null;

        return new QuestLiveSession
        {
            UniverseId = ReadLong(session, "universeId"),
            CreditedMinutes = (int)Math.Max(0L, Math.Min(int.MaxValue, ReadLong(session, "creditedMinutes"))),
            CarryMs = Math.Max(0L, ReadLong(session, "carryMs")),
            LastBeatMs = ReadLong(session, "lastBeat"),
            GraceMs = Math.Max(0L, ReadLong(session, "graceMs")),
            SkewMs = serverNow - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PlayMultiplier = (int)Math.Max(1L, Math.Min(4L, ReadLong(session, "playMultiplier"))),
        };
    }

    private static long ReadLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long parsed)
                ? parsed
                : 0L;
    }
}
