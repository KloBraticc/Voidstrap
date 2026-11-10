namespace Voidstrap.UI.ViewModels.Settings;

internal sealed class AvatarItemRow : NotifyPropertyChangedViewModel
{
    private string? _imageUrl;
    private bool _worn;
    private bool _busy;

    public AvatarItemRow(long id, string name, string typeName, bool isOutfit)
    {
        Id = id;
        Name = name;
        TypeName = typeName;
        IsOutfit = isOutfit;
    }

    public long Id { get; }

    public string Name { get; }

    public string TypeName { get; }

    public bool IsOutfit { get; }

    public string? ImageUrl
    {
        get => _imageUrl;
        set => SetProperty(ref _imageUrl, value);
    }

    public bool Worn
    {
        get => _worn;
        set => SetProperty(ref _worn, value);
    }

    public bool Busy
    {
        get => _busy;
        set => SetProperty(ref _busy, value);
    }
}
