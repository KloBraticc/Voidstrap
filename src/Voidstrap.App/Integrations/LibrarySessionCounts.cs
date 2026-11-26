using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Voidstrap.Models.Entities;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

internal static class LibrarySessionCounts
{
    private const int MaximumBytes = 16 * 1024 * 1024;

    internal static void Record(IReadOnlyList<ActivityData> history, IReadOnlyList<ActivityData> current)
    {
        foreach (IGrouping<long, ActivityData> account in current.Where(IsCountable).GroupBy(session => session.UserId))
            _ = Load(account.Key, history, account);
    }

    internal static Dictionary<long, int>? Read(long userId, IReadOnlyList<ActivityData> history) =>
        userId > 0 ? Load(userId, history, []) : null;

    private static Dictionary<long, int>? Load(long userId, IReadOnlyList<ActivityData> history, IEnumerable<ActivityData> current)
    {
        const string LOG_IDENT = "LibrarySessionCounts::Load";
        try
        {
            if (string.IsNullOrEmpty(Paths.Data))
                return null;

            string account = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string file = Path.Combine(Paths.Data, "LibrarySessionCounts", account + ".json");
            using InterProcessLock writeLock = new("LibrarySessionCounts-" + account, TimeSpan.FromSeconds(2));
            if (!writeLock.IsAcquired)
                return null;

            bool exists = File.Exists(file) || File.Exists(file + ".bak");
            Dictionary<long, HashSet<string>>? index = null;
            if (exists && !JsonFile.TryLoad(file, JsonOptions.Tolerant, out index, out _, out Exception? failure, MaximumBytes))
            {
                if (failure != null)
                    App.Logger?.WriteException(LOG_IDENT, failure);
                return null;
            }
            index ??= new();

            bool changed = false;
            ActivityData? previous = null;
            foreach (ActivityData session in history.Concat(current)
                .Where(session => IsCountable(session) && session.UserId == userId)
                .DistinctBy(PlayTimeStore.GetSessionKey)
                .OrderBy(session => session.TimeJoined))
            {
                string? key = session.LocalPlaySessionKey;
                if (string.IsNullOrEmpty(key) && !exists && !ContinuesLegacySession(previous, session))
                    key = PlayTimeStore.GetSessionKey(session);
                previous = session;
                if (string.IsNullOrEmpty(key))
                    continue;

                if (!index.TryGetValue(session.UniverseId, out HashSet<string>? sessions) || sessions == null)
                    index[session.UniverseId] = sessions = new(StringComparer.Ordinal);
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
                changed |= sessions.Add(Convert.ToHexString(hash.AsSpan(0, 16)));
            }
            if (changed)
            {
                string json = JsonSerializer.Serialize(index);
                if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
                    return null;
                JsonFile.WriteAtomicText(file, json);
            }

            return index.Where(entry => entry.Key > 0 && entry.Value != null)
                .ToDictionary(entry => entry.Key, entry => entry.Value.Count(value => value is { Length: 32 }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            App.Logger?.WriteException(LOG_IDENT, ex);
            return null;
        }
    }

    private static bool IsCountable(ActivityData? session) =>
        session is { UserId: > 0, UniverseId: > 0, PlaceId: > 0 } && session.TimeJoined != default;

    private static bool ContinuesLegacySession(ActivityData? previous, ActivityData next) =>
        next.IsTeleport && previous?.UniverseId == next.UniverseId && previous.TimeLeft is DateTime left
        && next.TimeJoined >= left && next.TimeJoined - left <= TimeSpan.FromMinutes(3);
}
