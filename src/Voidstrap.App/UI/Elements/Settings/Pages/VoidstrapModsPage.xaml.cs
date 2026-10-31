using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Search;
using Voidstrap.Integrations.Plugins;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class VoidstrapModsPage : UiPage
{
    public VoidstrapModsPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        PluginHost.PanesChanged -= OnPanesChanged;
        PluginHost.PanesChanged += OnPanesChanged;
        PluginHost.PaneInvalidated -= OnPaneInvalidated;
        PluginHost.PaneInvalidated += OnPaneInvalidated;
        PluginHost.StatusChanged -= OnStatusChanged;
        PluginHost.StatusChanged += OnStatusChanged;
        PluginHost.ModsChanged -= OnModsChanged;
        PluginHost.ModsChanged += OnModsChanged;
        EnsureRenderTimer();
        RenderTimer_Tick(null, EventArgs.Empty);
        RenderAll();
        RenderStatus();
        RenderAccount();
        _ = LoadLibraryAsync(false);
        _libraryPoll.Tick -= LibraryPoll_Tick;
        _libraryPoll.Tick += LibraryPoll_Tick;
        _libraryPoll.Start();
    }

    private async void LibraryPoll_Tick(object? sender, EventArgs e)
    {
        try
        {
            ModLibrary.Index fresh = await ModLibrary.GetIndexAsync(false, CancellationToken.None);
            if (_libraryIndex != null && _libraryIndex.Commit != fresh.Commit && IsLoaded)
                await LoadLibraryAsync(false);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("VoidstrapModsPage::LibraryPoll_Tick", "Could not check the library: " + ex.Message);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _libraryPoll.Stop();
        _libraryPoll.Tick -= LibraryPoll_Tick;
        _renderGeneration++;
        PluginHost.PanesChanged -= OnPanesChanged;
        PluginHost.PaneInvalidated -= OnPaneInvalidated;
        PluginHost.StatusChanged -= OnStatusChanged;
        PluginHost.ModsChanged -= OnModsChanged;
        if (_renderTimer != null)
        {
            _renderTimer.Stop();
            _renderTimer.Tick -= RenderTimer_Tick;
            _renderTimer = null;
        }
        _libraryCts?.Cancel();
        _libraryCts?.Dispose();
        _libraryCts = null;
    }

    private void OnModsChanged() => Dispatcher.BeginInvoke(new Action(RenderMods));

    private System.Windows.Threading.DispatcherTimer? _renderTimer;

    private int _renderGeneration;

    private bool _modRenderRunning;

    private string _lastModSignature = "";

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, BitmapSource? Icon)> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private void RenderMods()
    {
        if (!IsLoaded || Dispatcher.HasShutdownStarted)
            return;
        EnsureRenderTimer();
        if (_renderTimer is { IsEnabled: false })
            _renderTimer.Start();
    }

    private void EnsureRenderTimer()
    {
        if (_renderTimer != null)
            return;
        _renderTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _renderTimer.Tick += RenderTimer_Tick;
    }

    private async void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (_modRenderRunning)
            return;
        _renderTimer?.Stop();
        _modRenderRunning = true;
        int generation = ++_renderGeneration;
        HashSet<string> updating = [.. _updateSpins.Keys];
        try
        {
            List<string> pendingIcons = [];
            List<ModRenderItem> items = await Task.Run(() =>
            {
                IReadOnlyList<PluginHost.ModInfo> mods = PluginHost.ListMods();
                List<ModRenderItem> built = [];
                foreach (PluginHost.ModInfo mod in mods)
                {
                    ModLibrary.LibraryMod? libraryMod = _libraryIndex?.Mods.FirstOrDefault(m => m.Name == mod.Name);
                    PluginManifest.TryRead(mod.Directory, out PluginManifest? manifest, out _);
                    BitmapSource? icon = CachedModIcon(mod.Directory, out bool iconPending);
                    if (iconPending)
                        pendingIcons.Add(mod.Directory);
                    bool hasUpdate = libraryMod != null && ModLibrary.GetState(libraryMod) == ModLibrary.InstallState.UpdateAvailable;
                    built.Add(new ModRenderItem(mod, libraryMod, manifest, icon, hasUpdate, updating.Contains(mod.Name)));
                }
                return built;
            });
            if (generation != _renderGeneration || !IsLoaded || Dispatcher.HasShutdownStarted)
                return;
            string signature = string.Join("|", items.Select(item => item.Signature));
            if (signature != _lastModSignature || ModsHost.Children.Count != items.Count)
            {
                _lastModSignature = signature;
                ModsHost.Children.Clear();
                EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (ModRenderItem item in items)
                    ModsHost.Children.Add(BuildInstalledModCard(item));
            }
            _ = FillModIconsAsync(pendingIcons, generation);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("VoidstrapModsPage::RenderMods", ex);
        }
        finally
        {
            _modRenderRunning = false;
        }
    }

    private async Task FillModIconsAsync(List<string> directories, int generation)
    {
        if (directories.Count == 0)
            return;
        BitmapSource?[] icons = await Task.WhenAll(directories.Select(DecodeModIconAsync));
        if (generation != _renderGeneration || !IsLoaded || !icons.Any(icon => icon != null))
            return;
        _lastModSignature = "";
        RenderMods();
    }

    private static BitmapSource? CachedModIcon(string directory, out bool pending)
    {
        pending = false;
        try
        {
            string? iconPath = ModFiles.GetIconPath(directory);
            if (iconPath == null)
                return null;
            if (_iconCache.TryGetValue(iconPath, out (DateTime Stamp, BitmapSource? Icon) cached) && cached.Stamp == File.GetLastWriteTimeUtc(iconPath))
                return cached.Icon;
            pending = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<BitmapSource?> DecodeModIconAsync(string directory)
    {
        string? iconPath = ModFiles.GetIconPath(directory);
        if (iconPath == null)
            return null;
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(iconPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (_iconCache.TryGetValue(iconPath, out (DateTime Stamp, BitmapSource? Icon) cached) && cached.Stamp == stamp)
            return cached.Icon;
        BitmapSource? icon = await MediaContentFilter.LoadCheckedIconAsync(iconPath).ConfigureAwait(false);
        if (icon != null)
            _iconCache[iconPath] = (stamp, icon);
        return icon;
    }

    private static long IconStamp(string directory) =>
        ModFiles.GetIconPath(directory) is string path ? File.GetLastWriteTimeUtc(path).Ticks : 0;

    private sealed record ModRenderItem(
        PluginHost.ModInfo Info,
        ModLibrary.LibraryMod? Library,
        PluginManifest? Manifest,
        BitmapSource? Icon,
        bool HasUpdate,
        bool Updating
    )
    {
        public string Signature =>
            string.Join(
                "\u0001",
                Info.Name,
                Info.Version,
                Info.Description,
                Info.Consent,
                Info.Loaded,
                Info.Problems.Count,
                Info.Problems.LastOrDefault() ?? "",
                Library?.Version ?? "",
                Manifest?.Version ?? "",
                Icon != null,
                Library != null && _libraryIcons.ContainsKey(Library.Id),
                IconStamp(Info.Directory),
                HasUpdate,
                Updating
            );
    }

    private Border BuildInstalledModCard(ModRenderItem item)
    {
        PluginHost.ModInfo mod = item.Info;
        ModLibrary.LibraryMod? libraryMod = item.Library;
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Border icon = new() { Child = BuildModIcon(mod, libraryMod, item.Icon) };
        icon.SetResourceReference(StyleProperty, "RowIcon");
        row.Children.Add(icon);

        StackPanel text = new() { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        WrapPanel title = new() { Orientation = Orientation.Horizontal };
        title.Children.Add(StyledText("RowTitle", mod.Name));
        if (libraryMod != null)
            AddTrustBadges(title, libraryMod);
        else if (item.Manifest != null && PluginNative.IsPowerful(item.Manifest))
            title.Children.Add(MakePowerfulBadge());
        bool invalid = mod.Consent == PluginHost.InvalidState;
        if (invalid)
            title.Children.Add(
                MakeBadge(Strings.Ui_VoidstrapMods_InvalidBadge, SymbolRegular.ErrorCircle24, Color.FromRgb(232, 72, 85), "")
            );
        else
            title.Children.Add(
                MakeBadge(
                    mod.Loaded ? Strings.Ui_VoidstrapMods_LoadedBadge : Strings.Ui_VoidstrapMods_DisabledBadge,
                    mod.Loaded ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.PauseCircle24,
                    mod.Loaded ? Color.FromRgb(46, 204, 113) : Color.FromRgb(160, 168, 180),
                    ""
                )
            );
        text.Children.Add(title);

        System.Windows.Controls.TextBlock summary = StyledText("RowSummary", mod.Description);
        summary.MaxHeight = 40;
        text.Children.Add(summary);

        StackPanel meta = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        string authorLogin = libraryMod?.Login ?? "";
        if (string.IsNullOrEmpty(authorLogin) && GitHubPublisher.SignedIn != null)
            authorLogin = GitHubPublisher.SignedIn.Login;
        if (!string.IsNullOrEmpty(authorLogin))
        {
            System.Windows.Controls.TextBlock author = StyledText("RowMeta", "@" + authorLogin);
            author.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            meta.Children.Add(author);
        }
        IReadOnlyList<string> networkHosts = libraryMod?.Network ?? item.Manifest?.NetworkHosts ?? [];
        bool usesRoblox = libraryMod?.UsesRobloxAccount ?? item.Manifest?.HasPermission(PluginManifest.RobloxAccountPermission) ?? false;
        System.Windows.Controls.TextBlock network = StyledText(
            "RowMeta",
            (
                networkHosts.Count == 0
                    ? Strings.Ui_VoidstrapMods_LibraryNoNetwork
                    : string.Format(Strings.Ui_VoidstrapMods_LibraryNetwork, string.Join(", ", networkHosts))
            ) + (usesRoblox ? "   " + Strings.Ui_VoidstrapMods_LibraryRobloxAccess : "")
        );
        network.Margin = new Thickness(!string.IsNullOrEmpty(authorLogin) ? 12 : 0, 0, 0, 0);
        meta.Children.Add(network);
        text.Children.Add(meta);
        row.Children.Add(text);

        StackPanel side = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(side, 2);
        StackPanel version = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 10),
        };
        SymbolIcon tagIcon = new()
        {
            Symbol = SymbolRegular.Tag24,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tagIcon.SetResourceReference(SymbolIcon.ForegroundProperty, "TextFillColorTertiaryBrush");
        version.Children.Add(tagIcon);
        System.Windows.Controls.TextBlock versionText = StyledText("RowMeta", mod.Version);
        versionText.Margin = new Thickness(5, 0, 0, 0);
        version.Children.Add(versionText);
        side.Children.Add(version);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        if ((item.HasUpdate || item.Updating) && libraryMod != null)
            buttons.Children.Add(BuildUpdateButton(libraryMod, item.Updating));

        if (item.Manifest is { ConfigFields.Count: > 0 })
        {
            Wpf.Ui.Controls.Button settingsBtn = new()
            {
                Icon = SymbolRegular.Settings24,
                ToolTip = Strings.Ui_VoidstrapMods_ModSettings,
                Appearance = ControlAppearance.Secondary,
                Margin = new Thickness(0, 0, 6, 0),
                Tag = mod.Name,
            };
            settingsBtn.Click += ModSettings_Click;
            buttons.Children.Add(settingsBtn);
        }

        Wpf.Ui.Controls.Button publishBtn = new()
        {
            Icon = SymbolRegular.CloudArrowUp24,
            ToolTip = Strings.Ui_VoidstrapMods_PublishTooltip,
            Appearance = ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 6, 0),
            Tag = mod.Name,
        };
        publishBtn.Click += PublishMod_Click;
        if (!invalid)
            buttons.Children.Add(publishBtn);

        Wpf.Ui.Controls.Button iconBtn = new()
        {
            Icon = SymbolRegular.ImageEdit24,
            ToolTip = Strings.Ui_VoidstrapMods_ChangeIcon,
            Appearance = ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 6, 0),
            Tag = mod.Name,
        };
        iconBtn.Click += ChangeIcon_Click;
        if (!invalid)
            buttons.Children.Add(iconBtn);

        Wpf.Ui.Controls.Button exportBtn = new()
        {
            Icon = SymbolRegular.ArrowExportUp24,
            ToolTip = Strings.Ui_VoidstrapMods_ExportTooltip,
            Appearance = ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 6, 0),
            Tag = mod.Name,
        };
        exportBtn.Click += ExportMod_Click;
        if (!invalid)
            buttons.Children.Add(exportBtn);

        Wpf.Ui.Controls.Button deleteBtn = new()
        {
            Icon = SymbolRegular.Delete24,
            ToolTip = Strings.Common_Delete,
            Appearance = ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = mod.Name,
        };
        deleteBtn.Click += DeleteMod_Click;
        buttons.Children.Add(deleteBtn);

        Wpf.Ui.Controls.Button editBtn = new()
        {
            Content = Strings.Ui_VoidstrapMods_EditButton,
            Icon = SymbolRegular.Code24,
            Appearance = ControlAppearance.Secondary,
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = mod.Name,
        };
        editBtn.Click += EditMod_Click;
        buttons.Children.Add(editBtn);

        bool on = mod.Consent == "on";
        Wpf.Ui.Controls.Button toggle = new()
        {
            MinWidth = 84,
            Appearance = on ? ControlAppearance.Secondary : ControlAppearance.Primary,
            Content = on ? Strings.Ui_VoidstrapMods_LibraryDisable : Strings.Ui_VoidstrapMods_LibraryEnable,
            Icon = on ? SymbolRegular.Pause24 : SymbolRegular.Play24,
            Tag = mod.Name,
        };
        toggle.Click += CreateModToggle_Click;
        if (!invalid)
            buttons.Children.Add(toggle);

        side.Children.Add(buttons);
        row.Children.Add(side);

        StackPanel body = new();
        body.Children.Add(row);
        if (mod.Problems.Count > 0)
        {
            System.Windows.Controls.TextBlock problems = new()
            {
                Text = string.Join(Environment.NewLine, mod.Problems.TakeLast(10)),
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono, monospace"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };
            problems.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            StackPanel problemBody = new();
            problemBody.Children.Add(problems);
            Wpf.Ui.Controls.Button clearProblems = new()
            {
                Content = Strings.Ui_VoidstrapMods_ClearProblems,
                Appearance = ControlAppearance.Secondary,
                Margin = new Thickness(0, 8, 0, 0),
                Tag = mod.Name,
            };
            clearProblems.Click += ClearProblems_Click;
            problemBody.Children.Add(clearProblems);
            body.Children.Add(
                new Expander
                {
                    Header = Strings.Ui_VoidstrapMods_RecentProblems,
                    Content = problemBody,
                    IsExpanded = invalid,
                    Margin = new Thickness(0, 8, 0, 0),
                }
            );
        }

        Border card = new() { Child = body };
        card.SetResourceReference(StyleProperty, "RowCard");
        return card;
    }

    private FrameworkElement BuildModIcon(PluginHost.ModInfo mod, ModLibrary.LibraryMod? libraryMod, BitmapSource? icon)
    {
        if (icon != null)
            return new Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8),
            };
        if (libraryMod != null && _libraryIcons.TryGetValue(libraryMod.Id, out BitmapSource? libIcon) && libIcon != null)
            return new Image
            {
                Source = libIcon,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8),
            };
        return new SymbolIcon
        {
            Symbol = SymbolRegular.PuzzlePiece24,
            FontSize = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void NewMod_Click(object sender, RoutedEventArgs e)
    {
        Voidstrap.UI.Elements.Dialogs.CreateVoidstrapModDialog dialog = new();
        if (dialog.ShowOwnedDialog() != true || dialog.CreatedMod is not string name)
            return;
        PluginHost.SetEnabled(name, true);
        OpenModInEditor(name);
    }

    private void ModSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string modName } && PluginManifest.Read(Path.Combine(Paths.Plugins, modName)) is { } manifest)
            PluginOptions.ShowEditor(manifest);
    }

    private void ClearProblems_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string modName })
            PluginHost.ClearProblems(modName);
    }

    private void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string modName })
            return;
        Microsoft.Win32.OpenFileDialog picker = new() { Filter = Strings.Ui_VoidstrapMods_IconFilter, CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            if (Voidstrap.UI.Elements.Dialogs.CreateVoidstrapModDialog.LoadIcon(picker.FileName) == null)
                throw new InvalidDataException(Strings.Ui_VoidstrapMods_IconFailed);
            ModFiles.SetIcon(modName, picker.FileName);
            RenderMods();
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
        }
    }

    private void ExportMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string modName })
            return;
        Microsoft.Win32.SaveFileDialog picker = new()
        {
            Filter = Strings.Ui_VoidstrapMods_PackageFilter,
            FileName = modName + ModFiles.PackageExtension,
            DefaultExt = ModFiles.PackageExtension,
            AddExtension = true,
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            ModFiles.Export(modName, picker.FileName);
            PlatformShell.TryOpenFolder(Path.GetDirectoryName(picker.FileName)!);
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(string.Format(Strings.Ui_VoidstrapMods_ExportFailed, ex.Message), MessageBoxImage.Error);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog picker = new()
        {
            Filter = Strings.Ui_VoidstrapMods_PackageFilter,
            CheckFileExists = true,
            Multiselect = true,
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true)
            return;
        ImportPackages(picker.FileNames);
    }

    private static bool IsPackageFile(string path) =>
        path.EndsWith(ModFiles.PackageExtension, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects =
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsPackageFile)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            ImportPackages(files.Where(IsPackageFile).ToArray());
        e.Handled = true;
    }

    private async void ImportPackages(string[] files)
    {
        string? last = null;
        foreach (string file in files)
        {
            try
            {
                ModFiles.Package package = await Task.Run(() => ModFiles.OpenPackage(file));
                string name = package.Manifest.Name;
                if (
                    Directory.Exists(Path.Combine(Paths.Plugins, name))
                    && Frontend.ShowMessageBox(
                        string.Format(Strings.Ui_VoidstrapMods_ImportReplacePrompt, name),
                        MessageBoxImage.Question,
                        MessageBoxButton.YesNo,
                        MessageBoxResult.No
                    ) != MessageBoxResult.Yes
                )
                {
                    ModFiles.TryDelete(package.StagingFolder);
                    continue;
                }
                await Task.Run(() => ModFiles.Install(package));
                App.Logger.WriteLine("VoidstrapMods", "Imported " + name + " from a mod file");
                last = name;
            }
            catch (Exception ex)
            {
                Frontend.ShowMessageBox(
                    string.Format(Strings.Ui_VoidstrapMods_ImportFailed, Path.GetFileName(file), ex.Message),
                    MessageBoxImage.Error
                );
            }
        }
        if (last != null)
            await Task.Run(PluginHost.Reload);
        try
        {
            RenderMods();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("VoidstrapMods", "The mod list could not be rendered: " + ex.Message);
        }
    }

    private CancellationTokenSource? _libraryCts;

    private readonly System.Windows.Threading.DispatcherTimer _libraryPoll = new() { Interval = TimeSpan.FromSeconds(120) };

    private ModLibrary.Index? _libraryIndex;

    private static readonly int[] IconRetryDelaysSeconds = [20, 65, 120];

    private readonly Dictionary<string, Border> _libraryIconHosts = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, BitmapSource?> _libraryIcons = new(StringComparer.Ordinal);

    private ModLibrary.LibraryMod? _detailMod;

    private bool _sortReady;

    private readonly HashSet<string> _reportedTakedowns = new(StringComparer.Ordinal);

    private void PopulateSorts()
    {
        if (_sortReady)
            return;
        _sortReady = true;
        (ModLibrarySearch.Sort Sort, string Label)[] sorts =
        [
            (ModLibrarySearch.Sort.Featured, Strings.Ui_VoidstrapMods_LibrarySortFeatured),
            (ModLibrarySearch.Sort.Relevance, Strings.Ui_VoidstrapMods_LibrarySortRelevance),
            (ModLibrarySearch.Sort.Newest, Strings.Ui_VoidstrapMods_LibrarySortNewest),
            (ModLibrarySearch.Sort.Updated, Strings.Ui_VoidstrapMods_LibrarySortUpdated),
            (ModLibrarySearch.Sort.Name, Strings.Ui_VoidstrapMods_LibrarySortName),
        ];
        foreach ((ModLibrarySearch.Sort sort, string label) in sorts)
            SortBox.Items.Add(new ComboBoxItem { Content = label, Tag = sort });
        SortBox.SelectedIndex = 0;
    }

    private ModLibrarySearch.Sort? SelectedSort
    {
        get
        {
            ModLibrarySearch.Sort sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag is ModLibrarySearch.Sort chosen
                ? chosen
                : ModLibrarySearch.Sort.Featured;
            return sort == ModLibrarySearch.Sort.Featured && ModLibrarySearch.Parse(LibrarySearchBox.Text ?? "").Words.Count > 0
                ? null
                : sort;
        }
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderLibrary();

    private static Border MakeBadge(string text, SymbolRegular icon, Color color, string tooltip)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(
            new SymbolIcon
            {
                Symbol = icon,
                FontSize = 12,
                Margin = new Thickness(0, 0, 4, 0),
                Foreground = new SolidColorBrush(color),
            }
        );
        content.Children.Add(
            new System.Windows.Controls.TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
            }
        );
        return new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
            ToolTip = tooltip,
        };
    }

    private void PublishMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string modName })
            ShowPublishDialog(modName, null);
    }

    public static void ShowPublishDialog(string modName, string? notes)
    {
        new Voidstrap.UI.Elements.Dialogs.PublishModDialog(modName, notes).ShowOwnedDialog();
    }

    private void RenderAccount()
    {
        GitHubPublisher.Account? account = GitHubPublisher.SignedIn;
        AccountButton.Content = account != null ? "@" + account.Login : Strings.Ui_VoidstrapMods_SignIn;
        AccountButton.ToolTip = account != null ? Strings.Ui_VoidstrapMods_SignOut : Strings.Ui_VoidstrapMods_SignInHint;
        AccountButton.Visibility = GitHubPublisher.IsConfigured || account != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (GitHubPublisher.SignedIn != null)
        {
            string message = string.Format(Strings.Ui_VoidstrapMods_SignOutPrompt, GitHubPublisher.SignedIn.Login);
            if (
                Frontend.ShowMessageBox(message, MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No)
                != MessageBoxResult.Yes
            )
                return;
            GitHubPublisher.SignOut();
        }
        else
        {
            Voidstrap.UI.Elements.Dialogs.GitHubSignInDialog.EnsureSignedIn(Window.GetWindow(this));
        }
        RenderAccount();
    }

    private void LibraryRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadLibraryAsync(true);

    private void LibraryOpenGitHub_Click(object sender, RoutedEventArgs e) => Utilities.OpenWebLink(ModLibrary.RepositoryUrl);

    private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e) => RenderLibrary();

    private async Task LoadLibraryAsync(bool refresh)
    {
        _libraryCts?.Cancel();
        _libraryCts?.Dispose();
        _libraryCts = new CancellationTokenSource();
        CancellationToken token = _libraryCts.Token;
        ModLibrary.Index? known = _libraryIndex ?? await Task.Run(ModLibrary.GetLastKnownIndex);
        if (token.IsCancellationRequested)
            return;
        Task knownIcons = Task.CompletedTask;
        if (known != null)
        {
            _libraryIndex = known;
            PopulateSorts();
            RenderLibrary();
            knownIcons = LoadLibraryIconsAsync(known, token);
            RenderMods();
        }
        else
        {
            LibraryStatusText.Text = Strings.Ui_VoidstrapMods_LibraryLoading;
            LibraryEmpty.Visibility = Visibility.Visible;
        }
        try
        {
            ModLibrary.Index fresh = await ModLibrary.GetIndexAsync(refresh, token);
            bool changed = known == null || !string.Equals(known.Commit, fresh.Commit, StringComparison.Ordinal);
            _libraryIndex = fresh;
            if (changed)
            {
                PopulateSorts();
                RenderLibrary();
            }
            ReportTakedowns(fresh);
            await knownIcons;
            if (changed)
                await LoadLibraryIconsAsync(fresh, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (known != null)
            {
                App.Logger.WriteLine("VoidstrapModsPage::LoadLibraryAsync", "Showing the saved library: " + ex.Message);
                return;
            }
            LibraryHost.Children.Clear();
            LibraryStatusText.Text = string.Format(Strings.Ui_VoidstrapMods_LibraryFailed, ex.Message);
            LibraryEmpty.Visibility = Visibility.Visible;
        }
    }

    private async Task LoadLibraryIconsAsync(ModLibrary.Index index, CancellationToken token)
    {
        try
        {
            List<Task<(string Id, BitmapSource? Icon)>> loading = index
                .Mods.Where(m => m.HasIcon && !_libraryIcons.ContainsKey(m.Id))
                .Select(mod => LoadLibraryIconAsync(index, mod, token))
                .ToList();
            for (int attempt = 0; attempt < IconRetryDelaysSeconds.Length + 1; attempt++)
            {
                while (loading.Count > 0)
                {
                    Task<(string Id, BitmapSource? Icon)> finished = await Task.WhenAny(loading);
                    loading.Remove(finished);
                    (string id, BitmapSource? icon) = await finished;
                    if (icon != null)
                    {
                        _libraryIcons[id] = icon;
                        if (_libraryIconHosts.TryGetValue(id, out Border? host) && index.Mods.FirstOrDefault(m => m.Id == id) is { } shownMod)
                            host.Child = BuildIcon(shownMod, 8, 34);
                    }
                }
                RenderMods();
                List<ModLibrary.LibraryMod> missing = index.Mods.Where(m => m.HasIcon && !_libraryIcons.ContainsKey(m.Id)).ToList();
                if (missing.Count == 0 || attempt == IconRetryDelaysSeconds.Length)
                    break;
                await Task.Delay(TimeSpan.FromSeconds(IconRetryDelaysSeconds[attempt]), token);
                loading = missing.Select(mod => LoadLibraryIconAsync(index, mod, token)).ToList();
            }
            RenderMods();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task<(string Id, BitmapSource? Icon)> LoadLibraryIconAsync(ModLibrary.Index index, ModLibrary.LibraryMod mod, CancellationToken token)
    {
        try
        {
            byte[]? bytes = await ModLibrary.GetIconAsync(index, mod, token).ConfigureAwait(false);
            return (mod.Id, bytes == null ? null : await MediaContentFilter.LoadCheckedIconAsync(bytes).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Logger.WriteLine("VoidstrapModsPage::LoadLibraryIconAsync", mod.Name + " icon failed: " + ex.Message);
            return (mod.Id, null);
        }
    }

    private void ReportTakedowns(ModLibrary.Index index)
    {
        IReadOnlyList<(string Name, string Reason)> disabled = ModLibrary.DisableTakenDown(index);
        List<string> lines = disabled
            .Where(d => _reportedTakedowns.Add(d.Name))
            .Select(d => d.Reason.Length > 0 ? d.Name + ": " + d.Reason : d.Name)
            .ToList();
        if (lines.Count == 0)
            return;
        RenderMods();
        Frontend.ShowMessageBox(
            string.Format(Strings.Ui_VoidstrapMods_LibraryTakenDown, string.Join(Environment.NewLine, lines)),
            MessageBoxImage.Warning
        );
    }

    private void RenderLibrary()
    {
        if (_libraryIndex == null)
            return;
        LibraryHost.Children.Clear();
        _libraryIconHosts.Clear();
        List<ModLibrary.LibraryMod> mods = ModLibrarySearch.Run(_libraryIndex.Mods, LibrarySearchBox.Text ?? "", null, SelectedSort);
        LibraryStatusText.Text = Strings.Ui_VoidstrapMods_LibraryEmpty;
        LibraryEmpty.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (ModLibrary.LibraryMod mod in mods)
            LibraryHost.Children.Add(BuildLibraryCard(mod));
        if (_detailMod != null && LibraryDetail.Visibility == Visibility.Visible)
            ShowLibraryDetail(_libraryIndex.Mods.FirstOrDefault(m => m.Id == _detailMod.Id) ?? _detailMod);
    }

    private FrameworkElement BuildIcon(ModLibrary.LibraryMod mod, double margin, double fallbackSize)
    {
        return _libraryIcons.TryGetValue(mod.Id, out BitmapSource? icon) && icon != null
            ? new Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(margin),
            }
            : new SymbolIcon
            {
                Symbol = SymbolRegular.PuzzlePiece24,
                FontSize = fallbackSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
    }

    private static Border MakePowerfulBadge() =>
        MakeBadge(Strings.Ui_VoidstrapMods_PowerfulBadge, SymbolRegular.ShieldError24, Color.FromRgb(232, 72, 85), Strings.Plugins_PowerfulModWarning);

    private static void AddTrustBadges(Panel host, ModLibrary.LibraryMod mod)
    {
        if (mod.IsPowerful)
            host.Children.Add(MakePowerfulBadge());
        if (mod.Verified)
            host.Children.Add(
                MakeBadge(
                    Strings.Ui_VoidstrapMods_LibraryVerified,
                    SymbolRegular.ShieldCheckmark24,
                    Color.FromRgb(46, 204, 113),
                    Strings.Ui_VoidstrapMods_LibraryVerifiedTooltip
                )
            );
        else if (mod.Trusted)
            host.Children.Add(
                MakeBadge(
                    Strings.Ui_VoidstrapMods_LibraryTrusted,
                    SymbolRegular.PersonAvailable24,
                    Color.FromRgb(88, 166, 255),
                    Strings.Ui_VoidstrapMods_LibraryTrustedTooltip
                )
            );
        else
            host.Children.Add(
                MakeBadge(
                    Strings.Ui_VoidstrapMods_LibraryUnverified,
                    SymbolRegular.Warning24,
                    Color.FromRgb(210, 153, 34),
                    Strings.Ui_VoidstrapMods_LibraryUnverifiedTooltip
                )
            );
        if (mod.Featured > 0)
            host.Children.Add(
                MakeBadge(
                    Strings.Ui_VoidstrapMods_LibraryFeatured,
                    SymbolRegular.Star24,
                    Color.FromRgb(241, 196, 15),
                    Strings.Ui_VoidstrapMods_LibraryFeatured
                )
            );
    }

    private static string InstallLabel(ModLibrary.InstallState state) =>
        state switch
        {
            ModLibrary.InstallState.Installed => Strings.Ui_VoidstrapMods_LibraryInstalled,
            ModLibrary.InstallState.UpdateAvailable => Strings.Ui_VoidstrapMods_LibraryUpdate,
            ModLibrary.InstallState.NameTaken => Strings.Ui_VoidstrapMods_LibraryNameTaken,
            _ => Strings.Ui_VoidstrapMods_LibraryInstall,
        };

    private static void ApplyInstallButton(Wpf.Ui.Controls.Button button, ModLibrary.LibraryMod mod)
    {
        ModLibrary.InstallState state = ModLibrary.GetState(mod);
        button.Content = InstallLabel(state);
        button.Icon =
            state == ModLibrary.InstallState.UpdateAvailable ? SymbolRegular.ArrowSync24
            : state == ModLibrary.InstallState.Installed ? SymbolRegular.Checkmark24
            : SymbolRegular.ArrowDownload24;
        button.Appearance = state is ModLibrary.InstallState.NotInstalled or ModLibrary.InstallState.UpdateAvailable
            ? ControlAppearance.Primary
            : ControlAppearance.Secondary;
        button.IsEnabled = state is ModLibrary.InstallState.NotInstalled or ModLibrary.InstallState.UpdateAvailable;
        button.Tag = mod;
    }

    private static System.Windows.Controls.TextBlock StyledText(string style, string text)
    {
        System.Windows.Controls.TextBlock block = new() { Text = text };
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    private Border BuildLibraryCard(ModLibrary.LibraryMod mod)
    {
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Border icon = new() { Child = BuildIcon(mod, 8, 34) };
        _libraryIconHosts[mod.Id] = icon;
        icon.SetResourceReference(StyleProperty, "RowIcon");
        row.Children.Add(icon);

        StackPanel text = new() { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        WrapPanel title = new() { Orientation = Orientation.Horizontal };
        title.Children.Add(StyledText("RowTitle", mod.Name));
        AddTrustBadges(title, mod);
        text.Children.Add(title);
        System.Windows.Controls.TextBlock summary = StyledText("RowSummary", mod.Description);
        summary.MaxHeight = 40;
        text.Children.Add(summary);
        StackPanel meta = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        System.Windows.Controls.TextBlock author = StyledText("RowMeta", "@" + mod.Login);
        author.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        meta.Children.Add(author);
        System.Windows.Controls.TextBlock network = StyledText(
            "RowMeta",
            (
                mod.Network.Count == 0
                    ? Strings.Ui_VoidstrapMods_LibraryNoNetwork
                    : string.Format(Strings.Ui_VoidstrapMods_LibraryNetwork, string.Join(", ", mod.Network))
            ) + (mod.UsesRobloxAccount ? "   " + Strings.Ui_VoidstrapMods_LibraryRobloxAccess : "")
        );
        network.Margin = new Thickness(12, 0, 0, 0);
        meta.Children.Add(network);
        text.Children.Add(meta);
        row.Children.Add(text);

        StackPanel side = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(side, 2);
        StackPanel version = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 10),
        };
        SymbolIcon tagIcon = new()
        {
            Symbol = SymbolRegular.Tag24,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tagIcon.SetResourceReference(SymbolIcon.ForegroundProperty, "TextFillColorTertiaryBrush");
        version.Children.Add(tagIcon);
        System.Windows.Controls.TextBlock versionText = StyledText("RowMeta", mod.Version);
        versionText.Margin = new Thickness(5, 0, 0, 0);
        version.Children.Add(versionText);
        side.Children.Add(version);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Wpf.Ui.Controls.Button view = new()
        {
            Content = Strings.Ui_Mods_View,
            Appearance = ControlAppearance.Secondary,
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = mod,
        };
        view.Click += LibraryView_Click;
        buttons.Children.Add(view);
        if (ModLibrary.GetState(mod) == ModLibrary.InstallState.Installed)
        {
            bool on = IsModOn(mod.Name);
            Wpf.Ui.Controls.Button toggle = new()
            {
                MinWidth = 84,
                Appearance = on ? ControlAppearance.Secondary : ControlAppearance.Primary,
                Content = on ? Strings.Ui_VoidstrapMods_LibraryDisable : Strings.Ui_VoidstrapMods_LibraryEnable,
                Icon = on ? SymbolRegular.Pause24 : SymbolRegular.Play24,
                Tag = mod,
            };
            toggle.Click += LibraryToggle_Click;
            buttons.Children.Add(toggle);
        }
        else
        {
            Wpf.Ui.Controls.Button install = new() { MinWidth = 84 };
            ApplyInstallButton(install, mod);
            install.Click += LibraryInstall_Click;
            buttons.Children.Add(install);
        }
        side.Children.Add(buttons);
        row.Children.Add(side);

        Border card = new() { Child = row };
        card.SetResourceReference(StyleProperty, "RowCard");
        return card;
    }

    private void LibraryView_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ModLibrary.LibraryMod mod })
            ShowLibraryDetail(mod);
    }

    private void ShowLibraryDetail(ModLibrary.LibraryMod mod)
    {
        _detailMod = mod;
        DetailName.Text = mod.Name;
        DetailSummary.Text = mod.Description;
        DetailBadges.Children.Clear();
        AddTrustBadges(DetailBadges, mod);
        if (DetailBadges.Children.Count > 0 && DetailBadges.Children[0] is FrameworkElement first)
            first.Margin = new Thickness(0);
        bool hasIcon = _libraryIcons.TryGetValue(mod.Id, out BitmapSource? icon) && icon != null;
        DetailIcon.Source = hasIcon ? icon : null;
        DetailIconFallback.Visibility = hasIcon ? Visibility.Collapsed : Visibility.Visible;
        DetailNetwork.Text =
            (mod.Network.Count == 0 ? Strings.Ui_VoidstrapMods_LibraryNoNetwork : string.Join(Environment.NewLine, mod.Network))
            + (mod.UsesRobloxAccount ? Environment.NewLine + Strings.Ui_VoidstrapMods_LibraryRobloxAccess : "");
        DetailFiles.Text = string.Join(Environment.NewLine, mod.Files.Select(f => f.Path + "   " + FormatSize(f.Size)));
        DetailVersion.Text = mod.Version;
        DetailUpdated.Text = (mod.Updated ?? mod.Created)?.ToLocalTime().ToString("d") ?? "";
        DetailCreator.Text = "@" + mod.Login;
        DetailTrust.Text =
            mod.Verified ? Strings.Ui_VoidstrapMods_LibraryVerifiedTooltip
            : mod.Trusted ? Strings.Ui_VoidstrapMods_LibraryTrustedTooltip
            : Strings.Ui_VoidstrapMods_LibraryUnverifiedTooltip;
        ApplyInstallButton(DetailInstallButton, mod);
        DetailInstallButton.Height = 40;
        ApplyInstalledActions(mod);
        LibraryBrowse.Visibility = Visibility.Collapsed;
        LibraryDetail.Visibility = Visibility.Visible;
    }

    private static bool IsModOn(string name) => PluginHost.ListMods().FirstOrDefault(m => m.Name == name)?.Consent == "on";

    private void ApplyInstalledActions(ModLibrary.LibraryMod mod)
    {
        ModLibrary.InstallState state = ModLibrary.GetState(mod);
        bool installed = state is ModLibrary.InstallState.Installed or ModLibrary.InstallState.UpdateAvailable;
        DetailInstalledActions.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        DetailInstallButton.Visibility = state == ModLibrary.InstallState.Installed ? Visibility.Collapsed : Visibility.Visible;
        bool on = installed && IsModOn(mod.Name);
        DetailToggleButton.Content = on ? Strings.Ui_VoidstrapMods_LibraryDisable : Strings.Ui_VoidstrapMods_LibraryEnable;
        DetailToggleButton.Icon = on ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        DetailToggleButton.Tag = mod;
        DetailUninstallButton.Tag = mod;
    }

    private async void LibraryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModLibrary.LibraryMod mod })
            return;
        await ToggleModAsync(mod.Name);
    }

    private async Task ToggleModAsync(string modName)
    {
        try
        {
            PluginHost.SetEnabled(modName, !IsModOn(modName), reload: false);
            await Task.Run(PluginHost.Reload);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("VoidstrapModsPage::ToggleModAsync", ex);
        }
        RenderMods();
        RenderLibrary();
    }

    private async void LibraryUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModLibrary.LibraryMod mod })
            return;
        if (
            Frontend.ShowMessageBox(
                string.Format(Strings.Ui_VoidstrapMods_DeleteModPrompt, mod.Name),
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        try
        {
            await Task.Run(() => PluginHost.DeleteMod(mod.Name));
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
        }
        RenderMods();
        RenderLibrary();
    }

    private bool _compactDetail;

    private void LibraryDetail_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 720.0;
        if (compact == _compactDetail)
            return;
        _compactDetail = compact;
        LibraryDetailSideColumn.Width = new GridLength(compact ? 0.0 : 280.0);
        Grid.SetRow(LibraryDetailSidebar, compact ? 1 : 0);
        Grid.SetColumn(LibraryDetailSidebar, compact ? 0 : 1);
        LibraryDetailMain.Margin = compact ? new Thickness(0.0) : new Thickness(0.0, 0.0, 20.0, 0.0);
        LibraryDetailSidebar.Margin = compact ? new Thickness(0.0, 20.0, 0.0, 0.0) : new Thickness(0.0, 38.0, 0.0, 0.0);
    }

    private static string FormatSize(long bytes) => bytes < 1024 ? bytes + " B" : (bytes / 1024.0).ToString("0.#") + " KB";

    private void LibraryBack_Click(object sender, RoutedEventArgs e)
    {
        _detailMod = null;
        LibraryDetail.Visibility = Visibility.Collapsed;
        LibraryBrowse.Visibility = Visibility.Visible;
    }

    private void DetailGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (_detailMod != null)
            Utilities.OpenWebLink(ModLibrary.RepositoryUrl + "/tree/main/mods/" + Uri.EscapeDataString(_detailMod.Name));
    }

    private async void LibraryInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { Tag: ModLibrary.LibraryMod mod } button || _libraryIndex is not { } index)
            return;
        bool vouched = mod.Verified || mod.Trusted;
        if (!vouched)
        {
            string network =
                mod.Network.Count == 0
                    ? Strings.Ui_VoidstrapMods_LibraryNoNetwork
                    : string.Format(Strings.Ui_VoidstrapMods_LibraryNetwork, string.Join(", ", mod.Network));
            if (
                Frontend.ShowMessageBox(
                    string.Format(Strings.Ui_VoidstrapMods_LibraryUnverifiedPrompt, mod.Name, mod.Version, mod.Login, network),
                    MessageBoxImage.Warning,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.No
                ) != MessageBoxResult.Yes
            )
                return;
        }
        button.IsEnabled = false;
        try
        {
            await ModLibrary.InstallAsync(index, mod, CancellationToken.None);
            if (vouched)
                PluginHost.SetEnabled(mod.Name, true, reload: false);
            await Task.Run(PluginHost.Reload);
            RenderMods();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(
                string.Format(Strings.Ui_VoidstrapMods_LibraryInstallFailed, mod.Name, ex.Message),
                MessageBoxImage.Error
            );
        }
        RenderLibrary();
    }

    private readonly Dictionary<string, RotateTransform> _updateSpins = new(StringComparer.Ordinal);

    private Wpf.Ui.Controls.Button BuildUpdateButton(ModLibrary.LibraryMod mod, bool updating)
    {
        SymbolIcon icon = new()
        {
            Symbol = SymbolRegular.ArrowSync24,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        if (updating && _updateSpins.TryGetValue(mod.Name, out RotateTransform? spin))
            icon.RenderTransform = spin;
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(
            new System.Windows.Controls.TextBlock
            {
                Text = Strings.Ui_VoidstrapMods_LibraryUpdate,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        Wpf.Ui.Controls.Button button = new()
        {
            Content = content,
            Appearance = ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 6, 0),
            IsHitTestVisible = !updating,
            Tag = mod,
        };
        button.Click += ModUpdate_Click;
        return button;
    }

    private async void ModUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (
            sender is not Wpf.Ui.Controls.Button { Tag: ModLibrary.LibraryMod mod, Content: StackPanel content } button
            || _libraryIndex is not { } index
            || _updateSpins.ContainsKey(mod.Name)
        )
            return;
        PluginHost.ModInfo? installed = PluginHost.ListMods().FirstOrDefault(info => info.Name == mod.Name);
        PluginManifest? manifest = null;
        if (installed != null)
            PluginManifest.TryRead(installed.Directory, out manifest, out _);
        bool asksForMore =
            mod.Network.Any(host => manifest == null || !manifest.NetworkHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            || (mod.UsesRobloxAccount && manifest?.HasPermission(PluginManifest.RobloxAccountPermission) != true);
        if (asksForMore || !(mod.Verified || mod.Trusted))
        {
            string network =
                mod.Network.Count == 0
                    ? Strings.Ui_VoidstrapMods_LibraryNoNetwork
                    : string.Format(Strings.Ui_VoidstrapMods_LibraryNetwork, string.Join(", ", mod.Network));
            if (
                Frontend.ShowMessageBox(
                    string.Format(Strings.Ui_VoidstrapMods_LibraryUnverifiedPrompt, mod.Name, mod.Version, mod.Login, network),
                    MessageBoxImage.Warning,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.No
                ) != MessageBoxResult.Yes
            )
                return;
        }
        RotateTransform spin = new();
        _updateSpins[mod.Name] = spin;
        button.IsHitTestVisible = false;
        if (content.Children[0] is SymbolIcon icon)
            icon.RenderTransform = spin;
        spin.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever }
        );
        try
        {
            await ModLibrary.InstallAsync(index, mod, CancellationToken.None);
            await Task.Run(PluginHost.Reload);
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(
                string.Format(Strings.Ui_VoidstrapMods_LibraryInstallFailed, mod.Name, ex.Message),
                MessageBoxImage.Error
            );
        }
        const int settle = 400;
        double angle = spin.Angle;
        spin.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(angle, angle + (360 - angle % 360), TimeSpan.FromMilliseconds(settle))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            }
        );
        await Task.Delay(settle);
        spin.BeginAnimation(RotateTransform.AngleProperty, null);
        _updateSpins.Remove(mod.Name);
        RenderMods();
        RenderLibrary();
    }

    private void EditMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string modName })
            OpenModInEditor(modName);
    }

    private void ModCode_Click(object sender, RoutedEventArgs e)
    {
        new Voidstrap.UI.Elements.Editor.VoidstrapModEditorWindow().ShowOwnedDialog();
    }

    private async void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string modName })
            return;
        if (
            Frontend.ShowMessageBox(
                string.Format(Strings.Ui_VoidstrapMods_DeleteModPrompt, modName),
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        try
        {
            await Task.Run(() => PluginHost.DeleteMod(modName));
            RenderMods();
            RenderLibrary();
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
        }
    }

    private async void CreateModToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string modName })
            return;
        await ToggleModAsync(modName);
    }

    public void OpenModInEditor(string modName)
    {
        if (Directory.Exists(Path.Combine(Paths.Plugins, modName)))
            new Voidstrap.UI.Elements.Editor.VoidstrapModEditorWindow(modName).ShowOwnedDialog();
    }

    private void ConnectAi_Click(object sender, RoutedEventArgs e) => Voidstrap.Integrations.Mcp.McpConnectInfo.Show();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(PluginHost.ReloadNow);
    }

    private void OnPanesChanged() => Dispatcher.BeginInvoke(new Action(RenderAll));

    private void OnPaneInvalidated(string plugin) => Dispatcher.BeginInvoke(new Action(RenderAll));

    private void OnStatusChanged() => Dispatcher.BeginInvoke(new Action(RenderStatus));

    private void RenderStatus()
    {
        StatusText.Text = string.Join("   ", PluginHost.StatusLines);
    }

    private int _panesGeneration;

    private async void RenderAll()
    {
        int generation = ++_panesGeneration;
        IReadOnlyList<PluginHost.PaneInfo> panes = PluginHost.Panes;
        ActivePanesHeader.Visibility = panes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        List<(PluginHost.PaneInfo Pane, JsonNode? Tree)> rendered = [];
        foreach (PluginHost.PaneInfo pane in panes)
        {
            JsonNode? tree;
            try
            {
                tree = await Task.Run(() => PluginHost.RenderPane(pane));
            }
            catch (Exception ex)
            {
                PluginHost.Problem(pane.Plugin, "the pane " + pane.Id + " could not be drawn: " + PluginHost.Describe(ex));
                tree = null;
            }
            if (generation != _panesGeneration || Dispatcher.HasShutdownStarted)
                return;
            rendered.Add((pane, tree));
        }
        if (generation != _panesGeneration)
            return;
        PanesHost.Children.Clear();
        foreach (var (pane, tree) in rendered)
        {
            StackPanel body = new();
            body.Children.Add(
                new System.Windows.Controls.TextBlock
                {
                    Text = pane.Title,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8),
                }
            );
            UIElement? content = null;
            try
            {
                string dir = Path.Combine(Paths.Plugins, pane.Plugin);
                content = tree == null ? null : PluginUiRenderer.BuildTree(tree, dir);
            }
            catch (Exception ex)
            {
                PluginHost.Problem(pane.Plugin, "the pane " + pane.Id + " could not be drawn: " + PluginHost.Describe(ex));
            }
            if (content != null)
                body.Children.Add(content);
            Border card = new()
            {
                Child = body,
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            PanesHost.Children.Add(card);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Paths.Plugins);
        PlatformShell.TryOpenFolder(Paths.Plugins);
    }
}
