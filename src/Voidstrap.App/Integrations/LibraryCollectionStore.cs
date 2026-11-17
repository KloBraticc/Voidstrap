namespace Voidstrap.Integrations;

internal static class LibraryCollectionStore
{
    private const string LOG_IDENT = "LibraryCollectionStore";

    private const long MaximumBytes = 8L * 1024 * 1024;

    private const int MaximumNameLength = 64;

    private static readonly object Sync = new();

    private static List<LibraryGameCollection>? _collections;

    private static Dictionary<long, List<LibrarySidebarGroup>>? _groupIndex;

    private static string CollectionsPath => Path.Combine(Paths.Library, "Collections.json");

    public static bool HasAny
    {
        get
        {
            lock (Sync)
            {
                return Collections().Count > 0;
            }
        }
    }

    public static IReadOnlyList<LibraryGameCollection> All()
    {
        lock (Sync)
        {
            return Ordered(Collections()).Select(Copy).ToList();
        }
    }

    public static LibrarySidebarGroup[] SidebarGroups(long universeId, bool pinned)
    {
        List<LibrarySidebarGroup> groups = new();
        if (pinned)
            groups.Add(new LibrarySidebarGroup("pinned", Strings.Library_GroupPinned, 0, null));
        bool hasCollections;
        lock (Sync)
        {
            hasCollections = Collections().Count > 0;
            if (GroupIndex().TryGetValue(universeId, out List<LibrarySidebarGroup>? collectionGroups))
                groups.AddRange(collectionGroups);
        }
        if (groups.Count == 0)
            groups.Add(
                new LibrarySidebarGroup(
                    "all",
                    hasCollections ? Strings.Library_GroupUncategorized : Strings.Library_GroupAll,
                    int.MaxValue,
                    null
                )
            );
        return groups.ToArray();
    }

    private static Dictionary<long, List<LibrarySidebarGroup>> GroupIndex()
    {
        if (_groupIndex != null)
            return _groupIndex;
        Dictionary<long, List<LibrarySidebarGroup>> index = new();
        int order = 1;
        foreach (LibraryGameCollection collection in Ordered(Collections()))
        {
            LibrarySidebarGroup group = new LibrarySidebarGroup("collection:" + collection.Id, collection.Name, order++, collection.Id);
            foreach (long universeId in collection.UniverseIds)
            {
                if (!index.TryGetValue(universeId, out List<LibrarySidebarGroup>? list))
                    index[universeId] = list = new List<LibrarySidebarGroup>();
                list.Add(group);
            }
        }
        return _groupIndex = index;
    }

    public static IReadOnlyList<LibraryGameCollection> For(long universeId)
    {
        lock (Sync)
        {
            return Ordered(Collections().Where(collection => collection.UniverseIds.Contains(universeId))).Select(Copy).ToList();
        }
    }

    public static void Add(string collectionId, long universeId)
    {
        lock (Sync)
        {
            LibraryGameCollection? collection = Collections().FirstOrDefault(entry => entry.Id == collectionId);
            if (collection == null || collection.UniverseIds.Contains(universeId))
                return;
            collection.UniverseIds.Add(universeId);
            Write();
        }
    }

    public static void Remove(string collectionId, long universeId)
    {
        lock (Sync)
        {
            List<LibraryGameCollection> collections = Collections();
            LibraryGameCollection? collection = collections.FirstOrDefault(entry => entry.Id == collectionId);
            if (collection == null || !collection.UniverseIds.Remove(universeId))
                return;
            if (collection.UniverseIds.Count == 0)
                collections.Remove(collection);
            Write();
        }
    }

    public static void RemoveGame(long universeId)
    {
        lock (Sync)
        {
            List<LibraryGameCollection> collections = Collections();
            bool changed = false;
            foreach (LibraryGameCollection collection in collections)
                changed |= collection.UniverseIds.Remove(universeId);
            if (!changed)
                return;
            collections.RemoveAll(collection => collection.UniverseIds.Count == 0);
            Write();
        }
    }

    public static string? Create(string name, long universeId)
    {
        string clean = CleanName(name);
        if (clean.Length == 0)
            return null;
        lock (Sync)
        {
            List<LibraryGameCollection> collections = Collections();
            LibraryGameCollection? existing = collections.FirstOrDefault(entry => entry.Name.Equals(clean, StringComparison.CurrentCultureIgnoreCase));
            if (existing == null)
            {
                existing = new LibraryGameCollection { Id = Guid.NewGuid().ToString("N"), Name = clean };
                collections.Add(existing);
            }
            if (!existing.UniverseIds.Contains(universeId))
                existing.UniverseIds.Add(universeId);
            Write();
            return existing.Id;
        }
    }

    public static void Rename(string collectionId, string name)
    {
        string clean = CleanName(name);
        if (clean.Length == 0)
            return;
        lock (Sync)
        {
            List<LibraryGameCollection> collections = Collections();
            LibraryGameCollection? collection = collections.FirstOrDefault(entry => entry.Id == collectionId);
            if (collection == null || collection.Name == clean)
                return;
            LibraryGameCollection? other = collections.FirstOrDefault(
                entry => entry.Id != collectionId && entry.Name.Equals(clean, StringComparison.CurrentCultureIgnoreCase)
            );
            if (other != null)
            {
                foreach (long universeId in collection.UniverseIds.Where(id => !other.UniverseIds.Contains(id)))
                    other.UniverseIds.Add(universeId);
                collections.Remove(collection);
            }
            else
                collection.Name = clean;
            Write();
        }
    }

    public static void Delete(string collectionId)
    {
        lock (Sync)
        {
            if (Collections().RemoveAll(entry => entry.Id == collectionId) > 0)
                Write();
        }
    }

    private static string CleanName(string name)
    {
        string clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaximumNameLength ? clean[..MaximumNameLength].TrimEnd() : clean;
    }

    private static IEnumerable<LibraryGameCollection> Ordered(IEnumerable<LibraryGameCollection> collections)
    {
        return collections.OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static LibraryGameCollection Copy(LibraryGameCollection source)
    {
        return new LibraryGameCollection { Id = source.Id, Name = source.Name, UniverseIds = new List<long>(source.UniverseIds) };
    }

    private static List<LibraryGameCollection> Collections()
    {
        if (_collections != null)
            return _collections;
        try
        {
            if (File.Exists(CollectionsPath))
            {
                List<LibraryGameCollection> loaded =
                    JsonFile.Deserialize<List<LibraryGameCollection>>(CollectionsPath, JsonOptions.Tolerant, MaximumBytes) ?? [];
                return _collections = loaded
                    .Where(collection => !string.IsNullOrWhiteSpace(collection.Id) && !string.IsNullOrWhiteSpace(collection.Name))
                    .Select(collection => new LibraryGameCollection
                    {
                        Id = collection.Id,
                        Name = CleanName(collection.Name),
                        UniverseIds = (collection.UniverseIds ?? []).Where(id => id > 0).Distinct().ToList(),
                    })
                    .Where(collection => collection.Name.Length > 0 && collection.UniverseIds.Count > 0)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The library collections could not be read: " + ex.Message);
        }
        return _collections = new List<LibraryGameCollection>();
    }

    private static void Write()
    {
        _groupIndex = null;
        try
        {
            JsonFile.SerializeAtomic(CollectionsPath, _collections ?? [], JsonOptions.Indented, false);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The library collections could not be saved: " + ex.Message);
        }
    }
}
