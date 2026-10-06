using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Voidstrap.UI.Elements.ContextMenu;
using Voidstrap.UI.Elements.Dialogs;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Controls;

public partial class CustomThemePanel : UserControl
{
    private const string LOG_IDENT = "CustomThemePanel";

    private const string UnsavedProfile = "Unsaved theme";

    private static readonly string[] BuiltInThemes = ["Dark", "UltraGray", "Light", "Voidstrap", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple", "Pink", "Berry"];

    private readonly ObservableCollection<ThemeColorItem> _items = new();

    private readonly ObservableCollection<GradientStopItem> _stops = new();

    private readonly ObservableCollection<string> _profiles = new();

    private readonly DispatcherTimer _applyTimer;

    private TextEditor? _codeEditor;

    private FileSystemWatcher? _watcher;

    private string _lastWritten = string.Empty;

    private string? _activeProfile;

    private bool _ready;

    private bool _suppress;

    private bool _suppressProfileChange;

    private bool _dirty;

    private bool _codePending;

    public CustomThemePanel()
    {
        _applyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };

        InitializeComponent();

        foreach (ThemeKeyInfo info in CustomTheme.Schema)
        {
            Color color = CustomTheme.TryParseColor(info.Fallback, out Color fallback) ? fallback : Colors.Black;
            ThemeColorItem item = new ThemeColorItem(info.Key, info.Label, info.IsBrush, color, info.Group, info.Optional, isSet: false);
            item.Changed += ScheduleApply;
            _items.Add(item);
        }

        CollectionViewSource main = new CollectionViewSource { Source = _items };
        main.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ThemeColorItem.Group)));
        main.Filter += MainFilter;
        ColorList.ItemsSource = main.View;

        CollectionViewSource advanced = new CollectionViewSource { Source = _items };
        advanced.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ThemeColorItem.Group)));
        advanced.Filter += AdvancedFilter;
        AdvancedList.ItemsSource = advanced.View;

        StopList.ItemsSource = _stops;
        GradientTypeBox.SelectedIndex = 0;
        AngleSlider.Value = 45;
        RadiusSlider.Value = 75;
        CenterXSlider.Value = 50;
        CenterYSlider.Value = 50;
        ProfileBox.ItemsSource = _profiles;
    }

    private static void MainFilter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is ThemeColorItem { Optional: false };
    }

    private static void AdvancedFilter(object sender, FilterEventArgs e)
    {
        e.Accepted = e.Item is ThemeColorItem { Optional: true };
    }

    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        _applyTimer.Tick -= ApplyTimer_Tick;
        _applyTimer.Tick += ApplyTimer_Tick;
        if (!_ready)
        {
            RefreshProfiles(null);
            LoadFromDisk();
            _ready = true;
        }
        if (_codeEditor != null)
            StartWatching();
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_applyTimer.IsEnabled)
            ApplyTimer_Tick(this, EventArgs.Empty);
        _applyTimer.Stop();
        _applyTimer.Tick -= ApplyTimer_Tick;
        StopWatching();
    }

    private void Panel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_ready && IsVisible && !_applyTimer.IsEnabled)
            LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        string xaml;
        try
        {
            xaml = File.Exists(Paths.CustomThemeXaml) ? CustomTheme.ReadFile(Paths.CustomThemeXaml) : string.Empty;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not read the custom theme: " + ex.Message);
            xaml = string.Empty;
        }
        ThemeValidationResult result = string.IsNullOrEmpty(xaml) ? new ThemeValidationResult { Dictionary = null } : CustomTheme.Validate(xaml);
        if (string.IsNullOrEmpty(xaml) || !result.Ok)
            xaml = CustomTheme.BuildXaml(DefaultModel());
        ThemeValidationResult loaded = CustomTheme.Validate(xaml);
        LoadModelIntoUi(CustomTheme.ReadModel(loaded.Dictionary));
        _lastWritten = xaml;
        SetCodeText(xaml);
    }

    private static ThemeModel DefaultModel()
    {
        ThemeModel model = new ThemeModel();
        foreach (ThemeKeyInfo info in CustomTheme.Schema)
        {
            if (!info.Optional && CustomTheme.TryParseColor(info.Fallback, out Color c))
                model.Colors[info.Key] = c;
        }
        return model;
    }

    private ThemeModel CurrentModel()
    {
        ThemeModel model = new ThemeModel();
        foreach (ThemeColorItem item in _items)
        {
            if (item.IsSet)
                model.Colors[item.Key] = item.Color;
        }
        if (GradientToggle.IsChecked == true && _stops.Count >= 2)
            model.Gradient = CurrentGradient();
        return model;
    }

    private ThemeGradient CurrentGradient()
    {
        ThemeGradient gradient = new ThemeGradient
        {
            Radial = GradientTypeBox.SelectedIndex == 1,
            Angle = Math.Round(AngleSlider.Value),
            Radius = RadiusSlider.Value / 100.0,
            CenterX = CenterXSlider.Value / 100.0,
            CenterY = CenterYSlider.Value / 100.0
        };
        foreach (GradientStopItem stop in _stops)
            gradient.Stops.Add(new ThemeGradientStop { Color = stop.Color, Offset = stop.Position / 100.0 });
        return gradient;
    }

    private void LoadModelIntoUi(ThemeModel model)
    {
        bool previous = _suppress;
        _suppress = true;
        try
        {
            foreach (ThemeColorItem item in _items)
            {
                ThemeKeyInfo info = CustomTheme.Schema.First(s => s.Key == item.Key);
                if (model.Colors.TryGetValue(item.Key, out Color color))
                    item.Load(color, isSet: true);
                else
                    item.Load(CustomTheme.TryParseColor(info.Fallback, out Color fallback) ? fallback : Colors.Black, isSet: false);
            }

            ClearStops();
            if (model.Gradient is { Stops.Count: >= 2 } gradient)
            {
                GradientTypeBox.SelectedIndex = gradient.Radial ? 1 : 0;
                AngleSlider.Value = gradient.Angle;
                RadiusSlider.Value = Math.Clamp(gradient.Radius * 100.0, RadiusSlider.Minimum, RadiusSlider.Maximum);
                CenterXSlider.Value = gradient.CenterX * 100.0;
                CenterYSlider.Value = gradient.CenterY * 100.0;
                foreach (ThemeGradientStop stop in gradient.Stops.OrderBy(s => s.Offset))
                    AddStopItem(stop.Color, stop.Offset * 100.0);
                GradientToggle.IsChecked = true;
            }
            else
            {
                GradientToggle.IsChecked = false;
            }
            UpdateGradientPanel();
        }
        finally
        {
            _suppress = previous;
        }
    }

    private void ScheduleApply()
    {
        if (_suppress || !_ready)
            return;
        _dirty = true;
        _codePending = false;
        _applyTimer.Stop();
        _applyTimer.Interval = TimeSpan.FromMilliseconds(Voidstrap.Utility.Platform.IsLinux ? 350 : 200);
        _applyTimer.Start();
    }

    private void ApplyTimer_Tick(object? sender, EventArgs e)
    {
        _applyTimer.Stop();
        if (_codePending && _codeEditor != null)
        {
            _codePending = false;
            ApplyXaml(_codeEditor.Text, reloadUi: true, fromCode: true);
            return;
        }
        SyncWindowColorsFromGradient();
        string xaml = CustomTheme.BuildXaml(CurrentModel());
        ApplyXaml(xaml, reloadUi: false, fromCode: false);
    }

    private bool ApplyXaml(string xaml, bool reloadUi, bool fromCode)
    {
        ThemeValidationResult result = CustomTheme.Validate(xaml);
        if (!result.Ok)
        {
            string first = result.Errors.FirstOrDefault() ?? "That theme is not valid.";
            ShowStatus(result.ErrorLine > 0 ? $"Line {result.ErrorLine}: {first}" : first, isError: true);
            return false;
        }
        ClearStatus();
        if (reloadUi)
            LoadModelIntoUi(CustomTheme.ReadModel(result.Dictionary));
        if (!fromCode)
            SetCodeText(xaml);
        if (string.Equals(xaml, _lastWritten, StringComparison.Ordinal))
            return true;
        try
        {
            CustomTheme.WriteFile(Paths.CustomThemeXaml, xaml);
            _lastWritten = xaml;
        }
        catch (Exception ex)
        {
            ShowStatus("Could not save the theme: " + ex.Message, isError: true);
            return false;
        }
        Voidstrap.UI.Elements.Base.WpfUiWindow.InvalidateCustomTheme();
        WindowBackdrop.ApplyThemeToAllOpenWindows();
        return true;
    }

    private void SetCodeText(string xaml)
    {
        if (_codeEditor == null || _codeEditor.Text == xaml)
            return;
        bool previous = _suppress;
        _suppress = true;
        _codeEditor.Text = xaml;
        _suppress = previous;
    }

    private void SyncWindowColorsFromGradient()
    {
        if (GradientToggle.IsChecked != true || _stops.Count < 2)
            return;
        List<GradientStopItem> ordered = _stops.OrderBy(s => s.Position).ToList();
        SetWindowColorQuietly("WindowBackgroundColorPrimary", ordered[0].Color);
        SetWindowColorQuietly("WindowBackgroundColorSecondary", ordered[ordered.Count / 2].Color);
        SetWindowColorQuietly("WindowBackgroundColorThird", ordered[^1].Color);
    }

    private void SetWindowColorQuietly(string key, Color color)
    {
        ThemeColorItem? item = _items.FirstOrDefault(i => i.Key == key);
        if (item == null || (item.Color == color && item.IsSet))
            return;
        bool previous = _suppress;
        _suppress = true;
        item.Load(color, isSet: true);
        _suppress = previous;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusIcon.Symbol = isError ? Wpf.Ui.Common.SymbolRegular.ErrorCircle24 : Wpf.Ui.Common.SymbolRegular.CheckmarkCircle24;
        StatusBorder.Background = new SolidColorBrush(isError ? Color.FromArgb(0x33, 0xFF, 0x3B, 0x3B) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        StatusBorder.BorderBrush = new SolidColorBrush(isError ? Color.FromArgb(0x80, 0xFF, 0x3B, 0x3B) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        StatusText.Foreground = new SolidColorBrush(isError ? Color.FromRgb(0xFF, 0xC9, 0xC9) : Color.FromRgb(0xDD, 0xDD, 0xDD));
        StatusBorder.Visibility = Visibility.Visible;
    }

    private void ClearStatus()
    {
        StatusBorder.Visibility = Visibility.Collapsed;
    }

    private void LoadXaml(string xaml, string message)
    {
        if (ApplyXaml(xaml, reloadUi: true, fromCode: false))
            ShowStatus(message, isError: false);
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private void GradientToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppress)
            return;
        if (GradientToggle.IsChecked == true && _stops.Count < 2)
            SeedStopsFromWindowColors();
        UpdateGradientPanel();
        ScheduleApply();
    }

    private void GradientTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppress)
            return;
        UpdateGradientPanel();
        ScheduleApply();
    }

    private void GradientSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready)
            return;
        UpdateGradientLabels();
        UpdateGradientBar();
        ScheduleApply();
    }

    private void UpdateGradientPanel()
    {
        if (GradientPanel == null || AnglePanel == null || RadialPanel == null || AddStopButton == null)
            return;
        GradientPanel.Visibility = GradientToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (GradientOffHint != null)
            GradientOffHint.Visibility = GradientToggle.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        bool radial = GradientTypeBox.SelectedIndex == 1;
        AnglePanel.Visibility = radial ? Visibility.Collapsed : Visibility.Visible;
        RadialPanel.Visibility = radial ? Visibility.Visible : Visibility.Collapsed;
        AddStopButton.IsEnabled = _stops.Count < ThemeGradient.MaximumStops;
        UpdateGradientLabels();
        UpdateGradientBar();
    }

    private void UpdateGradientLabels()
    {
        if (AngleText == null || RadiusText == null || CenterXText == null || CenterYText == null)
            return;
        AngleText.Text = Math.Round(AngleSlider.Value) + "°";
        RadiusText.Text = Math.Round(RadiusSlider.Value) + "%";
        CenterXText.Text = Math.Round(CenterXSlider.Value) + "%";
        CenterYText.Text = Math.Round(CenterYSlider.Value) + "%";
    }

    private void UpdateGradientBar()
    {
        if (GradientBar == null || GradientTypeBox == null || RadiusSlider == null || CenterXSlider == null || CenterYSlider == null)
            return;
        try
        {
            if (_stops.Count < 2)
            {
                GradientBar.Background = Brushes.Transparent;
                return;
            }
            ThemeGradient gradient = CurrentGradient();
            foreach (ThemeGradientStop stop in gradient.Stops)
                stop.Color = Color.FromRgb(stop.Color.R, stop.Color.G, stop.Color.B);
            GradientBrush brush = gradient.ToBrush();
            brush.Freeze();
            GradientBar.Background = brush;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The gradient preview could not be drawn: " + ex.Message);
        }
    }

    private void AddStopItem(Color color, double position)
    {
        GradientStopItem item = new GradientStopItem(color, position);
        item.Changed += StopChanged;
        _stops.Add(item);
    }

    private void ClearStops()
    {
        foreach (GradientStopItem stop in _stops)
        {
            stop.Changed -= StopChanged;
            stop.Detach();
        }
        _stops.Clear();
    }

    private void StopChanged()
    {
        UpdateGradientBar();
        ScheduleApply();
    }

    private void SeedStopsFromWindowColors()
    {
        Color Read(string key) => _items.FirstOrDefault(i => i.Key == key)?.Color ?? Color.FromRgb(0x20, 0x20, 0x20);
        ClearStops();
        AddStopItem(Read("WindowBackgroundColorPrimary"), 0);
        AddStopItem(Read("WindowBackgroundColorSecondary"), 50);
        AddStopItem(Read("WindowBackgroundColorThird"), 100);
    }

    private void AddStop_Click(object sender, RoutedEventArgs e)
    {
        if (_stops.Count >= ThemeGradient.MaximumStops)
            return;
        List<GradientStopItem> ordered = _stops.OrderBy(s => s.Position).ToList();
        double gapStart = 0;
        double gapEnd = 100;
        Color left = Colors.Black;
        Color right = Colors.White;
        double widest = -1;
        for (int index = 0; index + 1 < ordered.Count; index++)
        {
            double width = ordered[index + 1].Position - ordered[index].Position;
            if (width > widest)
            {
                widest = width;
                gapStart = ordered[index].Position;
                gapEnd = ordered[index + 1].Position;
                left = ordered[index].Color;
                right = ordered[index + 1].Color;
            }
        }
        Color middle = Color.FromArgb((byte)((left.A + right.A) / 2), (byte)((left.R + right.R) / 2), (byte)((left.G + right.G) / 2), (byte)((left.B + right.B) / 2));
        AddStopItem(middle, (gapStart + gapEnd) / 2.0);
        SortStops();
        UpdateGradientPanel();
        ScheduleApply();
    }

    private void RemoveStop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GradientStopItem stop })
            return;
        if (_stops.Count <= 2)
        {
            ShowStatus("A gradient needs at least two colours. Turn the gradient off to use the window colours instead.", isError: false);
            return;
        }
        stop.Changed -= StopChanged;
        stop.Detach();
        _stops.Remove(stop);
        UpdateGradientPanel();
        ScheduleApply();
    }

    private void ReverseStops_Click(object sender, RoutedEventArgs e)
    {
        bool previous = _suppress;
        _suppress = true;
        foreach (GradientStopItem stop in _stops.ToList())
            stop.Position = 100 - stop.Position;
        _suppress = previous;
        SortStops();
        UpdateGradientBar();
        ScheduleApply();
    }

    private void EvenStops_Click(object sender, RoutedEventArgs e)
    {
        bool previous = _suppress;
        _suppress = true;
        List<GradientStopItem> ordered = _stops.OrderBy(s => s.Position).ToList();
        for (int index = 0; index < ordered.Count; index++)
            ordered[index].Position = ordered.Count == 1 ? 0 : index * 100.0 / (ordered.Count - 1);
        _suppress = previous;
        SortStops();
        UpdateGradientBar();
        ScheduleApply();
    }

    private void SeedStops_Click(object sender, RoutedEventArgs e)
    {
        SeedStopsFromWindowColors();
        UpdateGradientPanel();
        ScheduleApply();
    }

    private void SortStops()
    {
        List<GradientStopItem> ordered = _stops.OrderBy(s => s.Position).ToList();
        for (int index = 0; index < ordered.Count; index++)
        {
            int current = _stops.IndexOf(ordered[index]);
            if (current != index)
                _stops.Move(current, index);
        }
    }

    private void StopSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GradientStopItem stop })
            return;
        if (TryPickColor(stop.Color, out Color picked))
            stop.SetColor(picked);
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ThemeColorItem item })
            return;
        if (TryPickColor(item.Color, out Color picked))
            item.SetColor(picked);
    }

    private void ResetColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThemeColorItem item })
            item.Clear();
    }

    private bool TryPickColor(Color initial, out Color picked)
    {
        picked = initial;
        try
        {
            RinColorPickerDialog dialog = new(initial, alphaEnabled: true) { Owner = OwnerWindow };
            if (dialog.ShowOwnedDialog() != true)
                return false;
            picked = dialog.SelectedColor;
            return true;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Colour picker failed: " + ex.Message);
            return false;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Frontend.ShowMessageBox("Reset the custom theme to the default colours?", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        LoadXaml(CustomTheme.BuildXaml(DefaultModel()), "Reset to the default colours");
        _dirty = true;
    }

    private void RefreshProfiles(string? select)
    {
        _suppressProfileChange = true;
        try
        {
            _profiles.Clear();
            _profiles.Add(UnsavedProfile);
            foreach (string profile in CustomTheme.ListProfiles())
                _profiles.Add(profile);
            _activeProfile = select != null && _profiles.Contains(select) ? select : null;
            ProfileBox.SelectedItem = _activeProfile ?? UnsavedProfile;
        }
        finally
        {
            _suppressProfileChange = false;
        }
    }

    private bool ConfirmDiscard()
    {
        return !_dirty || Frontend.ShowMessageBox("Your current colours are not saved to a profile. Replace them anyway?", MessageBoxImage.Question, MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _suppressProfileChange || ProfileBox.SelectedItem is not string name)
            return;
        if (name == UnsavedProfile)
        {
            _activeProfile = null;
            return;
        }
        if (!ConfirmDiscard())
        {
            RefreshProfiles(_activeProfile);
            return;
        }
        try
        {
            LoadXaml(CustomTheme.ReadProfile(name), "Applied the " + name + " profile");
            _activeProfile = name;
            _dirty = false;
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open that profile: " + ex.Message, isError: true);
            RefreshProfiles(_activeProfile);
        }
    }

    private string? AskProfileName(string prompt, string initial)
    {
        TextInputDialog dialog = new TextInputDialog(prompt, initial) { Owner = OwnerWindow, Title = "Theme profile" };
        dialog.ShowOwnedDialog();
        if (!dialog.Confirmed)
            return null;
        string name = (dialog.Value ?? string.Empty).Trim();
        if (!CustomTheme.IsValidProfileName(name))
        {
            ShowStatus("Profile names can use letters, numbers, spaces, brackets, dashes and underscores, up to 40 characters.", isError: true);
            return null;
        }
        return name;
    }

    private string CurrentXaml()
    {
        return _lastWritten.Length > 0 ? _lastWritten : CustomTheme.BuildXaml(CurrentModel());
    }

    private void FlushPending()
    {
        if (_applyTimer.IsEnabled)
            ApplyTimer_Tick(this, EventArgs.Empty);
    }

    private void SaveProfileAs(string name, string xaml)
    {
        if (CustomTheme.ListProfiles().Contains(name, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(name, _activeProfile, StringComparison.OrdinalIgnoreCase)
            && Frontend.ShowMessageBox("A profile called " + name + " already exists. Replace it?", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        CustomTheme.SaveProfile(name, xaml);
        RefreshProfiles(name);
        _dirty = false;
        ShowStatus("Saved the " + name + " profile", isError: false);
    }

    private void ProfileSave_Click(object sender, RoutedEventArgs e)
    {
        FlushPending();
        if (_activeProfile == null)
        {
            ProfileSaveAs_Click(sender, e);
            return;
        }
        try
        {
            SaveProfileAs(_activeProfile, CurrentXaml());
        }
        catch (Exception ex)
        {
            ShowStatus("Could not save the profile: " + ex.Message, isError: true);
        }
    }

    private void ProfileSaveAs_Click(object sender, RoutedEventArgs e)
    {
        FlushPending();
        string? name = AskProfileName("Name this theme profile", _activeProfile ?? "My theme");
        if (name == null)
            return;
        try
        {
            SaveProfileAs(name, CurrentXaml());
        }
        catch (Exception ex)
        {
            ShowStatus("Could not save the profile: " + ex.Message, isError: true);
        }
    }

    private void ProfileRename_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfile == null)
        {
            ShowStatus("Pick a saved profile to rename it.", isError: false);
            return;
        }
        string? name = AskProfileName("Rename the profile", _activeProfile);
        if (name == null || string.Equals(name, _activeProfile, StringComparison.Ordinal))
            return;
        try
        {
            CustomTheme.RenameProfile(_activeProfile, name);
            RefreshProfiles(name);
            ShowStatus("Renamed the profile to " + name, isError: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not rename the profile: " + ex.Message, isError: true);
        }
    }

    private void ProfileDuplicate_Click(object sender, RoutedEventArgs e)
    {
        FlushPending();
        string baseName = _activeProfile ?? "My theme";
        IReadOnlyList<string> existing = CustomTheme.ListProfiles();
        string candidate = baseName + " (copy)";
        for (int number = 2; existing.Contains(candidate, StringComparer.OrdinalIgnoreCase) && number < 100; number++)
            candidate = baseName + " (copy " + number + ")";
        if (!CustomTheme.IsValidProfileName(candidate))
            candidate = "Theme copy";
        try
        {
            CustomTheme.SaveProfile(candidate, CurrentXaml());
            RefreshProfiles(candidate);
            _dirty = false;
            ShowStatus("Duplicated as " + candidate, isError: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not duplicate the profile: " + ex.Message, isError: true);
        }
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfile == null)
        {
            ShowStatus("Pick a saved profile to delete it.", isError: false);
            return;
        }
        if (Frontend.ShowMessageBox("Delete the " + _activeProfile + " profile? This cannot be undone.", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        try
        {
            string removed = _activeProfile;
            CustomTheme.DeleteProfile(removed);
            RefreshProfiles(null);
            ShowStatus("Deleted the " + removed + " profile", isError: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not delete the profile: " + ex.Message, isError: true);
        }
    }

    private void ProfileImport_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
            return;
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Theme files|*.xaml",
            Title = "Import a theme"
        };
        if (dialog.ShowDialog(OwnerWindow) != true)
            return;
        try
        {
            string xaml = CustomTheme.ReadFile(dialog.FileName);
            ThemeValidationResult result = CustomTheme.Validate(xaml);
            if (!result.Ok)
            {
                ShowStatus("That file is not a valid theme: " + (result.Errors.FirstOrDefault() ?? "unknown problem"), isError: true);
                return;
            }
            string suggested = Path.GetFileNameWithoutExtension(dialog.FileName);
            string? name = AskProfileName("Name the imported profile", CustomTheme.IsValidProfileName(suggested) ? suggested : "Imported theme");
            if (name == null)
                return;
            SaveProfileAs(name, xaml);
            LoadXaml(xaml, "Imported and applied " + name);
            _activeProfile = name;
            _dirty = false;
        }
        catch (Exception ex)
        {
            ShowStatus("Could not import that theme: " + ex.Message, isError: true);
        }
    }

    private void ProfileExport_Click(object sender, RoutedEventArgs e)
    {
        FlushPending();
        Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Theme files|*.xaml",
            FileName = (_activeProfile ?? "Voidstrap theme") + ".xaml",
            Title = "Export the theme"
        };
        if (dialog.ShowDialog(OwnerWindow) != true)
            return;
        try
        {
            CustomTheme.WriteFile(dialog.FileName, CurrentXaml());
            ShowStatus("Exported to " + dialog.FileName, isError: false);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not export the theme: " + ex.Message, isError: true);
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Controls.ContextMenu menu = new System.Windows.Controls.ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(CreateMenuItem("Save as a new profile", ProfileSaveAs_Click));
        menu.Items.Add(CreateMenuItem("Rename profile", ProfileRename_Click, _activeProfile != null));
        menu.Items.Add(CreateMenuItem("Duplicate profile", ProfileDuplicate_Click));
        menu.Items.Add(CreateMenuItem("Delete profile", ProfileDelete_Click, _activeProfile != null));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Import a theme file", ProfileImport_Click));
        menu.Items.Add(CreateMenuItem("Export to a file", ProfileExport_Click));
        menu.Items.Add(new Separator());
        MenuItem startFrom = new MenuItem { Header = "Start from a built in theme" };
        foreach (string theme in BuiltInThemes)
        {
            MenuItem item = new MenuItem { Header = theme, Tag = theme };
            item.Click += StartFromMenu_Click;
            startFrom.Items.Add(item);
        }
        menu.Items.Add(startFrom);
        menu.Items.Add(CreateMenuItem("Reset to the default colours", Reset_Click));
        menu.IsOpen = true;
    }

    private static MenuItem CreateMenuItem(string header, RoutedEventHandler handler, bool enabled = true)
    {
        MenuItem item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += handler;
        return item;
    }

    private void StartFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string name } || !_ready)
            return;
        if (!ConfirmDiscard())
            return;
        string? xaml = CustomTheme.BuiltInXaml(name);
        if (xaml == null)
        {
            ShowStatus("Could not load the " + name + " theme.", isError: true);
            return;
        }
        LoadXaml(xaml, "Started from the " + name + " theme");
        RefreshProfiles(null);
        _dirty = true;
    }

    private void CodeExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (_codeEditor != null)
            return;
        try
        {
            TextEditor editor = new TextEditor
            {
                Padding = new Thickness(8, 6, 8, 6),
                ShowLineNumbers = true,
                WordWrap = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            CodeEditorBehavior.SetLanguage(editor, ".xaml");
            editor.Text = _lastWritten;
            editor.TextChanged += CodeEditor_TextChanged;
            CodeHost.Child = editor;
            _codeEditor = editor;
            StartWatching();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The code editor could not be created: " + ex.Message);
        }
    }

    private void CodeEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_suppress || !_ready)
            return;
        _dirty = true;
        _codePending = true;
        _applyTimer.Stop();
        _applyTimer.Interval = TimeSpan.FromMilliseconds(500);
        _applyTimer.Start();
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        if (_codeEditor == null)
            return;
        ThemeValidationResult result = CustomTheme.Validate(_codeEditor.Text);
        if (!result.Ok)
        {
            ShowStatus(result.Errors.FirstOrDefault() ?? "Cannot format an invalid theme", isError: true);
            return;
        }
        string formatted = CustomTheme.BuildXaml(CustomTheme.ReadModel(result.Dictionary));
        SetCodeText(formatted);
        ApplyXaml(formatted, reloadUi: true, fromCode: true);
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (_codeEditor != null && TryPickColor(Colors.White, out Color picked))
            _codeEditor.Document.Insert(_codeEditor.CaretOffset, CustomTheme.ToHex(picked));
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClipboardService.SetText(_codeEditor?.Text ?? CurrentXaml());
            ShowStatus("Copied to the clipboard", isError: false);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Copy failed: " + ex.Message);
        }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        FlushPending();
        IReadOnlyList<ExternalEditorInfo> editors = ExternalEditor.Detect();
        if (editors.Count == 0)
        {
            ShowStatus("No other code editor was found on this computer.", isError: false);
            return;
        }
        ExternalEditorPickerDialog dialog = new(editors) { Owner = OwnerWindow };
        if (dialog.ShowOwnedDialog() != true || dialog.SelectedEditor == null)
            return;
        try
        {
            if (!File.Exists(Paths.CustomThemeXaml))
                CustomTheme.WriteFile(Paths.CustomThemeXaml, CurrentXaml());
        }
        catch (Exception ex)
        {
            ShowStatus("Could not write the theme file: " + ex.Message, isError: true);
            return;
        }
        if (!ExternalEditor.Open(dialog.SelectedEditor, Paths.CustomThemeXaml))
        {
            ShowStatus("Could not start " + dialog.SelectedEditor.Name, isError: true);
            return;
        }
        StartWatching();
        ShowStatus("Editing in " + dialog.SelectedEditor.Name + ", saving there updates Voidstrap right away", isError: false);
    }

    private void StartWatching()
    {
        if (_watcher != null)
            return;
        try
        {
            string? directory = Path.GetDirectoryName(Paths.CustomThemeXaml);
            if (string.IsNullOrEmpty(directory))
                return;
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(Paths.CustomThemeXaml))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += Watcher_Changed;
            _watcher.Renamed += Watcher_Changed;
            _watcher.Created += Watcher_Changed;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not watch the theme file: " + ex.Message);
        }
    }

    private void StopWatching()
    {
        if (_watcher == null)
            return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= Watcher_Changed;
        _watcher.Renamed -= Watcher_Changed;
        _watcher.Created -= Watcher_Changed;
        _watcher.Dispose();
        _watcher = null;
    }

    private void Watcher_Changed(object sender, FileSystemEventArgs e)
    {
        try
        {
            Dispatcher.BeginInvoke(ReloadFromDisk, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Theme file change could not be handled: " + ex.Message);
        }
    }

    private void ReloadFromDisk()
    {
        if (!IsLoaded)
            return;
        string text;
        try
        {
            text = CustomTheme.ReadFile(Paths.CustomThemeXaml);
        }
        catch
        {
            return;
        }
        if (string.Equals(text, _lastWritten, StringComparison.Ordinal))
            return;
        ThemeValidationResult result = CustomTheme.Validate(text);
        if (!result.Ok)
        {
            ShowStatus("The theme file has a problem: " + (result.Errors.FirstOrDefault() ?? "unknown"), isError: true);
            return;
        }
        _lastWritten = text;
        LoadModelIntoUi(CustomTheme.ReadModel(result.Dictionary));
        SetCodeText(text);
        Voidstrap.UI.Elements.Base.WpfUiWindow.InvalidateCustomTheme();
        WindowBackdrop.ApplyThemeToAllOpenWindows();
    }
}
