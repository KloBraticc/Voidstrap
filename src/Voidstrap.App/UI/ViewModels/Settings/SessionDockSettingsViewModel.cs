namespace Voidstrap.UI.ViewModels.Settings;

public sealed class SessionDockSettingsViewModel : NotifyPropertyChangedViewModel
{
    public static SessionDockSettingsViewModel Instance { get; } = new();

    private SessionDockSettingsViewModel()
    {
    }

    public bool SoundsEnabled
    {
        get => App.Settings.Prop.SessionDockSounds;
        set
        {
            if (App.Settings.Prop.SessionDockSounds == value)
                return;

            App.Settings.Prop.SessionDockSounds = value;
            App.Settings.SaveDeferred();
            Refresh();
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(SoundsEnabled));
}
