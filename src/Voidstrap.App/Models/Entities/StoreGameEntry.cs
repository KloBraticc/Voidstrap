using System.Collections.Generic;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.Models.Entities;

public class StoreGameEntry : LibraryGameEntry
{
    private bool _isCurrent;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
                return;
            _isCurrent = value;
            OnPropertyChanged(nameof(IsCurrent));
        }
    }

    public override System.Windows.Visibility RemoveVisibility => System.Windows.Visibility.Collapsed;

    public string GenreName { get; set; } = "";

    public List<string> Screenshots { get; set; } = new();

    public IEnumerable<string> PreviewShots => System.Linq.Enumerable.Take(Screenshots, 4);

    public string PlayingLine => string.Format(Resources.Strings.Library_Store_Playing, PlayingDisplay);

    public string PriceLine => Resources.Strings.Library_Store_Free;
}
