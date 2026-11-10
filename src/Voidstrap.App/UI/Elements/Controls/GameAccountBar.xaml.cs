using System.Windows;
using System.Windows.Controls;
using Voidstrap.Integrations;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Controls;

public partial class GameAccountBar : UserControl
{
    public static readonly DependencyProperty PlaceIdProperty = DependencyProperty.Register(
        nameof(PlaceId),
        typeof(long),
        typeof(GameAccountBar),
        new PropertyMetadata(0L, OnPlaceIdChanged)
    );

    private CancellationTokenSource? _cts;
    private long _userId;
    private bool _syncing;
    private bool _switching;

    public GameAccountBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? AccountSwitched;

    public long PlaceId
    {
        get => (long)GetValue(PlaceIdProperty);
        set => SetValue(PlaceIdProperty, value);
    }

    private static void OnPlaceIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GameAccountBar bar)
            bar.SelectSavedOutfit();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SavedAccounts.Changed -= OnAccountsChanged;
        SavedAccounts.Changed += OnAccountsChanged;
        Restart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SavedAccounts.Changed -= OnAccountsChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _userId = 0;
        _syncing = true;
        AccountBox.ItemsSource = null;
        OutfitBox.ItemsSource = null;
        _syncing = false;
    }

    private void OnAccountsChanged()
    {
        Dispatcher.BeginInvoke(RestartIfAccountChanged);
    }

    private void RestartIfAccountChanged()
    {
        if (IsLoaded && !_switching && (SavedAccounts.Current?.UserId ?? 0) != _userId)
            Restart();
    }

    private void Restart()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = LoadAsync(_cts.Token);
    }

    private async Task LoadAsync(CancellationToken token)
    {
        const string LOG_IDENT = "GameAccountBar::LoadAsync";

        try
        {
            CurrentAccountInfo? current = SavedAccounts.Current ?? await SavedAccounts.RefreshCurrentAsync(token);
            if (token.IsCancellationRequested)
                return;
            _userId = current?.UserId ?? 0;
            List<SavedAccount> accounts = SavedAccounts.SwitchingSupported ? await SavedAccounts.LoadAsync(token) : [];
            if (token.IsCancellationRequested)
                return;
            Task outfits = LoadOutfitsAsync(accounts.FirstOrDefault(x => x.UserId == _userId), token);
            if (accounts.Count > 0)
            {
                Dictionary<long, string> avatars = new();
                try
                {
                    avatars = await SavedAccounts.GetAvatarsAsync(accounts.Select(x => x.UserId), token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not load avatars: " + ex.Message);
                }
                if (token.IsCancellationRequested)
                    return;
                List<AccountChoice> choices = accounts
                    .Select(x => new AccountChoice { Account = x, AvatarUrl = avatars.TryGetValue(x.UserId, out string? url) && url.Length > 0 ? url : null })
                    .ToList();
                _syncing = true;
                AccountBox.ItemsSource = choices;
                AccountBox.SelectedItem = choices.FirstOrDefault(x => x.Account.UserId == _userId);
                _syncing = false;
                AccountBox.Visibility = Visibility.Visible;
                Visibility = Visibility.Visible;
            }
            await outfits;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
    }

    private async Task LoadOutfitsAsync(SavedAccount? account, CancellationToken token)
    {
        const string LOG_IDENT = "GameAccountBar::LoadOutfitsAsync";

        try
        {
            List<OutfitRow> outfits = new();
            if (_userId > 0)
            {
                string? cookie = account != null ? SavedAccounts.ReadCurrentCookie(account) : RobloxOutfits.CurrentCookie();
                outfits = await RobloxOutfits.ListAsync(_userId, cookie, token);
            }
            if (token.IsCancellationRequested)
                return;
            if (outfits.Count == 0)
            {
                _syncing = true;
                OutfitBox.ItemsSource = null;
                _syncing = false;
                OutfitBox.Visibility = Visibility.Collapsed;
                return;
            }
            List<OutfitRow> rows = [new OutfitRow(0, Strings.Accounts_Outfits_GameNone), .. outfits];
            _syncing = true;
            OutfitBox.ItemsSource = rows;
            _syncing = false;
            SelectSavedOutfit();
            OutfitBox.Visibility = Visibility.Visible;
            Visibility = Visibility.Visible;
            await RobloxOutfits.FillThumbnailsAsync(outfits, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The outfit list could not be loaded: " + ex.Message);
        }
    }

    private void SelectSavedOutfit()
    {
        if (OutfitBox.ItemsSource is not List<OutfitRow> rows)
            return;
        long saved = RobloxOutfits.GetSaved(_userId, PlaceId);
        _syncing = true;
        OutfitBox.SelectedItem = rows.FirstOrDefault(x => x.Id == saved) ?? rows[0];
        _syncing = false;
        OutfitBox.IsEnabled = PlaceId > 0;
    }

    private void OutfitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || OutfitBox.SelectedItem is not OutfitRow row)
            return;
        RobloxOutfits.SetSaved(_userId, PlaceId, row.Id);
    }

    private async void AccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        const string LOG_IDENT = "GameAccountBar::AccountBox_SelectionChanged";

        if (_syncing || _switching || AccountBox.SelectedItem is not AccountChoice { Account: SavedAccount target })
            return;
        AccountChoice? previous = e.RemovedItems.Count > 0 ? e.RemovedItems[0] as AccountChoice : null;
        _switching = true;
        AccountBox.IsEnabled = false;
        try
        {
            if (target.Expired)
            {
                Frontend.ShowMessageBox(Strings.Accounts_Expired, MessageBoxImage.Warning);
                Revert(previous);
                return;
            }
            if (!await AccountsViewModel.EnsureRobloxClosedAsync())
            {
                Revert(previous);
                return;
            }
            await SavedAccounts.SwitchAsync(target);
            App.Logger.WriteLine(LOG_IDENT, "Switched account from the game account bar");
            AccountSwitched?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The account switch failed: " + ex.Message);
            Frontend.ShowMessageBox(ex is FileNotFoundException ? Strings.Accounts_MissingLogin : ex.Message, MessageBoxImage.Warning);
            Revert(previous);
        }
        finally
        {
            _switching = false;
            AccountBox.IsEnabled = true;
            RestartIfAccountChanged();
        }
    }

    private void Revert(AccountChoice? previous)
    {
        _syncing = true;
        AccountBox.SelectedItem = previous;
        _syncing = false;
    }
}
