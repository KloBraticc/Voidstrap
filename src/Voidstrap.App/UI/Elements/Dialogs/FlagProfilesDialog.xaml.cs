using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public sealed class FlagProfile
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public Dictionary<string, string> Flags { get; init; } = new(StringComparer.Ordinal);

    public string LocalFileName { get; init; } = "";
}

public partial class FlagProfilesDialog : WpfUiWindow
{
    private const int MaxLocalBytes = 600000;
    private readonly ObservableCollection<FlagProfile> _profiles = new();
    private readonly Dictionary<string, string> _currentFlags;
    private bool _loaded;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;
    public Dictionary<string, string>? AppliedFlags { get; private set; }
    public string AppliedProfileName { get; private set; } = "";
    public bool ReplaceExisting => ClearFlags.IsChecked == true;

    public FlagProfilesDialog(Dictionary<string, string> currentFlags)
    {
        _currentFlags = new Dictionary<string, string>(currentFlags, StringComparer.Ordinal);
        InitializeComponent();
        LoadBackup.ItemsSource = _profiles;
        UpdateState();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        Reload();
    }

    private void Reload()
    {
        string selectedId = (LoadBackup.SelectedItem as FlagProfile)?.Id ?? "";
        _profiles.Clear();
        foreach (FlagProfile profile in LoadLocalProfiles().OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            _profiles.Add(profile);
        FlagProfile? restore = _profiles.FirstOrDefault(x => x.Id == selectedId);
        if (restore != null)
            LoadBackup.SelectedItem = restore;
        UpdateState();
    }

    private static List<FlagProfile> LoadLocalProfiles()
    {
        List<FlagProfile> profiles = new();
        try
        {
            Directory.CreateDirectory(Paths.SavedBackups);
            foreach (string file in Directory.EnumerateFiles(Paths.SavedBackups))
            {
                try
                {
                    FileInfo info = new FileInfo(file);
                    if (info.Length <= 0 || info.Length > MaxLocalBytes)
                        continue;
                    Dictionary<string, object>? raw = JsonFile.Deserialize<Dictionary<string, object>>(file, JsonOptions.Tolerant);
                    if (raw == null || raw.Count > 1000)
                        continue;
                    Dictionary<string, string> flags = new(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object> item in raw)
                    {
                        if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 160 || item.Value == null)
                            continue;
                        string value = item.Value.ToString() ?? "";
                        if (value.Length <= 1000)
                            flags[item.Key] = value;
                    }
                    string fileName = Path.GetFileName(file);
                    profiles.Add(new FlagProfile
                    {
                        Id = "local:" + fileName,
                        Name = Path.GetFileNameWithoutExtension(fileName),
                        Flags = flags,
                        LocalFileName = fileName
                    });
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("FlagProfiles::LoadDeviceProfile", ex);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FlagProfiles::LoadDeviceProfiles", ex);
        }
        return profiles;
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedIndex == 1)
        {
            if (LoadBackup.SelectedItem is not FlagProfile selected)
                return;
            AppliedFlags = new Dictionary<string, string>(selected.Flags, StringComparer.Ordinal);
            AppliedProfileName = selected.Name;
            Result = MessageBoxResult.OK;
            DialogResult = true;
            return;
        }
        string name = NormalizeName(SaveBackup.Text);
        if (string.IsNullOrEmpty(name) || _currentFlags.Count == 0)
            return;
        try
        {
            string fileName = SafeLocalFileName(name);
            Directory.CreateDirectory(Paths.SavedBackups);
            string path = Path.Combine(Paths.SavedBackups, fileName);
            JsonFile.SerializeAtomic(path, _currentFlags, JsonOptions.Indented, false);
            Dictionary<string, string>? stored = JsonFile.Deserialize<Dictionary<string, string>>(path, JsonOptions.Tolerant);
            EnsureCompleteSnapshot(_currentFlags, stored);
            Close();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FlagProfiles::Save", ex);
            Frontend.ShowMessageBox("That profile could not be saved. Try again.", MessageBoxImage.Hand);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (LoadBackup.SelectedItem is not FlagProfile selected)
            return;
        if (Frontend.ShowMessageBox("Delete the profile '" + selected.Name + "'?", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        App.FastFlags.DeleteBackup(selected.LocalFileName);
        Reload();
    }

    private void LoadBackup_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateState();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateState();
    }

    private void SaveBackup_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateState();
    }

    private void UpdateState()
    {
        EmptyProfiles.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = LoadBackup.SelectedItem is FlagProfile;
        OKButton.IsEnabled = Tabs.SelectedIndex == 1 ? LoadBackup.SelectedItem is FlagProfile : _currentFlags.Count > 0 && !string.IsNullOrEmpty(NormalizeName(SaveBackup.Text));
    }

    private static string NormalizeName(string value)
    {
        string name = ControlCharacterPattern.Replace(value ?? "", "");
        name = WhitespacePattern.Replace(name, " ").Trim();
        return name.Length <= 48 ? name : "";
    }

    private static string SafeLocalFileName(string name)
    {
        string safe = name;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        safe = safe.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safe))
            throw new InvalidDataException("Profile name is invalid");
        return safe + ".json";
    }

    private static void EnsureCompleteSnapshot(Dictionary<string, string> expected, Dictionary<string, string>? actual)
    {
        if (actual == null || actual.Count != expected.Count)
            throw new InvalidDataException("The complete flag list could not be saved");
        foreach (KeyValuePair<string, string> flag in expected)
        {
            if (!actual.TryGetValue(flag.Key, out string? value) || !string.Equals(value, flag.Value, StringComparison.Ordinal))
                throw new InvalidDataException("The complete flag list could not be saved");
        }
    }

    [GeneratedRegex("[\\x00-\\x1f\\x7f]")]
    private static partial Regex ControlCharacterPattern { get; }
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern { get; }
}
