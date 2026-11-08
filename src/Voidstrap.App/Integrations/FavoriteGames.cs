namespace Voidstrap.Integrations;

internal static class FavoriteGames
{
    public static event EventHandler<long>? Changed;

    private static List<long> Ids => App.Settings.Prop.FavoriteGamePlaceIds ??= new List<long>();

    public static bool Contains(long placeId) => placeId > 0 && Ids.Contains(placeId);

    public static HashSet<long> Snapshot() => Ids.ToHashSet();

    public static bool Toggle(long placeId)
    {
        bool favorite = !Contains(placeId);
        Set(placeId, favorite);
        return favorite;
    }

    public static void Set(long placeId, bool favorite)
    {
        if (placeId <= 0 || Contains(placeId) == favorite)
            return;
        List<long> ids = Ids;
        ids.RemoveAll(id => id == placeId);
        if (favorite)
            ids.Insert(0, placeId);
        App.Settings.SaveDeferred();
        Changed?.Invoke(null, placeId);
    }
}
