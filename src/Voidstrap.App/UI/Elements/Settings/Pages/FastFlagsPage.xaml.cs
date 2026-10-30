using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Navigation;
using System.Windows.Threading;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Controls;
using Wpf.Ui.Mvvm.Contracts;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class FastFlagsPage : UiPage
{
    [GeneratedRegex("^[\\+\\-]?[0-9]*$")]
    private static partial Regex SignedIntegerInputPattern { get; }

    public class NvidiaFFlag : INotifyPropertyChanged, IDataErrorInfo
    {
        private string _name = string.Empty;

        private string _value = string.Empty;

        public string Name
        {
            get { return _name; }
            set
            {
                _name = value;
                OnPropertyChanged("Name");
            }
        }

        public string Value
        {
            get { return _value; }
            set
            {
                _value = value;
                OnPropertyChanged("Value");
            }
        }

        public string Error => string.Empty;

        public string this[string columnName]
        {
            get
            {
                if (columnName == "Name" && string.IsNullOrWhiteSpace(Name))
                {
                    return "Flag name is required";
                }
                if (columnName == "Value" && string.IsNullOrWhiteSpace(Value))
                {
                    return "Value is required";
                }
                return string.Empty;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string prop)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }
    }

    public class FFlagItem
    {
        public string Name { get; set; } = null!;

        public string Value { get; set; } = null!;
    }

    private bool _initialLoad;

    private FastFlagsViewModel _viewModel = null!;
    private bool _loadingFlags;

    private DispatcherTimer? _waterPreviewTimer;

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex _intRegex { get; }

    private static readonly HttpClient _httpClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(25L));

    public ObservableCollection<FFlagItem> FFlags { get; } = new ObservableCollection<FFlagItem>();

    public ObservableCollection<NvidiaFFlag> CustomFFlags { get; } = new ObservableCollection<NvidiaFFlag>();

    private void ValidateInt(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !_intRegex.IsMatch(e.Text);
    }

    public FastFlagsPage()
    {
        SetupViewModel();
        InitializeComponent();
        base.Loaded += LoadFlagsOnLoaded;
        base.Loaded += StartWaterPreview;
        base.Loaded += RestartNvidiaWarning;
        base.Unloaded += FastFlagsPage_Unloaded;
        Wpf.Ui.Controls.Breadcrumb.AddBackRequestedHandler(this, OnBreadcrumbBack);
    }

    internal bool IsAdvancedTabSelected => ReferenceEquals(FlagTabs.SelectedItem, AdvancedFlagsTab);

    private void OnBreadcrumbBack(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(FlagTabs.SelectedItem, AdvancedFlagsTab))
            FlagTabs.SelectedIndex = 0;
    }

    private void RestartNvidiaWarning(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(FlagTabs.SelectedItem, NvidiaTab) && NvidiaWarning.Visibility == Visibility.Visible)
            ShowNvidiaWarning();
    }

    private void FastFlagsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadingFlags = false;
        StopWaterPreview();
        StopNvidiaWarning();
    }

    private const int NvidiaWarningSeconds = 10;
    private DispatcherTimer? _nvidiaWarningTimer;
    private readonly Stopwatch _nvidiaWarningClock = new();
    private TabItem? _previousTab;

    private void FlagTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, FlagTabs))
            return;
        if (ReferenceEquals(FlagTabs.SelectedItem, AdvancedFlagsTab))
            Voidstrap.UI.BreadcrumbHeader.SetLocalized(this, nameof(Voidstrap.Resources.Strings.Ui_FastFlags_AdvancedFFlags));
        else
            Voidstrap.UI.BreadcrumbHeader.Clear(this);
        if (Window.GetWindow(this) is Voidstrap.UI.Elements.Settings.MainWindow mainWindow)
            mainWindow.RefreshGlobalSearchBoxVisibility();
        if (ReferenceEquals(FlagTabs.SelectedItem, NvidiaTab))
            ShowNvidiaWarning();
        else
        {
            StopNvidiaWarning();
            _previousTab = FlagTabs.SelectedItem as TabItem;
        }
    }

    private void ShowNvidiaWarning()
    {
        StopNvidiaWarning();
        if (App.State.Prop.NvidiaFlagsWarningAccepted)
        {
            NvidiaWarning.Visibility = Visibility.Collapsed;
            NvidiaFrame.Visibility = Visibility.Visible;
            return;
        }
        NvidiaFrame.Visibility = Visibility.Collapsed;
        NvidiaWarning.Visibility = Visibility.Visible;
        NvidiaWarningContinue.IsEnabled = false;
        _nvidiaWarningClock.Restart();
        _nvidiaWarningTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _nvidiaWarningTimer.Tick += NvidiaWarningTimer_Tick;
        UpdateNvidiaWarning();
    }

    private void NvidiaWarningTimer_Tick(object? sender, EventArgs e)
    {
        UpdateNvidiaWarning();
    }

    private void UpdateNvidiaWarning()
    {
        if (_nvidiaWarningTimer is null)
            return;
        _nvidiaWarningTimer.Stop();
        double remaining = NvidiaWarningSeconds * 1000d - _nvidiaWarningClock.Elapsed.TotalMilliseconds;
        string label = Voidstrap.Resources.Strings.Menu_FastFlagEditor_Warning_Continue;
        if (remaining <= 0)
        {
            NvidiaWarningContinue.Content = label;
            NvidiaWarningContinue.IsEnabled = true;
            StopNvidiaWarning();
            return;
        }
        NvidiaWarningContinue.Content = $"({(int)Math.Ceiling(remaining / 1000d)}) {label}";
        _nvidiaWarningTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, remaining % 1000d));
        _nvidiaWarningTimer.Start();
    }

    private void StopNvidiaWarning()
    {
        _nvidiaWarningClock.Stop();
        if (_nvidiaWarningTimer is null)
            return;
        _nvidiaWarningTimer.Stop();
        _nvidiaWarningTimer.Tick -= NvidiaWarningTimer_Tick;
        _nvidiaWarningTimer = null;
    }

    private void NvidiaWarningContinue_Click(object sender, RoutedEventArgs e)
    {
        if (!NvidiaWarningContinue.IsEnabled)
            return;
        StopNvidiaWarning();
        App.State.Prop.NvidiaFlagsWarningAccepted = true;
        App.State.Save();
        NvidiaWarning.Visibility = Visibility.Collapsed;
        NvidiaFrame.Visibility = Visibility.Visible;
    }

    private void NvidiaWarningBack_Click(object sender, RoutedEventArgs e)
    {
        StopNvidiaWarning();
        TabItem? target = _previousTab is { IsEnabled: true, Visibility: Visibility.Visible } ? _previousTab : null;
        target ??= FlagTabs
            .Items.OfType<TabItem>()
            .FirstOrDefault(tab => !ReferenceEquals(tab, NvidiaTab) && tab.IsEnabled && tab.Visibility == Visibility.Visible);
        if (target is not null)
            FlagTabs.SelectedItem = target;
    }

    private void StartWaterPreview(object sender, RoutedEventArgs e)
    {
        StopWaterPreview();
        _viewModel.RefreshWaterPreview();
        _waterPreviewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _waterPreviewTimer.Tick += WaterPreviewTimer_Tick;
        _waterPreviewTimer.Start();
    }

    private void StopWaterPreview()
    {
        if (_waterPreviewTimer is null)
            return;
        _waterPreviewTimer.Stop();
        _waterPreviewTimer.Tick -= WaterPreviewTimer_Tick;
        _waterPreviewTimer = null;
    }

    private void WaterPreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (WaterPreviewImage.IsVisible)
            _viewModel.AdvanceWaterPreview();
    }

    private async void LoadFlagsOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadingFlags || FFlags.Count > 0)
            return;

        _loadingFlags = true;
        try
        {
            await LoadFFlagsAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FastFlagsPage::LoadFlagsOnLoaded", ex);
        }
        finally
        {
            _loadingFlags = false;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Utilities.OpenWebLink(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private async Task LoadFFlagsAsync()
    {
        if (FFlags.Count > 0)
        {
            return;
        }

        IReadOnlyDictionary<string, string> dict = await RobloxFastFlagAllowlist.RefreshAsync();

        FFlags.Clear();
        foreach (KeyValuePair<string, string> item in dict.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key))
        {
            FFlags.Add(new FFlagItem { Name = item.Key, Value = item.Value });
        }
        DataGrid.ItemsSource = FFlags;
    }

    private void NvidiaFrame_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node != null && node != sender)
        {
            if (node is ScrollViewer scrollViewer && scrollViewer.ScrollableHeight > 0.0)
            {
                return;
            }
            node = (
                (node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D)
                    ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node)
            );
        }
        e.Handled = true;
        System.Windows.Input.MouseWheelEventArgs args = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        };
        if (sender is UIElement element)
        {
            element.RaiseEvent(args);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_initialLoad)
        {
            _initialLoad = true;
        }
        else
        {
            SetupViewModel();
        }
    }

    private void SetupViewModel()
    {
        if (_viewModel != null)
        {
            _viewModel.OpenFlagEditorEvent -= OpenFlagEditor;
            _viewModel.RequestPageReloadEvent -= RequestPageReload;
        }

        _viewModel = new FastFlagsViewModel();
        _viewModel.OpenFlagEditorEvent += OpenFlagEditor;
        _viewModel.RequestPageReloadEvent += RequestPageReload;
        base.DataContext = _viewModel;
        if (IsLoaded)
            _viewModel.RefreshWaterPreview();
    }

    private void RequestPageReload(object? sender, EventArgs e)
    {
        SetupViewModel();
    }

    private void OpenFlagEditor(object? sender, EventArgs e)
    {
        if (Window.GetWindow((DependencyObject)(object)this) is INavigationWindow navigationWindow)
        {
            navigationWindow.Navigate(typeof(FastFlagEditorPage));
        }
    }

    private void ValidateUInt32(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !uint.TryParse(e.Text, out var _);
    }

    private void CheckSystemButton_Click(object sender, RoutedEventArgs e)
    {
        CheckSystemButton.IsEnabled = false;
        try
        {
            ApplyRecommendedFlags();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FastFlagsPage::ApplyRecommended", ex);
            Frontend.ShowMessageBox("The recommended flags could not be applied: " + ex.Message, MessageBoxImage.Hand);
        }
        finally
        {
            CheckSystemButton.IsEnabled = true;
        }
    }

    private void ApplyRecommendedFlags()
    {
        string profile = (RecommendedProfileBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Balanced";
        Dictionary<string, Dictionary<string, string>> profiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["Performance"] = new Dictionary<string, string>
            {
                ["DFFlagTextureQualityOverrideEnabled"] = "True",
                ["DFIntTextureQualityOverride"] = "0",
                ["FIntDebugForceMSAASamples"] = "1",
            },
            ["Balanced"] = new Dictionary<string, string>
            {
                ["DFFlagTextureQualityOverrideEnabled"] = "True",
                ["DFIntTextureQualityOverride"] = "2",
                ["FIntDebugForceMSAASamples"] = "2",
            },
            ["Quality"] = new Dictionary<string, string>
            {
                ["DFFlagTextureQualityOverrideEnabled"] = "True",
                ["DFIntTextureQualityOverride"] = "3",
                ["FIntDebugForceMSAASamples"] = "4",
            },
        };

        if (!profiles.TryGetValue(profile, out Dictionary<string, string>? flags))
        {
            Frontend.ShowMessageBox("Choose a recommended profile first.", MessageBoxImage.Warning);
            return;
        }

        foreach (KeyValuePair<string, string> flag in flags)
        {
            if (!RobloxFastFlagAllowlist.IsAllowed(flag.Key))
                throw new InvalidOperationException(flag.Key + " is not on Roblox's FastFlag allowlist.");
            App.FastFlags.SetValue(flag.Key, flag.Value);
        }
        App.FastFlags.Save();
        Frontend.ShowMessageBox(
            profile + " profile applied. " + flags.Count + " Roblox allowlisted flags were updated.\nYour other flags were left unchanged.",
            MessageBoxImage.Asterisk
        );
    }

    [GeneratedRegex(
        "<p><strong>(?<category>[^<]+)</strong>:</p>\\s*<ul>(?<items>.*?)</ul>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        "en-US"
    )]
    private static partial Regex CategorySectionPattern { get; }

    [GeneratedRegex("<li>(?<flag>[A-Za-z][A-Za-z0-9_]*)</li>", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex FlagListItemPattern { get; }

    private void OpenAdvancedFlags_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        FlagTabs.SelectedItem = AdvancedFlagsTab;
    }
}
