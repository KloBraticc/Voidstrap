using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Models;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Bootstrapper;
using Voidstrap.UI.Elements.Dialogs;
using Voidstrap.UI.Elements.Editor;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public class AppearanceViewModel : NotifyPropertyChangedViewModel
{
    private const string ClearFontRestartKey = "appearance.clearFont";

    private bool _clearFont;

    public static class AudioEvents
    {
        public static event Action<string?>? StartupAudioChanged;

        public static void RaiseStartupAudioChanged(string? path)
        {
            AudioEvents.StartupAudioChanged?.Invoke(path);
        }
    }

    public class BackgroundSettings
    {
        public string? BackgroundFilePath { get; set; }

        public double GradientOpacity { get; set; } = 1.0;

        public double BlackOverlayOpacity { get; set; }

        public bool DisplayEverywhere { get; set; }
    }

    private readonly Page _page;

    public int[] ZoomOptions { get; } = new int[] { 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200 };

    public int UiZoomPercent
    {
        get
        {
            return App.Settings.Prop.UiZoomPercent;
        }
        set
        {
            if (App.Settings.Prop.UiZoomPercent == value)
            {
                return;
            }
            App.Settings.Prop.UiZoomPercent = value;
            App.Settings.Save();
            OnPropertyChanged(nameof(UiZoomPercent));
            Voidstrap.UI.Elements.Settings.MainWindow.ApplyUiZoomToOpenWindows();
        }
    }

    private static readonly Dictionary<string, byte[]> _appFontHeaders = new Dictionary<string, byte[]>
    {
        {
            "ttf",
            new byte[4] { 0, 1, 0, 0 }
        },
        {
            "otf",
            new byte[4] { 79, 84, 84, 79 }
        },
        {
            "ttc",
            new byte[4] { 116, 116, 99, 102 }
        }
    };

    private const string FileName = "BackgroundSettings.json";

    private static readonly string FilePath = Paths.BackgroundSettings;

    public static double SharedGradientOpacity { get; private set; } = LoadSharedGradientOpacity();

    private static double LoadSharedGradientOpacity()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                BackgroundSettings settings = JsonFile.Deserialize<BackgroundSettings>(FilePath, JsonOptions.Tolerant);
                return Math.Clamp(settings.GradientOpacity, 0.0, 1.0);
            }
        }
        catch (Exception)
        {
        }
        return 1.0;
    }

    private BackgroundSettings _settings;

    private static readonly object _saveLock = new object();

    private static CancellationTokenSource? _saveCts;

    private static int _saveGeneration;

    public IEnumerable<Theme> BindableThemes => Voidstrap.Extensions.ThemeEx.Selections;

    public IEnumerable<BackdropType> BackdropOptions { get; } = Enum.GetValues<BackdropType>();

    public ICommand PreviewBootstrapperCommand => new RelayCommand(PreviewBootstrapper);

    public ICommand BrowseCustomIconLocationCommand => new RelayCommand(BrowseCustomIconLocation);

    public ICommand AddCustomThemeCommand => new RelayCommand(AddCustomTheme);

    public ICommand DeleteCustomThemeCommand => new AsyncRelayCommand(DeleteCustomThemeAsync);

    public ICommand RenameCustomThemeCommand => new AsyncRelayCommand(RenameCustomThemeAsync);

    public ICommand EditCustomThemeCommand => new RelayCommand(EditCustomTheme);

    public ICommand ExportCustomThemeCommand => new RelayCommand(ExportCustomTheme);

    public ICommand ViewCustomThemeFilesCommand => new AsyncRelayCommand(ViewCustomThemeFilesAsync);

    public ICommand PublishCustomThemeCommand => new RelayCommand(PublishCustomTheme);

    private readonly ObservableCollection<Voidstrap.Integrations.PublishedThemeInfo> _publishedThemes = new();

    private Voidstrap.Integrations.PublishedThemeInfo? _selectedPublishedTheme;

    private bool _publishedBusy;

    private string _publishedStatus = "Sign in and press Refresh to see the themes you have published.";

    public ObservableCollection<Voidstrap.Integrations.PublishedThemeInfo> PublishedThemes => _publishedThemes;

    public Voidstrap.Integrations.PublishedThemeInfo? SelectedPublishedTheme
    {
        get => _selectedPublishedTheme;
        set
        {
            _selectedPublishedTheme = value;
            OnPropertyChanged(nameof(SelectedPublishedTheme));
            OnPropertyChanged(nameof(IsPublishedThemeSelected));
            OnPropertyChanged(nameof(SelectedPublishedFacts));
            OnPropertyChanged(nameof(SelectedPublishedState));
            OnPropertyChanged(nameof(CanCommitSelected));
            OnPropertyChanged(nameof(CanFetchSelected));
        }
    }

    public bool IsPublishedThemeSelected => _selectedPublishedTheme != null && !_publishedBusy;

    public bool CanRefreshPublishedThemes => !_publishedBusy;

    public bool CanCommitSelected => _selectedPublishedTheme != null && _selectedPublishedTheme.HasLocalCopy && !_publishedBusy;

    public bool CanFetchSelected => _selectedPublishedTheme != null && !_publishedBusy
        && (!_selectedPublishedTheme.HasLocalCopy || _selectedPublishedTheme.HasChanges);

    public string PublishedStatus
    {
        get => _publishedStatus;
        private set
        {
            _publishedStatus = value;
            OnPropertyChanged(nameof(PublishedStatus));
        }
    }

    public string SelectedPublishedFacts => _selectedPublishedTheme?.Facts ?? "";

    public string SelectedPublishedState => _selectedPublishedTheme?.State ?? "";

    public ICommand RefreshPublishedThemesCommand => new AsyncRelayCommand(RefreshPublishedThemesAsync);

    public ICommand FetchPublishedThemeCommand => new AsyncRelayCommand(FetchPublishedThemeAsync);

    public ICommand CommitPublishedThemeCommand => new RelayCommand(CommitPublishedTheme);

    public ICommand OpenPublishedThemeCommand => new RelayCommand(OpenPublishedTheme);

    public ICommand ViewPublishedChangesCommand => new AsyncRelayCommand(ViewPublishedChangesAsync);

    private void SetPublishedBusy(bool busy)
    {
        _publishedBusy = busy;
        OnPropertyChanged(nameof(IsPublishedThemeSelected));
        OnPropertyChanged(nameof(CanRefreshPublishedThemes));
        OnPropertyChanged(nameof(CanCommitSelected));
        OnPropertyChanged(nameof(CanFetchSelected));
    }

    private async Task RefreshPublishedThemesAsync()
    {
        if (_publishedBusy)
            return;

        if (string.IsNullOrWhiteSpace(WebsiteAuth.GetToken()))
        {
            PublishedStatus = "Sign in to your Voidstrap account first, from the Home page.";
            return;
        }

        SetPublishedBusy(true);
        PublishedStatus = "Loading your published themes...";

        try
        {
            var themes = await Voidstrap.Integrations.BootstrapperThemes.GetMineAsync().ConfigureAwait(true);

            string? keep = _selectedPublishedTheme?.Id;

            _publishedThemes.Clear();

            foreach (var theme in themes)
                _publishedThemes.Add(theme);

            SelectedPublishedTheme = _publishedThemes.FirstOrDefault(t => t.Id == keep) ?? _publishedThemes.FirstOrDefault();

            int pending = themes.Count(t => t.HasChanges);
            int local = themes.Count(t => t.HasLocalCopy);

            PublishedStatus = themes.Count == 0
                ? "You have not published any themes yet."
                : themes.Count + (themes.Count == 1 ? " theme published, " : " themes published, ") +
                  local + " on this PC, " +
                  (pending == 0 ? "nothing waiting to be committed." : pending + " with uncommitted changes.");
        }
        catch (Exception ex)
        {
            PublishedStatus = ex.Message;
            App.Logger.WriteLine("AppearanceViewModel::RefreshPublishedThemes", ex.Message);
        }
        finally
        {
            SetPublishedBusy(false);
        }
    }

    private async Task FetchPublishedThemeAsync()
    {
        if (_publishedBusy)
            return;

        var theme = _selectedPublishedTheme;

        if (theme == null)
            return;

        if (theme.HasLocalCopy && !theme.HasChanges)
        {
            PublishedStatus = theme.Name + " is already on this PC and matches the published version, so there is nothing to pull.";
            return;
        }

        if (theme.HasLocalCopy && theme.HasChanges)
        {
            string warning = "This PC has changes that are not published yet (" + theme.Pending!.Describe() +
                "). Pulling replaces them with the published version and they cannot be recovered. Pull anyway?";

            if (Frontend.ShowMessageBox(warning, MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            {
                PublishedStatus = "Pull cancelled, your local changes were kept.";
                return;
            }
        }

        SetPublishedBusy(true);
        PublishedStatus = "Downloading " + theme.Name + "...";

        bool refresh = false;
        try
        {
            string folder = await Voidstrap.Integrations.BootstrapperThemes
                .InstallFromWebsiteAsync(theme.Id, default, theme.LocalFolder)
                .ConfigureAwait(true);

            PopulateCustomThemes();
            PublishedStatus = theme.Name + " is now in your themes as " + folder + ".";
            refresh = true;

            Frontend.ShowMessageBox("This preview has updated.", MessageBoxImage.Information, MessageBoxButton.OK);
        }
        catch (Exception ex)
        {
            PublishedStatus = ex.Message;
            Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Hand);
        }
        finally
        {
            SetPublishedBusy(false);
        }
        if (refresh)
            await RefreshPublishedThemesAsync().ConfigureAwait(true);
    }

    private void CommitPublishedTheme()
    {
        var theme = _selectedPublishedTheme;

        if (theme == null || !theme.HasLocalCopy)
            return;

        if (!theme.HasChanges)
        {
            Frontend.ShowMessageBox("This PC already matches the published version, so there is nothing to commit.", MessageBoxImage.Asterisk);
            return;
        }

        PublishThemeDialog dialog = new PublishThemeDialog(theme.LocalFolder!, new Voidstrap.Integrations.ThemePublishRecord
        {
            Id = theme.Id,
            Name = theme.Name,
            Description = theme.Description,
            Version = theme.Version
        })
        {
            Owner = Application.Current.MainWindow
        };

        dialog.ShowDialog();
        _ = RefreshPublishedThemesAsync();
    }


    private async Task ViewPublishedChangesAsync()
    {
        if (_publishedBusy)
            return;

        var theme = _selectedPublishedTheme;

        if (theme == null || !theme.HasLocalCopy)
            return;

        SetPublishedBusy(true);

        try
        {
            var changes = await Voidstrap.Integrations.BootstrapperThemes
                .LoadChangesAsync(theme.LocalFolder!, theme.Id)
                .ConfigureAwait(true);

            ThemeChangesDialog dialog = new ThemeChangesDialog(theme.Name, changes)
            {
                Owner = Application.Current.MainWindow
            };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            PublishedStatus = ex.Message;
            App.Logger.WriteLine("AppearanceViewModel::ViewPublishedChanges", ex.Message);
        }
        finally
        {
            SetPublishedBusy(false);
        }
    }

    private void OpenPublishedTheme()
    {
        var theme = _selectedPublishedTheme;

        if (theme == null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = App.WebsiteBaseUrl + "/pages/theme.html?id=" + Uri.EscapeDataString(theme.Id),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("AppearanceViewModel::OpenPublishedTheme", ex.Message);
        }
    }


    public ICommand ImportBackgroundCommand { get; }

    public ICommand RemoveBackgroundCommand { get; }

    public ICommand ImportStartupAudioCommand { get; }

    public ICommand RemoveStartupAudioCommand { get; }

    public ICommand ManageAppFontCommand => new RelayCommand(ManageAppFont);

    public Visibility ChooseAppFontVisibility
    {
        get
        {
            if (!AppFont.HasCustomFont)
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }
    }

    public Visibility RemoveAppFontVisibility
    {
        get
        {
            if (!AppFont.HasCustomFont)
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }
    }

    public System.Windows.Media.FontFamily AppFontFamily => AppFont.CurrentFontFamily;

    public string AppFontName
    {
        get
        {
            string currentFontName = AppFont.CurrentFontName;
            if (!string.IsNullOrWhiteSpace(currentFontName))
            {
                return currentFontName;
            }
            return "Remove Custom Font";
        }
    }

    public bool ShowLaunchProfile
    {
        get
        {
            return App.Settings.Prop.ShowLaunchProfile;
        }
        set
        {
            App.Settings.Prop.ShowLaunchProfile = value;
        }
    }

    public bool Snowww
    {
        get
        {
            return App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw;
        }
        set
        {
            if (App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw == value)
            {
                return;
            }
            App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged(nameof(Snowww));
            ApplyToMainWindow(delegate (MainWindow mainWindow)
            {
                mainWindow.ApplySnow(value);
            });
        }
    }

    public bool GRADmentFR
    {
        get
        {
            return App.Settings.Prop.GRADmentFR;
        }
        set
        {
            if (App.Settings.Prop.GRADmentFR == value)
            {
                return;
            }
            App.Settings.Prop.GRADmentFR = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged(nameof(GRADmentFR));
            ApplyToMainWindow(delegate (MainWindow mainWindow)
            {
                mainWindow.ApplyGradientMovement(value);
            });
        }
    }

    public bool ClearFont
    {
        get
        {
            return _clearFont;
        }
        set
        {
            if (_clearFont == value)
            {
                return;
            }
            _clearFont = value;
            OnPropertyChanged(nameof(ClearFont));
            RestartNotificationService.TrackApplicationSetting(
                ClearFontRestartKey,
                value,
                "Clear Font changed",
                "Restart Voidstrap to apply the new text rendering mode.",
                ApplyClearFontSetting);
        }
    }

    private void ApplyClearFontSetting()
    {
        App.Settings.Prop.ClearFont = _clearFont;
        App.Settings.SaveDeferred();
    }

    public bool SmooothBARRyesirikikthxlucipook
    {
        get
        {
            return App.Settings.Prop.SmooothBARRyesirikikthxlucipook;
        }
        set
        {
            if (App.Settings.Prop.SmooothBARRyesirikikthxlucipook == value)
            {
                return;
            }
            App.Settings.Prop.SmooothBARRyesirikikthxlucipook = value;
            App.Settings.Save();
            Wpf.Ui.Controls.SmoothScroll.SetGlobalEnabled(value);
        }
    }

    public string? BackgroundFilePath
    {
        get
        {
            return _settings.BackgroundFilePath;
        }
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
            if (_settings.BackgroundFilePath != value)
            {
                _settings.BackgroundFilePath = value;
                OnPropertyChanged(nameof(BackgroundFilePath));
                SaveSettings();
                PushGlobalBackground();
            }
        }
    }

    public double GradientOpacity
    {
        get
        {
            return _settings.GradientOpacity;
        }
        set
        {
            value = Math.Clamp(value, 0.0, 1.0);
            if (_settings.GradientOpacity != value)
            {
                _settings.GradientOpacity = value;
                SharedGradientOpacity = value;
                OnPropertyChanged(nameof(GradientOpacity));
                SaveSettings();
                PushGlobalBackground();
                Voidstrap.UI.WindowBackdrop.ApplyGradientOpacityChange();
            }
        }
    }

    public double BlackOverlayOpacity
    {
        get
        {
            return _settings.BlackOverlayOpacity;
        }
        set
        {
            value = Math.Clamp(value, 0.0, 1.0);
            if (_settings.BlackOverlayOpacity != value)
            {
                _settings.BlackOverlayOpacity = value;
                OnPropertyChanged(nameof(BlackOverlayOpacity));
                SaveSettings();
                PushGlobalBackground();
            }
        }
    }

    public bool BackgroundEverywhere
    {
        get
        {
            return _settings.DisplayEverywhere;
        }
        set
        {
            if (_settings.DisplayEverywhere != value)
            {
                _settings.DisplayEverywhere = value;
                OnPropertyChanged(nameof(BackgroundEverywhere));
                SaveSettings();
                PushGlobalBackground();
            }
        }
    }

    public bool HasCustomBackground =>
        !string.IsNullOrEmpty(_settings.BackgroundFilePath) && System.IO.File.Exists(_settings.BackgroundFilePath);

    public void ApplyLiveBackgroundState(GlobalBackground.State state)
    {
        bool pathChanged = !string.Equals(_settings.BackgroundFilePath, state.FilePath, StringComparison.OrdinalIgnoreCase);
        bool gradientChanged = _settings.GradientOpacity != state.GradientOpacity;
        bool overlayChanged = _settings.BlackOverlayOpacity != state.BlackOverlayOpacity;
        bool everywhereChanged = _settings.DisplayEverywhere != state.DisplayEverywhere;
        _settings.BackgroundFilePath = state.FilePath;
        _settings.GradientOpacity = state.GradientOpacity;
        _settings.BlackOverlayOpacity = state.BlackOverlayOpacity;
        _settings.DisplayEverywhere = state.DisplayEverywhere;
        SharedGradientOpacity = state.GradientOpacity;
        if (pathChanged)
        {
            OnPropertyChanged(nameof(BackgroundFilePath));
            OnPropertyChanged(nameof(HasCustomBackground));
        }
        if (gradientChanged)
        {
            OnPropertyChanged(nameof(GradientOpacity));
        }
        if (overlayChanged)
        {
            OnPropertyChanged(nameof(BlackOverlayOpacity));
        }
        if (everywhereChanged)
        {
            OnPropertyChanged(nameof(BackgroundEverywhere));
        }
    }

    public ICommand ImportBackgroundCommand2 { get; }

    public ICommand RemoveBackgroundCommand2 { get; }

    public IEnumerable<Theme> Themes { get; } = Enum.GetValues(typeof(Theme)).Cast<Theme>();

    public BackdropType SelectedBackdrop
    {
        get
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && App.Settings.Prop.WindowBackdrop != BackdropType.None)
            {
                App.Settings.Prop.WindowBackdrop = BackdropType.None;
                App.Settings.SaveDeferred();
                OnPropertyChanged(nameof(SelectedBackdrop));
            }
            return App.Settings.Prop.WindowBackdrop;
        }
        set
        {
            if (App.Settings.Prop.WindowBackdrop == value)
            {
                ApplyBackdropToMainWindow();
                return;
            }
            App.Settings.Prop.WindowBackdrop = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged(nameof(SelectedBackdrop));
            ApplyBackdropToMainWindow();
        }
    }

    private static void ApplyBackdropToMainWindow()
    {
        if (Application.Current == null)
        {
            return;
        }
        foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray())
        {
            if (window is Voidstrap.UI.Elements.Settings.MainWindow mainWindow)
            {
                Voidstrap.UI.WindowBackdrop.ApplyMainWindow(mainWindow);
            }
            else
            {
                Voidstrap.UI.WindowBackdrop.Apply(window);
            }
        }
        foreach (Voidstrap.UI.Elements.ContextMenu.MenuContainer menu in Application.Current.Windows.OfType<Voidstrap.UI.Elements.ContextMenu.MenuContainer>())
        {
            menu.ApplyBackdrop();
        }
    }

    public Theme Theme
    {
        get
        {
            return App.Settings.Prop.Theme2;
        }
        set
        {
            if (App.Settings.Prop.Theme2 == value)
            {
                return;
            }
            App.Settings.Prop.Theme2 = value;
            MainWindow window = null;
            try
            {
                if (Application.Current?.Windows != null)
                {
                    foreach (Window window2 in Application.Current.Windows)
                    {
                        if (window2 is MainWindow mainWindow)
                        {
                            window = mainWindow;
                            break;
                        }
                    }
                }
            }
            catch
            {
            }
            if (window == null)
            {
                return;
            }
            try
            {
                ThemeTransition.Animate(window, delegate
                {
                    window.ApplyTheme();
                });
            }
            catch
            {
                try
                {
                    window.ApplyTheme();
                }
                catch
                {
                }
            }
        }
    }

    private readonly string _autoTranslateOption;

    private string _selectedLanguage;

    private string _selectedAutoTranslateLanguage;

    public static string AutoTranslateOption => Strings.Dialog_LanguageSelector_AutoTranslate;

    public static List<string> Languages => Locale.GetLanguages();

    public List<string> LanguageOptions
    {
        get
        {
            List<string> list = Locale.GetLanguages();
            if (list.Count > 0)
            {
                list.Insert(1, _autoTranslateOption);
            }
            else
            {
                list.Add(_autoTranslateOption);
            }
            return list;
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (string.IsNullOrEmpty(value) || _selectedLanguage == value)
            {
                return;
            }
            _selectedLanguage = value;
            OnPropertyChanged(nameof(SelectedLanguage));
            if (value == _autoTranslateOption)
            {
                App.Settings.Prop.AutoTranslate = true;
                App.Settings.Prop.AutoTranslateLanguage = GetSelectedAutoTranslateCode();
                TranslationService.Initialize();
                LiveLanguageRefresher.Initialize();
                App.Settings.Save();
                OnPropertyChanged(nameof(AutoTranslateVisibility));
                OnPropertyChanged(nameof(SelectedAutoTranslateLanguage));
                LiveLanguageRefresher.RefreshAllOpenWindows();
                return;
            }
            App.Settings.Prop.AutoTranslate = false;
            string identifier = Locale.GetIdentifierFromName(value);
            App.Settings.Prop.Locale = identifier;
            App.Settings.Save();
            Locale.Set(identifier);
            OnPropertyChanged(nameof(AutoTranslateVisibility));
        }
    }

    public List<string> AutoTranslateLanguages => TranslationService.AvailableLanguages.Values.OrderBy((string x) => x).ToList();

    public string SelectedAutoTranslateLanguage
    {
        get => _selectedAutoTranslateLanguage;
        set
        {
            if (string.IsNullOrEmpty(value) || _selectedAutoTranslateLanguage == value)
            {
                return;
            }
            _selectedAutoTranslateLanguage = value;
            OnPropertyChanged(nameof(SelectedAutoTranslateLanguage));
            KeyValuePair<string, string> match = TranslationService.AvailableLanguages.FirstOrDefault((KeyValuePair<string, string> kv) => kv.Value == value);
            if (!string.IsNullOrEmpty(match.Key))
            {
                App.Settings.Prop.AutoTranslate = true;
                App.Settings.Prop.AutoTranslateLanguage = match.Key;
                TranslationService.Initialize();
                LiveLanguageRefresher.Initialize();
                App.Settings.Save();
                Voidstrap.UI.LiveLanguageRefresher.RefreshAllOpenWindows();
            }
        }
    }

    public Visibility AutoTranslateVisibility => _selectedLanguage == _autoTranslateOption ? Visibility.Visible : Visibility.Collapsed;

    public IEnumerable<BootstrapperStyle> Dialogs { get; } = BootstrapperStyleEx.Selections;

    public BootstrapperStyle Dialog
    {
        get
        {
            return App.Settings.Prop.BootstrapperStyle;
        }
        set
        {
            App.Settings.Prop.BootstrapperStyle = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged("Dialog");
            OnPropertyChanged("CustomThemesExpanded");
            OnPropertyChanged("LauncherExtrasEnabled");
        }
    }

    public bool CustomThemesExpanded => App.Settings.Prop.BootstrapperStyle == BootstrapperStyle.CustomDialog;

    public bool LauncherExtrasEnabled => App.Settings.Prop.BootstrapperStyle != BootstrapperStyle.CustomDialog;

    public IEnumerable<BootstrapperScale> BootstrapperScales { get; } = Enum.GetValues<BootstrapperScale>();

    public BootstrapperScale BootstrapperScale
    {
        get
        {
            return App.Settings.Prop.BootstrapperScale;
        }
        set
        {
            App.Settings.Prop.BootstrapperScale = value;
        }
    }

    public ObservableCollection<BootstrapperIconEntry> Icons { get; set; } = new ObservableCollection<BootstrapperIconEntry>();

    public BootstrapperIcon Icon
    {
        get
        {
            return App.Settings.Prop.BootstrapperIcon;
        }
        set
        {
            if (value == BootstrapperIcon.IconCustom && !HasValidCustomIcon() && !PromptCustomIcon())
            {
                OnPropertyChanged("Icon");
                return;
            }
            App.Settings.Prop.BootstrapperIcon = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged("Icon");
        }
    }

    public Visibility StudioIconVisibility
    {
        get
        {
            if (!App.IsStudioVisible)
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }
    }

    public BootstrapperIcon StudioIcon
    {
        get
        {
            return App.Settings.Prop.StudioBootstrapperIcon;
        }
        set
        {
            if (value == BootstrapperIcon.IconCustom && !HasValidCustomIcon() && !PromptCustomIcon())
            {
                OnPropertyChanged("StudioIcon");
                return;
            }
            App.Settings.Prop.StudioBootstrapperIcon = value;
            OnPropertyChanged("StudioIcon");
            App.Settings.SaveDeferred();
        }
    }

    public string Title
    {
        get
        {
            return App.Settings.Prop.BootstrapperTitle;
        }
        set
        {
            App.Settings.Prop.BootstrapperTitle = value;
        }
    }

    public string CustomIconLocation
    {
        get
        {
            return App.Settings.Prop.BootstrapperIconCustomLocation;
        }
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                if (App.Settings.Prop.BootstrapperIcon == BootstrapperIcon.IconCustom)
                {
                    App.Settings.Prop.BootstrapperIcon = BootstrapperIcon.IconVoidstrap;
                }
            }
            else
            {
                App.Settings.Prop.BootstrapperIcon = BootstrapperIcon.IconCustom;
            }
            App.Settings.Prop.BootstrapperIconCustomLocation = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged("Icon");
            OnPropertyChanged("Icons");
            OnPropertyChanged("CustomIconLocation");
        }
    }

    public string? SelectedCustomTheme
    {
        get
        {
            return App.Settings.Prop.SelectedCustomTheme;
        }
        set
        {
            App.Settings.Prop.SelectedCustomTheme = value;
            OnPropertyChanged("IsCustomThemeSelected");
        }
    }

    public string SelectedCustomThemeName { get; set; } = "";

    public int SelectedCustomThemeIndex { get; set; }

    public ObservableCollection<string> CustomThemes { get; set; } = new ObservableCollection<string>();

    public bool IsCustomThemeSelected => SelectedCustomTheme != null;

    private void ManageAppFont()
    {
        if (AppFont.HasCustomFont)
        {
            AppFont.Clear();
        }
        else
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = Strings.Menu_FontFiles + "|*.ttf;*.otf;*.ttc"
            };
            if (openFileDialog.ShowDialog() != true)
            {
                return;
            }
            try
            {
                string key = Path.GetExtension(openFileDialog.FileName).TrimStart('.').ToLowerInvariant();
                byte[] array = File.ReadAllBytes(openFileDialog.FileName).Take(4).ToArray();
                if (!_appFontHeaders.TryGetValue(key, out byte[] value) || !value.SequenceEqual(array))
                {
                    Frontend.ShowMessageBox("Custom Font Invalid", MessageBoxImage.Hand);
                    return;
                }
                if (!AppFont.SetFromFile(openFileDialog.FileName))
                {
                    Frontend.ShowMessageBox("Custom Font Invalid", MessageBoxImage.Hand);
                    AppFont.Clear();
                    return;
                }
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox("Could not load font: " + ex.Message, MessageBoxImage.Hand);
                return;
            }
        }
        OnPropertyChanged("ChooseAppFontVisibility");
        OnPropertyChanged("RemoveAppFontVisibility");
        OnPropertyChanged("AppFontName");
        OnPropertyChanged("AppFontFamily");
    }

    public AppearanceViewModel()
    {
        bool savedClearFont = App.Settings.Prop.ClearFont;
        RestartNotificationService.RegisterSetting(ClearFontRestartKey, savedClearFont);
        _clearFont = RestartNotificationService.TryGetPendingValue(ClearFontRestartKey, out bool pendingClearFont)
            ? pendingClearFont
            : savedClearFont;
        _autoTranslateOption = Strings.Dialog_LanguageSelector_AutoTranslate;
        _selectedAutoTranslateLanguage = GetInitialAutoTranslateLanguageName();
        _selectedLanguage = App.Settings.Prop.AutoTranslate
            ? _autoTranslateOption
            : Locale.SupportedLocales.TryGetValue(App.Settings.Prop.Locale, out string? configuredLocale) ? configuredLocale : Locale.SupportedLocales[Locale.DefaultLocale];
        ImportBackgroundCommand = new RelayCommand(ImportBackground);
        RemoveBackgroundCommand = new RelayCommand(RemoveBackground);
        ImportStartupAudioCommand = new RelayCommand(ImportStartupAudio);
        RemoveStartupAudioCommand = new RelayCommand(RemoveStartupAudio);
        _settings = LoadSettings();
        SharedGradientOpacity = _settings.GradientOpacity;
        ImportBackgroundCommand2 = new RelayCommand<object>(ImportFile);
        RemoveBackgroundCommand2 = new RelayCommand<object>(RemoveFile);
        foreach (BootstrapperIcon selection in BootstrapperIconEx.Selections)
        {
            Icons.Add(new BootstrapperIconEntry
            {
                IconType = selection
            });
        }
        PopulateCustomThemes();

        if (!string.IsNullOrWhiteSpace(WebsiteAuth.GetToken()))
        {
            _ = RefreshPublishedThemesAsync();
        }
    }

    private string GetInitialAutoTranslateLanguageName()
    {
        string configured = App.Settings.Prop.AutoTranslateLanguage ?? "";
        if (!string.IsNullOrEmpty(configured) && TranslationService.AvailableLanguages.TryGetValue(configured, out string? configuredName))
        {
            return configuredName;
        }
        string systemLanguage = System.Globalization.CultureInfo.CurrentUICulture.Name;
        string normalized = systemLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? systemLanguage
            : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (TranslationService.AvailableLanguages.TryGetValue(normalized, out string? detected))
        {
            return detected;
        }
        return TranslationService.AvailableLanguages["en"];
    }

    private string GetSelectedAutoTranslateCode()
    {
        KeyValuePair<string, string> match = TranslationService.AvailableLanguages.FirstOrDefault(pair => pair.Value == _selectedAutoTranslateLanguage);
        return string.IsNullOrEmpty(match.Key) ? "en" : match.Key;
    }

    private static void ApplyToMainWindow(Action<MainWindow> action)
    {
        try
        {
            Application current = Application.Current;
            if (current == null)
            {
                return;
            }
            foreach (Window window in current.Windows)
            {
                if (window is MainWindow obj)
                {
                    action(obj);
                }
            }
        }
        catch
        {
        }
    }

    private void ImportFile(object? _)
    {
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Filter = "Background Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.webm;*.avi;*.mov",
            Title = "Select Background File"
        };
        if (openFileDialog.ShowDialog() == true)
        {
            string selectedPath = Path.GetFullPath(openFileDialog.FileName);
            if (string.Equals(BackgroundFilePath, selectedPath, StringComparison.OrdinalIgnoreCase))
            {
                Voidstrap.UI.GlobalBackground.Reload();
            }
            else
            {
                BackgroundFilePath = selectedPath;
            }
            double? recommended = RecommendGradientOpacity(selectedPath);
            if (recommended.HasValue)
            {
                GradientOpacity = recommended.Value;
            }
        }
    }

    private static double? RecommendGradientOpacity(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".webm" or ".avi" or ".mov")
        {
            return null;
        }
        try
        {
            BitmapSource? image = Voidstrap.Utility.SafeImaging.FromFile(path, 64);
            if (image == null)
            {
                return null;
            }
            FormatConvertedBitmap converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0.0);
            int stride = converted.PixelWidth * 4;
            byte[] pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            if (pixels.Length == 0)
            {
                return null;
            }
            double total = 0.0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                total += 0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2];
            }
            double luminance = total / (pixels.Length / 4) / 255.0;
            return Math.Clamp(0.35 + luminance * 0.6, 0.35, 0.95);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("AppearanceViewModel::RecommendGradientOpacity", "Could not measure background lighting: " + ex.Message);
            return null;
        }
    }

    private void RemoveFile(object? _)
    {
        BackgroundFilePath = null;
    }

    private static BackgroundSettings LoadSettings()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonFile.Deserialize<BackgroundSettings>(FilePath, JsonOptions.Tolerant);
            }
        }
        catch (Exception)
        {
        }
        return new BackgroundSettings();
    }

    private void PushGlobalBackground()
    {
        OnPropertyChanged(nameof(HasCustomBackground));
        Voidstrap.UI.GlobalBackground.Update(_settings.BackgroundFilePath, _settings.GradientOpacity, _settings.BlackOverlayOpacity, _settings.DisplayEverywhere);
    }

    private void SaveSettings()
    {
        BackgroundSettings snapshot = new BackgroundSettings
        {
            BackgroundFilePath = _settings.BackgroundFilePath,
            GradientOpacity = _settings.GradientOpacity,
            BlackOverlayOpacity = _settings.BlackOverlayOpacity,
            DisplayEverywhere = _settings.DisplayEverywhere
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        int generation = Interlocked.Increment(ref _saveGeneration);
        CancellationTokenSource? previous;
        lock (_saveLock)
        {
            previous = _saveCts;
            _saveCts = cts;
        }
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        previous?.Dispose();
        CancellationToken token = cts.Token;
        Task.Run(async delegate
        {
            _ = 1;
            try
            {
                await Task.Delay(120, token).ConfigureAwait(continueOnCapturedContext: false);
                if (!token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    if (generation == Volatile.Read(ref _saveGeneration))
                        JsonFile.SerializeAtomic(FilePath, snapshot, JsonOptions.Indented);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
            finally
            {
                lock (_saveLock)
                {
                    if (ReferenceEquals(_saveCts, cts))
                    {
                        _saveCts = null;
                    }
                }
                cts.Dispose();
            }
        });
    }

    private void PreviewBootstrapper()
    {
        IBootstrapperDialog bootstrapperDialog = App.Settings.Prop.BootstrapperStyle.GetNew();
        bootstrapperDialog.Message = ((App.Settings.Prop.BootstrapperStyle == BootstrapperStyle.ByfronDialog) ? Strings.Bootstrapper_StylePreview_ImageCancel : Strings.Bootstrapper_StylePreview_TextCancel);
        bootstrapperDialog.CancelEnabled = true;
        AudioPlayerHelper.PlayStartupAudio();
        if (bootstrapperDialog is Window window)
        {
			window.Closed += OnPreviewBootstrapperClosed;
        }
        bootstrapperDialog.ShowBootstrapper();
    }

	private static void OnPreviewBootstrapperClosed(object? sender, EventArgs e)
	{
		if (sender is Window window)
			window.Closed -= OnPreviewBootstrapperClosed;
		AudioPlayerHelper.StopAudio();
	}

    private void BrowseCustomIconLocation()
    {
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Filter = Strings.Menu_IconFiles + "|*.ico"
        };
        if (openFileDialog.ShowDialog() == true)
        {
            CustomIconLocation = openFileDialog.FileName;
        }
    }

    private static bool HasValidCustomIcon()
    {
        string bootstrapperIconCustomLocation = App.Settings.Prop.BootstrapperIconCustomLocation;
        if (!string.IsNullOrEmpty(bootstrapperIconCustomLocation))
        {
            return File.Exists(bootstrapperIconCustomLocation);
        }
        return false;
    }

    private bool PromptCustomIcon()
    {
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Filter = Strings.Menu_IconFiles + "|*.ico"
        };
        if (openFileDialog.ShowDialog() != true)
        {
            return false;
        }
        App.Settings.Prop.BootstrapperIconCustomLocation = openFileDialog.FileName;
        App.Settings.SaveDeferred();
        OnPropertyChanged("CustomIconLocation");
        return true;
    }

    private void AddCustomTheme()
    {
        AddCustomThemeDialog addCustomThemeDialog = new AddCustomThemeDialog();
        addCustomThemeDialog.ShowDialog();
        if (addCustomThemeDialog.Created)
        {
            CustomThemes.Add(addCustomThemeDialog.ThemeName);
            SelectedCustomThemeIndex = CustomThemes.Count - 1;
            OnPropertyChanged("SelectedCustomThemeIndex");
            OnPropertyChanged("IsCustomThemeSelected");
            if (addCustomThemeDialog.OpenEditor)
            {
                EditCustomTheme();
            }
        }
    }

    private void ImportStartupAudio()
    {
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Title = "Select a Startup Sound",
            Filter = "Audio Files|*.mp3;*.wav;*.ogg;*.flac;*.wma",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (openFileDialog.ShowDialog() != true)
        {
            return;
        }
        string fileName = openFileDialog.FileName;
        if (!File.Exists(fileName))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Paths.Media);
            string[] files = Directory.GetFiles(Paths.Media, "startup_audio.*");
            foreach (string path in files)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
            string path2 = "startup_audio" + Path.GetExtension(fileName);
            string text = Path.Combine(Paths.Media, path2);
            File.Copy(fileName, text, overwrite: true);
            AudioEvents.RaiseStartupAudioChanged(text);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("AppearanceViewModel::ImportStartupAudio", ex);
        }
    }

    private void RemoveStartupAudio()
    {
        try
        {
            string[] files = Directory.GetFiles(Paths.Media, "startup_audio.*");
            foreach (string path in files)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
            AudioEvents.RaiseStartupAudioChanged(null);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("AppearanceViewModel::RemoveStartupAudio", ex);
        }
    }

    private async Task DeleteCustomThemeAsync()
    {
        string? name = SelectedCustomTheme;
        if (name != null)
        {
            try
            {
                await DeleteCustomThemeStructure(name);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AppearanceViewModel::DeleteCustomTheme", ex);
                Frontend.ShowMessageBox(string.Format(Strings.Menu_Appearance_CustomThemes_DeleteFailed, name, ex.Message), MessageBoxImage.Hand);
                return;
            }
            CustomThemes.Remove(name);
            if (CustomThemes.Any())
            {
                SelectedCustomThemeIndex = CustomThemes.Count - 1;
                OnPropertyChanged("SelectedCustomThemeIndex");
            }
            SelectedCustomTheme = null;
        }
    }

    private async Task RenameCustomThemeAsync()
    {
        string? oldName = SelectedCustomTheme;
        if (oldName == null)
        {
            return;
        }

        string newName = SelectedCustomThemeName;

        if (string.IsNullOrWhiteSpace(newName))
        {
            Frontend.ShowMessageBox("Name cannot be empty.", MessageBoxImage.Hand);
            return;
        }

        PathValidator.ValidationResult validationResult = PathValidator.IsFileNameValid(newName);
        if (validationResult != PathValidator.ValidationResult.Ok)
        {
            object message = validationResult switch
            {
                PathValidator.ValidationResult.IllegalCharacter => "Name contains illegal characters.",
                PathValidator.ValidationResult.ReservedFileName => "Name is reserved.",
                _ => "Unknown validation error.",
            };
            App.Logger.WriteLine("AppearanceViewModel::RenameCustomTheme", "Validation result: " + validationResult);
            Frontend.ShowMessageBox((string)message, MessageBoxImage.Hand);
            return;
        }

        if (Voidstrap.Utility.Platform.IsWindows && (newName.EndsWith(" ") || newName.EndsWith(".")))
        {
            Frontend.ShowMessageBox("Windows does not allow names that end in a period or space.", MessageBoxImage.Hand);
            return;
        }

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (string folder in Directory.GetDirectories(Paths.CustomThemes))
        {
            if (string.Equals(Path.GetFileName(folder), newName, StringComparison.OrdinalIgnoreCase))
            {
                Frontend.ShowMessageBox("A theme with that name already exists.", MessageBoxImage.Hand);
                return;
            }
        }

        try
        {
            await RenameCustomThemeStructure(oldName, newName);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("AppearanceViewModel::RenameCustomTheme", ex);
            Frontend.ShowMessageBox(string.Format(Strings.Menu_Appearance_CustomThemes_RenameFailed, oldName, ex.Message), MessageBoxImage.Hand);
            return;
        }

        int num = CustomThemes.IndexOf(oldName);
        if (num != -1)
        {
            CustomThemes[num] = newName;
            SelectedCustomThemeIndex = num;
        }
        if (App.Settings.Prop.SelectedCustomTheme == oldName)
        {
            App.Settings.Prop.SelectedCustomTheme = newName;
            App.Settings.SaveDeferred();
        }
        SelectedCustomThemeName = newName;
        OnPropertyChanged("SelectedCustomTheme");
        OnPropertyChanged("SelectedCustomThemeName");
        OnPropertyChanged("SelectedCustomThemeIndex");
    }

    private void EditCustomTheme()
    {
        if (SelectedCustomTheme != null)
        {
            new BootstrapperEditorWindow(SelectedCustomTheme).ShowDialog();
        }
    }


    private async Task ViewCustomThemeFilesAsync()
    {
        if (SelectedCustomTheme == null)
            return;

        try
        {
            var files = await Voidstrap.Integrations.BootstrapperThemes
                .LoadLocalFilesAsync(SelectedCustomTheme)
                .ConfigureAwait(true);

            if (files.Count == 0)
            {
                Frontend.ShowMessageBox("That theme has no files yet.", MessageBoxImage.Asterisk);
                return;
            }

            ThemeChangesDialog dialog = new ThemeChangesDialog(SelectedCustomTheme, files, true)
            {
                Owner = Application.Current.MainWindow
            };

            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Hand);
        }
    }

    private void PublishCustomTheme()
    {
        if (SelectedCustomTheme == null)
        {
            return;
        }

        string themeDir = Path.Combine(Paths.CustomThemes, SelectedCustomTheme);

        if (!Directory.Exists(themeDir))
        {
            Frontend.ShowMessageBox("That theme folder no longer exists.", MessageBoxImage.Hand);
            return;
        }

        if (string.IsNullOrWhiteSpace(WebsiteAuth.GetToken()))
        {
            Frontend.ShowMessageBox("Sign in to your Voidstrap account first, from the Home page.", MessageBoxImage.Exclamation);
            return;
        }

        var record = Voidstrap.Integrations.BootstrapperThemes.ReadPublishRecord(SelectedCustomTheme);

        if (record == null)
        {
            string? problem = DescribeExportProblem(themeDir);

            if (problem != null && Frontend.ShowMessageBox(problem, MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var dialog = new Voidstrap.UI.Elements.Dialogs.PublishThemeDialog(SelectedCustomTheme, record)
        {
            Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        };

        dialog.ShowDialog();
    }

    private void ExportCustomTheme()
    {
        if (SelectedCustomTheme == null)
        {
            return;
        }

        string themeDir = Path.Combine(Paths.CustomThemes, SelectedCustomTheme);

        if (!Directory.Exists(themeDir))
        {
            Frontend.ShowMessageBox("That theme folder no longer exists.", MessageBoxImage.Hand);
            return;
        }

        string? problem = DescribeExportProblem(themeDir);
        if (problem != null)
        {
            if (Frontend.ShowMessageBox(problem, MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        SaveFileDialog saveFileDialog = new SaveFileDialog
        {
            FileName = SelectedCustomTheme + ".zip",
            Filter = Strings.FileTypes_ZipArchive + "|*.zip",
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = "zip"
        };

        if (saveFileDialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            using (FileStream destination = File.Create(saveFileDialog.FileName))
            using (ZipOutputStream zipOutputStream = new ZipOutputStream(destination) { IsStreamOwner = false })
            {
                zipOutputStream.SetLevel(9);

                foreach (string item in Directory.EnumerateFiles(themeDir, "*.*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(themeDir, item).Replace(Path.DirectorySeparatorChar, '/');

                    ZipEntry entry = new ZipEntry(relative)
                    {
                        DateTime = File.GetLastWriteTime(item)
                    };

                    zipOutputStream.PutNextEntry(entry);

                    using FileStream fileStream = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fileStream.CopyTo(zipOutputStream);
                    zipOutputStream.CloseEntry();
                }

                zipOutputStream.Finish();
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("AppearanceViewModel::ExportCustomTheme", "Export failed");
            App.Logger.WriteException("AppearanceViewModel::ExportCustomTheme", ex);
            Frontend.ShowMessageBox("Could not export the theme: " + ex.Message, MessageBoxImage.Hand);
            return;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + saveFileDialog.FileName + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("AppearanceViewModel::ExportCustomTheme", "Could not reveal the archive: " + ex.Message);
        }
    }

    private static string? DescribeExportProblem(string themeDir)
    {
        List<string> problems = new List<string>();

        string themeFile = Path.Combine(themeDir, "Theme.xml");

        if (!File.Exists(themeFile))
        {
            problems.Add("There is no Theme.xml in this folder, so the export will not load on another device.");
        }
        else
        {
            try
            {
                XElement root = XElement.Load(themeFile);

                foreach (XAttribute attribute in root.DescendantsAndSelf().Attributes())
                {
                    string value = attribute.Value;

                    if (value.Length > 2 && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
                    {
                        problems.Add("<" + attribute.Parent!.Name + "> uses an absolute path in " + attribute.Name + ", which will not exist on another device. Use theme:// instead.");
                        break;
                    }
                }

                foreach (XAttribute attribute in root.DescendantsAndSelf().Attributes())
                {
                    string value = attribute.Value;

                    if (!value.StartsWith("theme://", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relative = value["theme://".Length..];
                    int hash = relative.LastIndexOf('#');
                    if (hash >= 0)
                        relative = relative[..hash];

                    string resolved = Path.Combine(themeDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(resolved))
                    {
                        problems.Add("The file " + relative + " is referenced but missing from the theme folder.");
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add("Theme.xml could not be read: " + ex.Message);
            }
        }

        if (problems.Count == 0)
            return null;

        return "This theme may not work for other people:\n\n" + string.Join("\n", problems.Distinct()) + "\n\nExport anyway?";
    }

    private void ImportBackground()
    {
        OpenFileDialog openFileDialog = new OpenFileDialog
        {
            Title = "Select a Background Image",
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (openFileDialog.ShowDialog() != true)
        {
            return;
        }
        string fileName = openFileDialog.FileName;
        if (!File.Exists(fileName))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Paths.Media);
            string[] files = Directory.GetFiles(Paths.Media, "bootstrapper_bg.*");
            foreach (string path in files)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
            string path2 = "bootstrapper_bg" + Path.GetExtension(fileName);
            string text = Path.Combine(Paths.Media, path2);
            File.Copy(fileName, text, overwrite: true);
            BackgroundEvents.RaiseBackgroundChanged(text);
        }
        catch (Exception)
        {
        }
    }

    private async Task DeleteCustomThemeStructure(string name)
    {
        string folder = Path.Combine(Paths.CustomThemes, name);

        if (!Directory.Exists(folder))
        {
            App.Logger.WriteLine("AppearanceViewModel::DeleteCustomTheme", "Already gone from disk: " + folder);
            return;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt < 2)
                {
                    await Task.Delay(250);
                }
            }
        }

        if (!Directory.Exists(folder))
        {
            return;
        }

        try
        {
            DeleteDirectoryTree(folder);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException("A file in the theme folder is in use and could not be deleted. Delete the folder manually: " + folder, ex);
        }
    }

    private void RemoveBackground()
    {
        try
        {
            string[] files = Directory.GetFiles(Paths.Media, "bootstrapper_bg.*");
            foreach (string path in files)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
            BackgroundEvents.RaiseBackgroundChanged(null);
        }
        catch (Exception)
        {
        }
    }

    private async Task RenameCustomThemeStructure(string oldName, string newName)
    {
        string sourceDirName = Path.Combine(Paths.CustomThemes, oldName);
        string destDirName = Path.Combine(Paths.CustomThemes, newName);

        if (!Directory.Exists(sourceDirName))
        {
            throw new FileNotFoundException("That theme folder no longer exists.");
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Move(sourceDirName, destDirName);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt < 2)
                {
                    await Task.Delay(250);
                }
            }
        }

        try
        {
            Directory.CreateDirectory(destDirName);
            CopyDirectoryContents(sourceDirName, destDirName);
            DeleteDirectoryTree(sourceDirName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new IOException("The theme could not be fully renamed because a file in it is in use. If a folder with the new name was created, delete the old folder manually: " + sourceDirName, ex);
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            using FileStream source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using FileStream target = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }

        foreach (string sub in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryContents(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
    }

    private static void DeleteDirectoryTree(string dir)
    {
        foreach (string file in Directory.GetFiles(dir))
        {
            File.Delete(file);
        }

        foreach (string sub in Directory.GetDirectories(dir))
        {
            DeleteDirectoryTree(sub);
        }

        Directory.Delete(dir);
    }

    private void PopulateCustomThemes()
    {
        string selectedCustomTheme = App.Settings.Prop.SelectedCustomTheme;
        Directory.CreateDirectory(Paths.CustomThemes);
        CustomThemes.Clear();
        string[] directories = Directory.GetDirectories(Paths.CustomThemes);
        foreach (string text in directories)
        {
            if (File.Exists(Path.Combine(text, "Theme.xml")))
            {
                string fileName = Path.GetFileName(text);
                CustomThemes.Add(fileName);
            }
        }
        if (selectedCustomTheme != null)
        {
            int num = CustomThemes.IndexOf(selectedCustomTheme);
            if (num != -1)
            {
                SelectedCustomThemeIndex = num;
                OnPropertyChanged("SelectedCustomThemeIndex");
            }
            else
            {
                SelectedCustomTheme = null;
            }
        }
    }
}
