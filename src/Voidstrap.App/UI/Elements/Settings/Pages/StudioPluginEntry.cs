using System;
using System.Globalization;
using Voidstrap.Integrations.Studio;
using Voidstrap.Resources;
using Voidstrap.UI.ViewModels;

namespace Voidstrap.UI.Elements.Settings.Pages;

public sealed class StudioPluginEntry : NotifyPropertyChangedViewModel
{
    private StudioPluginListing? _listing;

    private InstalledStudioPlugin? _installed;

    private string _icon = "";

    private bool _busy;

    public StudioPluginEntry(StudioPluginListing? listing, InstalledStudioPlugin? installed)
    {
        _listing = listing;
        _installed = installed;
    }

    public StudioPluginListing? Listing => _listing;

    public InstalledStudioPlugin? Installed => _installed;

    public long? AssetId => _listing?.Id ?? _installed?.AssetId;

    public string Name => _listing?.Name is { Length: > 0 } name ? name : _installed?.Name ?? "";

    public string Summary => _listing != null ? FirstLine(_listing.Description) : Strings.Ui_Mods_StudioPlugins_Local;

    public string Description => _listing?.Description ?? "";

    public string Creator => _listing?.Creator ?? "";

    public string CreatorLine =>
        _listing == null ? ""
        : _listing.Verified ? _listing.Creator + " ✓"
        : _listing.Creator;

    public string Category =>
        _listing == null
            ? System.IO.Path.GetExtension(_installed?.Path ?? "").TrimStart('.').ToUpperInvariant()
            : CategoryName(_listing.Category);

    public string Rating =>
        _listing == null || _listing.UpVotes + _listing.DownVotes == 0
            ? ""
            : string.Format(
                Strings.Ui_Mods_StudioPlugins_Liked,
                Math.Round(100.0 * _listing.UpVotes / (_listing.UpVotes + _listing.DownVotes))
            );

    public bool IsPaid => _listing is { Price: > 0 };

    public string PriceText =>
        _listing == null ? ""
        : IsPaid ? string.Format(Strings.Ui_Mods_StudioPlugins_Price, _listing.Price.ToString("C2", CultureInfo.GetCultureInfo("en-US")))
        : Strings.Ui_Mods_StudioPlugins_Free;

    public string UpdatedText =>
        (_listing?.Updated ?? _installed?.InstalledAt)?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "";

    public bool UpdateAvailable => _installed != null && _listing?.Updated is DateTime updated && updated > _installed.InstalledAt;

    public string StatusLabel =>
        _busy ? Strings.Ui_Mods_StudioPlugins_Installing
        : UpdateAvailable ? Strings.Ui_Mods_StudioPlugins_UpdateAvailable
        : _installed != null ? Strings.Ui_Pages_Installed
        : "";

    public string PrimaryLabel =>
        UpdateAvailable ? Strings.Ui_Mods_StudioPlugins_Update
        : _installed != null ? Strings.Ui_Mods_StudioPlugins_Remove
        : Strings.Ui_Mods_StudioPlugins_Install;

    public bool CanAct => !_busy && (_installed != null || (_listing != null && !IsPaid));

    public string Icon
    {
        get => _icon;
        set
        {
            if (_icon == value)
                return;
            _icon = value;
            OnPropertyChanged();
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value)
                return;
            _busy = value;
            RaiseState();
        }
    }

    public void Apply(StudioPluginListing? listing, InstalledStudioPlugin? installed)
    {
        if (listing != null)
            _listing = listing;
        _installed = installed;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(CreatorLine));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(Rating));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(UpdatedText));
        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(CanAct));
    }

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }
        return "";
    }

    private static string CategoryName(string path)
    {
        int split = path.LastIndexOf("__", StringComparison.Ordinal);
        string name = (split >= 0 ? path[(split + 2)..] : path).Replace('_', ' ').Trim();
        return name.Length == 0 ? "" : char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..];
    }
}
