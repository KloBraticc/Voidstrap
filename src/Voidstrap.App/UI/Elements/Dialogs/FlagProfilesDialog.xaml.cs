using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class FlagProfilesDialog : WpfUiWindow
{
    private const int MaxLocalBytes = 600000;
    private readonly ObservableCollection<WebsiteFlagProfile> _profiles = new();
    private readonly Dictionary<string, string> _currentFlags;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _busy;
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

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        await ReloadAsync();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private async Task ReloadAsync()
    {
        if (_busy)
            return;
        SetBusy(true);
        WebsiteFlagProfile? selected = LoadBackup.SelectedItem as WebsiteFlagProfile;
        string selectedId = selected?.Id ?? "";
        try
        {
            List<WebsiteFlagProfile> profiles = new();
            if (WebsiteAuth.IsSignedIn())
            {
                try
                {
                    profiles.AddRange(await WebsiteFlagProfiles.GetAsync(_cancellation.Token));
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("FlagProfiles::LoadAccount", ex);
                }
            }
            profiles.AddRange(LoadLocalProfiles());
            _profiles.Clear();
            foreach (WebsiteFlagProfile profile in profiles.OrderByDescending(x => x.IsCloud).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                _profiles.Add(profile);
            WebsiteFlagProfile? restore = _profiles.FirstOrDefault(x => x.Id == selectedId);
            if (restore != null)
                LoadBackup.SelectedItem = restore;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static List<WebsiteFlagProfile> LoadLocalProfiles()
    {
        List<WebsiteFlagProfile> profiles = new();
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
                    profiles.Add(new WebsiteFlagProfile
                    {
                        Id = "local:" + fileName,
                        Name = Path.GetFileNameWithoutExtension(fileName),
                        Flags = flags,
                        Updated = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                        IsCloud = false,
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

    private async void OKButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        if (Tabs.SelectedIndex == 1)
        {
            if (LoadBackup.SelectedItem is not WebsiteFlagProfile selected)
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
        SetBusy(true);
        try
        {
            if (WebsiteAuth.IsSignedIn())
            {
                WebsiteFlagProfile? existing = _profiles.FirstOrDefault(x => x.IsCloud && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
                await WebsiteFlagProfiles.SaveAsync(name, _currentFlags, existing, _cancellation.Token);
            }
            else
            {
                string fileName = SafeLocalFileName(name);
                Directory.CreateDirectory(Paths.SavedBackups);
                string path = Path.Combine(Paths.SavedBackups, fileName);
                JsonFile.SerializeAtomic(path, _currentFlags, JsonOptions.Indented, false);
                Dictionary<string, string>? stored = JsonFile.Deserialize<Dictionary<string, string>>(path, JsonOptions.Tolerant);
                EnsureCompleteSnapshot(_currentFlags, stored);
            }
            Close();
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FlagProfiles::Save", ex);
            Frontend.ShowMessageBox(ex is InvalidOperationException ? ex.Message : "That profile could not be saved. Try again.", MessageBoxImage.Hand);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || LoadBackup.SelectedItem is not WebsiteFlagProfile selected)
            return;
        if (Frontend.ShowMessageBox("Delete the profile '" + selected.Name + "'?", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        SetBusy(true);
        try
        {
            if (selected.IsCloud)
                await WebsiteFlagProfiles.DeleteAsync(selected, _cancellation.Token);
            else
                App.FastFlags.DeleteBackup(selected.LocalFileName);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FlagProfiles::Delete", ex);
            Frontend.ShowMessageBox(ex is InvalidOperationException ? ex.Message : "That profile could not be deleted. Try again.", MessageBoxImage.Hand);
        }
        finally
        {
            SetBusy(false);
        }
        await ReloadAsync();
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

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Tabs.IsEnabled = !busy;
        UpdateState();
    }

    private void UpdateState()
    {
        EmptyProfiles.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = !_busy && LoadBackup.SelectedItem is WebsiteFlagProfile;
        OKButton.IsEnabled = !_busy && (Tabs.SelectedIndex == 1 ? LoadBackup.SelectedItem is WebsiteFlagProfile : _currentFlags.Count > 0 && !string.IsNullOrEmpty(NormalizeName(SaveBackup.Text)));
    }

    private static string NormalizeName(string value)
    {
        string name = Regex.Replace(value ?? "", "[\\x00-\\x1f\\x7f]", "");
        name = Regex.Replace(name, "\\s+", " ").Trim();
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
}
