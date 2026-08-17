using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Xml;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Nvidia;
using Voidstrap.Models;
using Voidstrap.UI.Elements.Dialogs;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class NvidiaFFlagEditorPage : UiPage
{
    private sealed class NvidiaHistoryEntry
    {
        public string Message { get; init; } = string.Empty;
        public List<NvidiaEditorEntry> Snapshot { get; init; } = [];
        public DateTime Timestamp { get; init; }

        public override string ToString()
        {
            return Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + Message;
        }
    }

    private const int MaxHistoryEntries = 100;
    private const int SearchDebounceMs = 180;
    private static readonly string NipDirectory = Paths.NipProfiles;
    private static readonly string NipPath = Path.Combine(NipDirectory, "Voidstrap.nip");

    private readonly DispatcherTimer _searchDebounce;
    private readonly ObservableCollection<NvidiaHistoryEntry> _history = [];
    private ICollectionView _entriesView = null!;
    private FileSystemWatcher? _watcher;
    private List<NvidiaEditorEntry>? _editSnapshot;
    private string _searchFilter = string.Empty;
    private bool _pageActive;
    private bool _pendingSave;
    private bool _saveQueued;
    private bool _loading;
    private bool _suppressSearchChanged;
    private bool _applying;
    private long _internalWriteUntil;

    public ObservableCollection<NvidiaEditorEntry> Entries { get; } = [];

    public ObservableCollection<string> ValueTypes { get; } =
    [
        "Dword",
        "Boolean",
        "Hex",
        "String",
        "Binary",
    ];

    public ICollectionView EntriesView
    {
        get => _entriesView;
        private set => _entriesView = value;
    }

    public NvidiaFFlagEditorPage()
    {
        InitializeComponent();
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntries;
        DataGridRef.ItemsSource = EntriesView;
        DataContext = this;
        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(SearchDebounceMs),
        };
        HistoryListBox.ItemsSource = _history;
        UpdateHistoryCount();
        UpdateCounters();
        UpdateEmptyState();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_pageActive)
            return;

        _pageActive = true;
        _searchDebounce.Tick += SearchDebounce_Tick;
        Entries.CollectionChanged += OnEntriesChanged;
        EnsureNipIsValid();
        SetupFileWatcher();
        LoadFromNipSafe();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_pageActive)
            return;

        _pageActive = false;
        _searchDebounce.Stop();
        _searchDebounce.Tick -= SearchDebounce_Tick;
        Entries.CollectionChanged -= OnEntriesChanged;
        DisposeFileWatcher();
        CancelPendingEdit();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_loading)
            ScheduleSave();

        UpdateCounters();
        UpdateEmptyState();
    }

    private void ScheduleSave()
    {
        if (_saveQueued)
            return;

        _saveQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CompleteQueuedSave));
    }

    private void CompleteQueuedSave()
    {
        _saveQueued = false;
        SaveEntries();
    }

    private bool FilterEntries(object obj)
    {
        if (obj is not NvidiaEditorEntry entry)
            return false;

        if (_searchFilter.Length == 0)
            return true;

        return entry.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)
            || entry.SettingId.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)
            || entry.Value.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)
            || entry.ValueType.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchChanged)
            return;

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        string search = SearchTextBox.Text.Trim();
        if (search == _searchFilter)
            return;

        _searchFilter = search;
        EntriesView.Refresh();
        ShowSearchSuggestion(search);
        UpdateEmptyState();
    }

    private void ShowSearchSuggestion(string search)
    {
        if (search.Length == 0)
        {
            SetSuggestionVisibility(false);
            return;
        }

        string? best = Entries
            .Select(entry => entry.Name)
            .Where(name => name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name.StartsWith(search, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name.IndexOf(search, StringComparison.OrdinalIgnoreCase))
            .ThenBy(name => name.Length)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(best) || string.Equals(best, search, StringComparison.OrdinalIgnoreCase))
        {
            SetSuggestionVisibility(false);
            return;
        }

        SuggestionKeywordRun.Text = best;
        SetSuggestionVisibility(true);
    }

    private void SetSuggestionVisibility(bool visible)
    {
        SuggestionTextBlock.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SuggestionTextBlock.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        SuggestionTranslateTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation
        {
            To = visible ? 0 : 10,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void SuggestionTextBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        string suggestion = SuggestionKeywordRun.Text;
        if (suggestion.Length == 0)
            return;

        SearchTextBox.Text = suggestion;
        SearchTextBox.CaretIndex = suggestion.Length;
    }

    private void ClearSearchBox()
    {
        _searchDebounce.Stop();
        _suppressSearchChanged = true;
        SearchTextBox.Text = string.Empty;
        _suppressSearchChanged = false;
        _searchFilter = string.Empty;
        EntriesView.Refresh();
        SetSuggestionVisibility(false);
    }

    private void DataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        _editSnapshot = SnapshotEntries();
    }

    private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
            _pendingSave = true;
        else
            _editSnapshot = null;
    }

    private void DataGrid_CurrentCellChanged(object sender, EventArgs e)
    {
        if (!_pendingSave)
            return;

        _pendingSave = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CompleteCellEdit));
    }

    private void CompleteCellEdit()
    {
        if (_editSnapshot != null)
            RecordHistory("Edited a flag", _editSnapshot);

        _editSnapshot = null;
        SaveEntries();
        EntriesView.Refresh();
        UpdateCounters();
    }

    private void CancelPendingEdit()
    {
        try
        {
            DataGridRef.CommitEdit(DataGridEditingUnit.Cell, true);
            DataGridRef.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch
        {
            try
            {
                DataGridRef.CancelEdit(DataGridEditingUnit.Row);
            }
            catch
            {
            }
        }
    }

    private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        if (TotalFlagsTextBlock == null || SelectedFlagsTextBlock == null || DataGridRef == null)
            return;

        TotalFlagsTextBlock.Text = "Flags added: " + Entries.Count.ToString(CultureInfo.InvariantCulture);
        SelectedFlagsTextBlock.Text = "Selected: " + DataGridRef.SelectedItems.Count.ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateEmptyState()
    {
        if (EmptyStatePanel == null)
            return;

        bool empty = EntriesView.IsEmpty;
        EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            EmptyStateTextBlock.Text = _searchFilter.Length == 0
                ? "Add an NVIDIA setting to get started."
                : "No NVIDIA flags match your search.";
        }
    }

    private void SaveEntries()
    {
        try
        {
            _internalWriteUntil = Environment.TickCount64 + 1500;
            NvidiaProfileManager.SaveToNip(NipPath, Entries.ToList());
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("Failed to save NIP file:\n" + ex.Message);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddNvidiaFFlagWindow dialog = new AddNvidiaFFlagWindow(Entries.Select(entry => entry.SettingId))
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
            return;

        List<NvidiaEditorEntry> snapshot = SnapshotEntries();
        HashSet<uint> existing = new HashSet<uint>(ParseSettingIds(Entries.Select(entry => entry.SettingId)));
        int added = 0;

        foreach (NvidiaEditorEntry entry in dialog.ResultEntries)
        {
            List<uint> parsed = ParseSettingIds([entry.SettingId]);
            if (parsed.Count == 0 || !existing.Add(parsed[0]))
                continue;

            Entries.Add(entry);
            added++;
        }

        if (added == 0)
            return;

        RecordHistory(added == 1 ? "Added 1 flag" : "Added " + added.ToString(CultureInfo.InvariantCulture) + " flags", snapshot);
        ClearSearchBox();
        SaveEntries();
        SelectEntry(dialog.ResultEntries[0].SettingId);
    }

    private void SelectEntry(string settingId)
    {
        NvidiaEditorEntry? entry = Entries.FirstOrDefault(item => string.Equals(item.SettingId, settingId, StringComparison.OrdinalIgnoreCase));
        if (entry == null || !EntriesView.Contains(entry))
            return;

        DataGridRef.SelectedItem = entry;
        DataGridRef.ScrollIntoView(entry);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        List<NvidiaEditorEntry> selected = DataGridRef.SelectedItems.OfType<NvidiaEditorEntry>().ToList();
        if (selected.Count == 0)
            return;

        List<NvidiaEditorEntry> snapshot = SnapshotEntries();
        CancelPendingEdit();
        foreach (NvidiaEditorEntry entry in selected)
            Entries.Remove(entry);

        RecordHistory(selected.Count == 1 ? "Deleted 1 flag" : "Deleted " + selected.Count.ToString(CultureInfo.InvariantCulture) + " flags", snapshot);
        SaveEntries();
    }

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0)
        {
            ShowInfoMessage("There are no flags to delete.");
            return;
        }

        if (Frontend.ShowMessageBox("Are you sure you want to delete all flags?", MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        List<NvidiaEditorEntry> snapshot = SnapshotEntries();
        Entries.Clear();
        RecordHistory("Deleted all flags", snapshot);
        SaveEntries();
    }

    private void ExportNip_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0)
        {
            ShowInfoMessage("There are no NVIDIA settings to export.");
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export NVIDIA Profile",
            Filter = "NVIDIA Profile (*.nip)|*.nip",
            FileName = "Voidstrap.nip",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            NvidiaProfileManager.SaveToNip(dialog.FileName, Entries.ToList());
            ShowInfoMessage("NVIDIA profile exported successfully.");
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("Failed to export NIP file:\n\n" + ex.Message, MessageBoxImage.Error);
        }
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0)
        {
            ShowInfoMessage("There are no flags to copy.");
            return;
        }

        SaveEntries();
        CopyNvidiaSettingsDialog dialog = new CopyNvidiaSettingsDialog(Entries.Count)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            string nip = File.ReadAllText(NipPath);
            string payload;
            string format;

            switch (dialog.SelectedFormat)
            {
                case CopyNvidiaSettingsFormat.SettingRows:
                    payload = string.Join(Environment.NewLine, Entries.Select(BuildSettingRow));
                    format = "setting rows";
                    break;
                case CopyNvidiaSettingsFormat.Base64Nip:
                    payload = Convert.ToBase64String(Encoding.Unicode.GetBytes(nip));
                    format = "Base64 NIP";
                    break;
                default:
                    payload = nip;
                    format = "a NIP profile";
                    break;
            }

            Clipboard.SetText(payload);
            ShowInfoMessage("Copied " + Entries.Count.ToString(CultureInfo.InvariantCulture) + " flags to the clipboard as " + format + ".");
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("Could not access the clipboard: " + ex.Message, MessageBoxImage.Hand);
        }
    }

    private static string BuildSettingRow(NvidiaEditorEntry entry)
    {
        return entry.Name.Replace('|', ' ') + " | " + entry.SettingId + " | " + entry.Value + " | " + entry.ValueType;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_applying)
            return;

        _applying = true;
        Control? button = sender as Control;
        if (button != null)
            button.IsEnabled = false;

        try
        {
            SaveEntries();
            if (await NvidiaApplyFlow.RunAsync(Entries.ToList()))
                LoadFromDriverSafe();
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("Failed to apply NVIDIA profile:\n" + ex.Message, MessageBoxImage.Error);
        }
        finally
        {
            _applying = false;
            if (button != null)
                button.IsEnabled = true;
        }
    }

    private void LoadFromDriverSafe()
    {
        _loading = true;
        try
        {
            if (!NvidiaProfileInspector.IsAvailable)
                return;

            int refreshed = NvidiaProfileManager.RefreshValuesFromDriver(Entries);
            if (refreshed > 0)
                App.Logger.WriteLine("NvidiaFFlagEditorPage", "Driver read-back corrected " + refreshed + " value(s)");
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("NvidiaFFlagEditorPage::LoadFromDriverSafe", ex);
        }
        finally
        {
            _loading = false;
            SaveEntries();
            EntriesView.Refresh();
            UpdateCounters();
            UpdateEmptyState();
        }
    }

    private void LoadFromNipSafe()
    {
        _loading = true;
        try
        {
            Entries.Clear();
            List<NvidiaEditorEntry> loaded = NvidiaProfileManager.LoadFromNip(NipPath)
                .GroupBy(entry => entry.SettingId)
                .Select(group => group.First())
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (NvidiaEditorEntry entry in loaded)
                Entries.Add(entry);
        }
        catch (XmlException)
        {
            RecreateNipFile();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(LoadFromNipSafe));
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("NvidiaFFlagEditorPage::LoadFromNipSafe", ex);
        }
        finally
        {
            _loading = false;
            EntriesView.Refresh();
            UpdateCounters();
            UpdateEmptyState();
        }
    }

    private void ResetNipFile_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0)
        {
            ShowInfoMessage("There are no NVIDIA flags to reset.");
            return;
        }

        if (Frontend.ShowMessageBox("Are you sure you want to reset every NVIDIA flag?", MessageBoxImage.Exclamation, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        List<NvidiaEditorEntry> snapshot = SnapshotEntries();
        try
        {
            List<uint> ids = ParseSettingIds(Entries.Select(entry => entry.SettingId));
            Entries.Clear();
            RecreateNipFile();
            SaveEntries();

            if (NvidiaProfileInspector.IsAvailable && ids.Count > 0)
            {
                NvidiaApplyResult result = NvidiaProfileInspector.Reset(ids);
                if (!result.Ok)
                    Frontend.ShowMessageBox(result.Message, MessageBoxImage.Warning);
            }

            RecordHistory("Reset every NVIDIA flag", snapshot);
        }
        catch (Exception ex)
        {
            RestoreSnapshot(snapshot);
            Frontend.ShowMessageBox("Error while resetting NIP file: " + ex.Message);
        }
    }

    private void BackButton(object sender, RoutedEventArgs e)
    {
        if (NavigationService != null && NavigationService.CanGoBack)
            NavigationService.GoBack();
        else
            NavigationService?.Navigate(new NvidiaFastFlagsPage());
    }

    private void RecordHistory(string message, List<NvidiaEditorEntry> snapshot)
    {
        _history.Insert(0, new NvidiaHistoryEntry
        {
            Message = message,
            Snapshot = snapshot,
            Timestamp = DateTime.Now,
        });

        while (_history.Count > MaxHistoryEntries)
            _history.RemoveAt(_history.Count - 1);

        UpdateHistoryCount();
    }

    private void UndoHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is NvidiaHistoryEntry entry)
            UndoHistory(entry);
    }

    private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryListBox.SelectedItem is NvidiaHistoryEntry entry)
            UndoHistory(entry);
    }

    private void UndoHistory(NvidiaHistoryEntry entry)
    {
        RestoreSnapshot(entry.Snapshot);
        _history.Remove(entry);
        UpdateHistoryCount();
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        UpdateHistoryCount();
    }

    private void UpdateHistoryCount()
    {
        if (HistoryCountTextBlock == null)
            return;

        HistoryCountTextBlock.Text = _history.Count == 0
            ? "No changes recorded"
            : _history.Count.ToString(CultureInfo.InvariantCulture) + (_history.Count == 1 ? " change recorded" : " changes recorded");
    }

    private List<NvidiaEditorEntry> SnapshotEntries()
    {
        return Entries.Select(CloneEntry).ToList();
    }

    private void RestoreSnapshot(IEnumerable<NvidiaEditorEntry> snapshot)
    {
        _loading = true;
        try
        {
            Entries.Clear();
            foreach (NvidiaEditorEntry entry in snapshot)
                Entries.Add(CloneEntry(entry));
        }
        finally
        {
            _loading = false;
        }

        SaveEntries();
        EntriesView.Refresh();
        UpdateCounters();
        UpdateEmptyState();
    }

    private static NvidiaEditorEntry CloneEntry(NvidiaEditorEntry entry)
    {
        return new NvidiaEditorEntry
        {
            Name = entry.Name,
            SettingId = entry.SettingId,
            Value = entry.Value,
            ValueType = entry.ValueType,
        };
    }

    private void SetupFileWatcher()
    {
        DisposeFileWatcher();
        Directory.CreateDirectory(NipDirectory);
        _watcher = new FileSystemWatcher(NipDirectory, "Voidstrap.nip")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnNipFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void DisposeFileWatcher()
    {
        if (_watcher == null)
            return;

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnNipFileChanged;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnNipFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!_pageActive || Environment.TickCount64 < _internalWriteUntil)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(LoadFromNipSafe));
    }

    private static List<uint> ParseSettingIds(IEnumerable<string> raw)
    {
        List<uint> parsed = [];
        foreach (string id in raw)
        {
            string text = (id ?? string.Empty).Trim();
            if (text.Length == 0)
                continue;

            bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (uint.TryParse(
                    hex ? text[2..] : text,
                    hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out uint value))
            {
                parsed.Add(value);
            }
        }
        return parsed;
    }

    private static void EnsureNipIsValid()
    {
        Directory.CreateDirectory(NipDirectory);
        if (!File.Exists(NipPath))
        {
            RecreateNipFile();
            return;
        }

        try
        {
            using XmlReader reader = XmlReader.Create(NipPath);
            while (reader.Read())
            {
            }
        }
        catch
        {
            RecreateNipFile();
        }
    }

    private static void RecreateNipFile()
    {
        Directory.CreateDirectory(NipDirectory);
        NvidiaProfileManager.SaveToNip(NipPath, []);
    }

    private static void ShowInfoMessage(string message)
    {
        Frontend.ShowMessageBox(message, MessageBoxImage.Asterisk);
    }
}
