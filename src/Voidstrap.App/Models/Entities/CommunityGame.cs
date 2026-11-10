using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Voidstrap.Models.Entities;

public sealed class CommunityGameInfo
{
    public long PlaceId { get; set; }
    public long UniverseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
}

public sealed class CommunityGame : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string? _iconUrl;
    private string? _thumbnailUrl;
    private string _creatorName = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long PlaceId { get; init; }
    public long UniverseId { get; set; }
    public long LatestTime { get; set; }
    public ObservableCollection<CommunityReview> Reviews { get; } = new();

    public string Name
    {
        get => _name;
        set => Set(ref _name, value, nameof(Name));
    }

    public string? IconUrl
    {
        get => _iconUrl;
        set => Set(ref _iconUrl, string.IsNullOrEmpty(value) ? null : value, nameof(IconUrl));
    }

    public string? ThumbnailUrl
    {
        get => _thumbnailUrl;
        set => Set(ref _thumbnailUrl, string.IsNullOrEmpty(value) ? null : value, nameof(ThumbnailUrl));
    }

    public string CreatorName
    {
        get => _creatorName;
        set
        {
            Set(ref _creatorName, value, nameof(CreatorName));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCreator)));
        }
    }

    public bool HasCreator => _creatorName.Length > 0;

    public void Apply(CommunityGameInfo info)
    {
        if (info.Name.Length > 0)
            Name = info.Name;
        if (info.UniverseId > 0)
            UniverseId = info.UniverseId;
        IconUrl = info.IconUrl;
        ThumbnailUrl = info.ThumbnailUrl;
        CreatorName = info.CreatorName;
    }

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
