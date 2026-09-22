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

    public sealed class SidebarItemEditor : NotifyPropertyChangedViewModel
    {
        private readonly Action<SidebarItemEditor> _changed;

        private string _name;

        private bool _isVisible;

        public string Key { get; }

        public string DefaultName { get; }

        public string Section { get; }

        public bool CanHide { get; }

        public string Name
        {
            get => _name;
            set
            {
                string normalized = MainWindow.NormalizeSidebarName(value, DefaultName);
                if (_name == normalized)
                {
                    return;
                }
                _name = normalized;
                OnPropertyChanged();
                _changed(this);
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                bool visible = !CanHide || value;
                if (_isVisible == visible)
                {
                    return;
                }
                _isVisible = visible;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisibilityLabel));
                _changed(this);
            }
        }

        public string VisibilityLabel => IsVisible ? "Shown" : "Hidden";

        public SidebarItemEditor(MainWindow.SidebarItemDefinition definition, string name, bool isVisible, Action<SidebarItemEditor> changed)
        {
            Key = definition.Key;
            DefaultName = definition.DefaultName;
            Section = definition.Section;
            CanHide = definition.CanHide;
            _name = MainWindow.NormalizeSidebarName(name, DefaultName);
            _isVisible = !CanHide || isVisible;
            _changed = changed;
        }

    }


    public int[] ZoomOptions { get; } = new int[] { 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200 };

    public ObservableCollection<SidebarItemEditor> SidebarItems { get; } = new ObservableCollection<SidebarItemEditor>();

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

    public ICommand ImportBackgroundCommand { get; }

    public ICommand RemoveBackgroundCommand { get; }

    public ICommand ImportStartupAudioCommand { get; }

    public ICommand RemoveStartupAudioCommand { get; }

    public ICommand ManageAppFontCommand => new RelayCommand(ManageAppFont);

    public ICommand MoveSidebarItemUpCommand { get; }

    public ICommand MoveSidebarItemDownCommand { get; }

    public ICommand ToggleSidebarItemVisibilityCommand { get; }

    public ICommand ResetSidebarCommand { get; }

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

    public System.Windows.Visibility WindowsOnlyVisibility =>
        Voidstrap.Utility.Platform.IsLinux ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

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

    public IEnumerable<Theme> Themes { get; } = Enum.GetValues<Theme>().Cast<Theme>();

    public BackdropType SelectedBackdrop
    {
        get
        {
            if (!Voidstrap.Utility.Platform.IsLinux
                && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                && App.Settings.Prop.WindowBackdrop != BackdropType.None)
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
            App.Settings.SaveDeferred();
            OnPropertyChanged(nameof(Theme));
            if (Application.Current == null)
                return;
            foreach (Voidstrap.UI.Elements.Base.WpfUiWindow window in Application.Current.Windows.OfType<Voidstrap.UI.Elements.Base.WpfUiWindow>().ToArray())
                ThemeTransition.Animate(window, window.ApplyTheme);
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
            OnPropertyChanged(nameof(Dialog));
            OnPropertyChanged(nameof(CustomThemesExpanded));
            OnPropertyChanged(nameof(LauncherExtrasEnabled));
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
                OnPropertyChanged(nameof(Icon));
                return;
            }
            App.Settings.Prop.BootstrapperIcon = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged(nameof(Icon));
            Voidstrap.UI.LinuxApplicationIdentity.Refresh();
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
                OnPropertyChanged(nameof(StudioIcon));
                return;
            }
            App.Settings.Prop.StudioBootstrapperIcon = value;
            OnPropertyChanged(nameof(StudioIcon));
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
            RebuildIcons();
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(CustomIconLocation));
            Voidstrap.UI.LinuxApplicationIdentity.Refresh();
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
            App.Settings.SaveDeferred();
            OnPropertyChanged(nameof(IsCustomThemeSelected));
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
                if (!_appFontHeaders.TryGetValue(key, out byte[]? value) || !value.SequenceEqual(array))
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
        OnPropertyChanged(nameof(ChooseAppFontVisibility));
        OnPropertyChanged(nameof(RemoveAppFontVisibility));
        OnPropertyChanged(nameof(AppFontName));
        OnPropertyChanged(nameof(AppFontFamily));
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
        MoveSidebarItemUpCommand = new RelayCommand<SidebarItemEditor>(item => MoveSidebarItem(item, -1));
        MoveSidebarItemDownCommand = new RelayCommand<SidebarItemEditor>(item => MoveSidebarItem(item, 1));
        ToggleSidebarItemVisibilityCommand = new RelayCommand<SidebarItemEditor>(ToggleSidebarItemVisibility);
        ResetSidebarCommand = new RelayCommand(ResetSidebar);
        _settings = LoadSettings();
        SharedGradientOpacity = _settings.GradientOpacity;
        ImportBackgroundCommand2 = new RelayCommand<object>(ImportFile);
        RemoveBackgroundCommand2 = new RelayCommand<object>(RemoveFile);
        RebuildIcons();
        PopulateCustomThemes();
        LoadSidebarItems();
    }

    private static string GetInitialAutoTranslateLanguageName()
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
        catch (Exception ex)
        {
            App.Logger?.WriteLine("AppearanceViewModel::ApplyToMainWindow", "Could not apply appearance changes: " + ex.Message);
        }
    }

    private void LoadSidebarItems()
    {
        Dictionary<string, string> names = App.Settings.Prop.SidebarNames ??= new Dictionary<string, string>();
        List<string> hidden = App.Settings.Prop.SidebarHiddenItems ??= new List<string>();
        List<string> savedOrder = App.Settings.Prop.SidebarOrder ??= new List<string>();
        Dictionary<string, int> ranks = savedOrder
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Select((key, index) => (key, index))
            .ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
        Dictionary<string, int> defaults = MainWindow.SidebarCustomizationItems
            .Select((item, index) => (item.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);
        SidebarItems.Clear();
        foreach (MainWindow.SidebarItemDefinition definition in MainWindow.SidebarCustomizationItems
            .Where(item => MainWindow.IsSidebarItemAvailable(item.Key))
            .OrderBy(item => SectionRank(item.Section))
            .ThenBy(item => ranks.TryGetValue(item.Key, out int rank) ? rank : int.MaxValue)
            .ThenBy(item => defaults[item.Key]))
        {
            string name = names.TryGetValue(definition.Key, out string? customName) ? customName : definition.DefaultName;
            bool visible = !hidden.Contains(definition.Key, StringComparer.Ordinal);
            SidebarItems.Add(new SidebarItemEditor(definition, name, visible, SaveSidebarItem));
        }
    }

    private static int SectionRank(string section)
    {
        return section switch
        {
            "Core" => 0,
            "Configuration" => 1,
            "More" => 2,
            _ => 3
        };
    }

    private void SaveSidebarItem(SidebarItemEditor item)
    {
        Dictionary<string, string> names = App.Settings.Prop.SidebarNames ??= new Dictionary<string, string>();
        if (item.Name == item.DefaultName)
        {
            names.Remove(item.Key);
        }
        else
        {
            names[item.Key] = item.Name;
        }
        List<string> hidden = App.Settings.Prop.SidebarHiddenItems ??= new List<string>();
        hidden.RemoveAll(key => string.Equals(key, item.Key, StringComparison.Ordinal));
        if (!item.IsVisible && item.CanHide)
        {
            hidden.Add(item.Key);
        }
        SaveAndApplySidebar();
    }

    private void MoveSidebarItem(SidebarItemEditor? item, int direction)
    {
        if (item == null)
        {
            return;
        }
        int index = SidebarItems.IndexOf(item);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= SidebarItems.Count || SidebarItems[target].Section != item.Section)
        {
            return;
        }
        SidebarItems.Move(index, target);
        PersistSidebarOrder();
        SaveAndApplySidebar();
    }

    private static void ToggleSidebarItemVisibility(SidebarItemEditor? item)
    {
        if (item == null || !item.CanHide)
        {
            return;
        }
        item.IsVisible = !item.IsVisible;
    }

    private void PersistSidebarOrder()
    {
        List<string> existing = App.Settings.Prop.SidebarOrder ??= new List<string>();
        Dictionary<string, int> ranks = existing
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Select((key, index) => (key, index))
            .ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
        List<string> merged = new List<string>();
        foreach (string section in new[] { "Core", "Configuration", "More", "Footer" })
        {
            List<MainWindow.SidebarItemDefinition> definitions = MainWindow.SidebarCustomizationItems
                .Where(item => item.Section == section)
                .Select((item, index) => (item, index))
                .OrderBy(pair => ranks.TryGetValue(pair.item.Key, out int rank) ? rank : int.MaxValue)
                .ThenBy(pair => pair.index)
                .Select(pair => pair.item)
                .ToList();
            Queue<string> available = new Queue<string>(SidebarItems.Where(item => item.Section == section).Select(item => item.Key));
            foreach (MainWindow.SidebarItemDefinition definition in definitions)
            {
                merged.Add(MainWindow.IsSidebarItemAvailable(definition.Key) && available.Count > 0 ? available.Dequeue() : definition.Key);
            }
        }
        App.Settings.Prop.SidebarOrder = merged;
    }

    private void ResetSidebar()
    {
        App.Settings.Prop.SidebarNames = new Dictionary<string, string>();
        App.Settings.Prop.SidebarOrder = new List<string>();
        App.Settings.Prop.SidebarHiddenItems = new List<string>();
        App.Settings.SaveDeferred();
        LoadSidebarItems();
        ApplyToMainWindow(window => window.ApplySidebarCustomization());
    }

    private static void SaveAndApplySidebar()
    {
        App.Settings.SaveDeferred();
        ApplyToMainWindow(window => window.ApplySidebarCustomization());
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

    internal static byte[]? ReadBgraPixels(BitmapSource image)
    {
        int width = image.PixelWidth;
        int height = image.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        PixelFormat format = image.Format;
        byte[] bgra = new byte[width * height * 4];

        if (format == PixelFormats.Bgra32 || format == PixelFormats.Bgr32)
        {
            image.CopyPixels(bgra, width * 4, 0);
            return bgra;
        }

        if (format == PixelFormats.Pbgra32)
        {
            image.CopyPixels(bgra, width * 4, 0);
            for (int i = 0; i < bgra.Length; i += 4)
            {
                byte alpha = bgra[i + 3];
                if (alpha == 0 || alpha == 255)
                {
                    continue;
                }
                bgra[i] = (byte)Math.Min(255, bgra[i] * 255 / alpha);
                bgra[i + 1] = (byte)Math.Min(255, bgra[i + 1] * 255 / alpha);
                bgra[i + 2] = (byte)Math.Min(255, bgra[i + 2] * 255 / alpha);
            }
            return bgra;
        }

        if (format == PixelFormats.Bgr24 || format == PixelFormats.Rgb24)
        {
            int stride = width * 3;
            byte[] source = new byte[stride * height];
            image.CopyPixels(source, stride, 0);
            bool swap = format == PixelFormats.Rgb24;
            for (int p = 0, q = 0; p < source.Length; p += 3, q += 4)
            {
                bgra[q] = swap ? source[p + 2] : source[p];
                bgra[q + 1] = source[p + 1];
                bgra[q + 2] = swap ? source[p] : source[p + 2];
                bgra[q + 3] = 255;
            }
            return bgra;
        }

        if (format == PixelFormats.Gray8 || format == PixelFormats.Indexed8)
        {
            byte[] source = new byte[width * height];
            image.CopyPixels(source, width, 0);
            for (int p = 0, q = 0; p < source.Length; p++, q += 4)
            {
                bgra[q] = source[p];
                bgra[q + 1] = source[p];
                bgra[q + 2] = source[p];
                bgra[q + 3] = 255;
            }
            return bgra;
        }

        try
        {
            FormatConvertedBitmap converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0.0);
            byte[] fallback = new byte[converted.PixelWidth * 4 * converted.PixelHeight];
            converted.CopyPixels(fallback, converted.PixelWidth * 4, 0);
            return fallback;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("AppearanceViewModel::ReadBgraPixels", "Could not convert " + format + ": " + ex.Message.Split('\n')[0]);
            return null;
        }
    }

    internal static double? RecommendGradientOpacity(string path)
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
            byte[]? pixels = ReadBgraPixels(image);
            if (pixels == null || pixels.Length == 0)
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

    internal static BackgroundSettings LoadSettings()
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

    private void RebuildIcons()
    {
        Icons.Clear();
        foreach (BootstrapperIcon selection in BootstrapperIconEx.Selections)
        {
            Icons.Add(new BootstrapperIconEntry
            {
                IconType = selection
            });
        }
        OnPropertyChanged(nameof(Icons));
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
        OnPropertyChanged(nameof(CustomIconLocation));
        return true;
    }

    private void AddCustomTheme()
    {
        AddCustomThemeDialog addCustomThemeDialog = new AddCustomThemeDialog();
        addCustomThemeDialog.ShowOwnedDialog();
        if (addCustomThemeDialog.Created)
        {
            CustomThemes.Add(addCustomThemeDialog.ThemeName);
            SelectedCustomThemeIndex = CustomThemes.Count - 1;
            OnPropertyChanged(nameof(SelectedCustomThemeIndex));
            OnPropertyChanged(nameof(IsCustomThemeSelected));
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
        App.Logger.WriteLine("AppearanceViewModel::DeleteCustomTheme", "Delete requested for " + (name ?? "nothing selected"));
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
                OnPropertyChanged(nameof(SelectedCustomThemeIndex));
            }
            SelectedCustomTheme = null;
            App.Settings.Save();
            App.Logger.WriteLine("AppearanceViewModel::DeleteCustomTheme", "Deleted " + name);
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

        if (Voidstrap.Utility.Platform.IsWindows && (newName.EndsWith(' ') || newName.EndsWith('.')))
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
        OnPropertyChanged(nameof(SelectedCustomTheme));
        OnPropertyChanged(nameof(SelectedCustomThemeName));
        OnPropertyChanged(nameof(SelectedCustomThemeIndex));
    }

    private void EditCustomTheme()
    {
        if (SelectedCustomTheme != null)
        {
            new BootstrapperEditorWindow(SelectedCustomTheme).ShowOwnedDialog();
        }
    }


    private async Task ViewCustomThemeFilesAsync()
    {
        if (SelectedCustomTheme == null)
            return;

        try
        {
            var files = await Voidstrap.Integrations.ThemeFiles
                .LoadAsync(SelectedCustomTheme)
                .ConfigureAwait(true);

            if (files.Count == 0)
            {
                Frontend.ShowMessageBox("That theme has no files yet.", MessageBoxImage.Asterisk);
                return;
            }

            ThemeChangesDialog dialog = new ThemeChangesDialog(SelectedCustomTheme, files)
            {
                Owner = Application.Current.MainWindow
            };

            dialog.ShowOwnedDialog();
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Hand);
        }
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
            if (Voidstrap.Utility.Platform.IsLinux)
            {
                Voidstrap.Utility.PlatformShell.TryRevealFile(saveFileDialog.FileName);
            }
            else
            {
                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + saveFileDialog.FileName + "\"",
                    UseShellExecute = true
                });
            }
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

    private static async Task DeleteCustomThemeStructure(string name)
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

    private static async Task RenameCustomThemeStructure(string oldName, string newName)
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
        string? selectedCustomTheme = App.Settings.Prop.SelectedCustomTheme;
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
                OnPropertyChanged(nameof(SelectedCustomThemeIndex));
            }
            else
            {
                SelectedCustomTheme = null;
            }
        }
    }
}
