using Voidstrap.UI.Elements.Overlay;

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

    public bool GameCardsEnabled
    {
        get => App.Settings.Prop.SessionDockGameCards;
        set
        {
            if (App.Settings.Prop.SessionDockGameCards == value)
                return;

            App.Settings.Prop.SessionDockGameCards = value;
            App.Settings.SaveDeferred();
            RefreshGameCards();
        }
    }

    public int GameCardSize
    {
        get => AppSettings.ClampSessionDockGameCardSize(App.Settings.Prop.SessionDockGameCardSize);
        set
        {
            value = AppSettings.ClampSessionDockGameCardSize(value);
            if (App.Settings.Prop.SessionDockGameCardSize == value)
                return;

            App.Settings.Prop.SessionDockGameCardSize = value;
            App.Settings.SaveDeferred();
            RefreshGameCards();
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(SoundsEnabled));

    internal void RefreshGameCards()
    {
        OnPropertyChanged(nameof(GameCardsEnabled));
        OnPropertyChanged(nameof(GameCardSize));
        SessionDock.ActiveInstance?.RefreshGameCardSettings();
    }
}
