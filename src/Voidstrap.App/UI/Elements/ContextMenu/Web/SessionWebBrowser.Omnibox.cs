using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Voidstrap.UI.Elements.Overlay;

// The address bar: search or an address, !bangs that turn into a chip, the site you type finished for you
// from where you have been, and suggestions from your tabs, your history and the search engine.
// Also the tab search and the find bar, which work the same way.
public partial class SessionWebBrowser
{
    private static readonly Regex BangPrefix = new(@"^!(\S+)\s", RegexOptions.Compiled);

    private DispatcherTimer? _suggestTimer;
    private CancellationTokenSource? _suggestCancel;
    private IReadOnlyList<string> _webSuggestions = Array.Empty<string>();
    private List<Suggestion> _suggestions = new();
    private List<Suggestion> _tabResults = new();
    private int _selected;
    private int _tabSelected;
    private Bang? _keyword;
    private bool _settingText;
    private int _lastTypedLength;
    private DateTime _tabSearchClosed;

    private void InitializeOmnibox()
    {
        _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _suggestTimer.Tick += async (_, _) =>
        {
            _suggestTimer.Stop();
            string typed = TypedText().Trim();
            if (!Settings.Suggestions || typed.Length == 0 || typed.StartsWith('!') || (_keyword == null && BrowserSearch.LooksLikeAddress(typed)))
                return;
            _suggestCancel?.Cancel();
            CancellationTokenSource cancel = new();
            _suggestCancel = cancel;
            string engine = _keyword != null ? "google" : Settings.Engine;
            IReadOnlyList<string> results = await BrowserSearch.SuggestAsync(typed, engine, cancel.Token);
            if (cancel.IsCancellationRequested || _closed || !AddressBox.IsKeyboardFocused || TypedText().Trim() != typed)
                return;
            _webSuggestions = results;
            BuildSuggestions();
        };
    }

    private void StopOmnibox()
    {
        _suggestTimer?.Stop();
        _suggestCancel?.Cancel();
    }

    private void FocusAddress()
    {
        RevealChrome();
        AddressBox.Focus();
        Keyboard.Focus(AddressBox);
        AddressBox.SelectAll();
    }

    private void RevealChrome()
    {
        if (Settings.ZenMode && !_zenRevealed && !_pageFullscreen)
        {
            _zenRevealed = true;
            ApplyChromeVisibility();
        }
    }

    // ---- What the address bar shows ----

    // The words of a search, the site alone when that option is on, otherwise the address without its https://
    private void ShowAddress()
    {
        BrowserTab? tab = _active;
        string url = tab == null || tab.IsNewTab ? string.Empty : tab.Core?.Source is { Length: > 0 } source && IsWebAddress(source) ? source : tab.Url;
        string shown = string.Empty;
        string glyph = "\uE721";
        if (url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            if (BrowserSearch.SearchedFor(url) is { } words)
            {
                shown = words;
            }
            else
            {
                glyph = uri.Scheme == Uri.UriSchemeHttps ? "\uE72E" : "\uE785";
                string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
                shown = Settings.MinimalAddress ? host : Pretty(uri);
            }
        }
        ClearKeyword();
        AddressIcon.Text = glyph;
        AddressIcon.ToolTip = glyph == "\uE72E" ? "Secure connection" : glyph == "\uE785" ? "Not secure, this page is not encrypted" : null;
        SetAddressText(shown);
    }

    private static string Pretty(Uri uri)
    {
        string text = uri.AbsoluteUri;
        if (uri.Scheme == Uri.UriSchemeHttps)
            text = text["https://".Length..];
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            text = text[4..];
        return uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? text.TrimEnd('/') : text;
    }

    private void SetAddressText(string text)
    {
        _settingText = true;
        AddressBox.Text = text;
        _settingText = false;
        _lastTypedLength = text.Length;
        UpdatePlaceholder();
    }

    private void UpdatePlaceholder()
    {
        Placeholder.Visibility = AddressBox.Text.Length == 0 && _keyword == null ? Visibility.Visible : Visibility.Collapsed;
        Placeholder.Text = $"Search {BrowserSearch.Engine(Settings.Engine).Name} or type an address";
    }

    // What was typed, without the part the address bar filled in for you
    private string TypedText()
    {
        string text = AddressBox.Text;
        if (AddressBox.SelectionLength > 0 && AddressBox.SelectionStart + AddressBox.SelectionLength == text.Length)
            return text[..AddressBox.SelectionStart];
        return text;
    }

    // ---- Bang chip ----

    private void SetKeyword(Bang bang)
    {
        _keyword = bang;
        KeywordText.Text = bang.Name;
        KeywordGlyph.Text = bang.Kind == BangKind.Ask ? "\uE8BD" : "\uE721";
        KeywordChip.Visibility = Visibility.Visible;
        _webSuggestions = Array.Empty<string>();
        UpdatePlaceholder();
    }

    private void ClearKeyword()
    {
        if (_keyword == null)
            return;
        _keyword = null;
        KeywordChip.Visibility = Visibility.Collapsed;
        UpdatePlaceholder();
    }

    // ---- Typing ----

    private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // The whole address while editing, so it can be copied or changed
        if (_active is { IsNewTab: false } tab && BrowserSearch.SearchedFor(tab.Url) == null)
        {
            string url = tab.Core?.Source is { Length: > 0 } source && IsWebAddress(source) ? source : tab.Url;
            SetAddressText(url);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(AddressBox.SelectAll));
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (AddressBox.IsKeyboardFocused || _closed)
                return;
            SuggestPopup.IsOpen = false;
            _suggestCancel?.Cancel();
            ShowAddress();
            if (Settings.ZenMode)
                _zenTimer?.Start();
        }));
    }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
        if (_settingText || !AddressBox.IsKeyboardFocused)
            return;
        string text = AddressBox.Text;
        // "!yt " turns into a YouTube chip and the rest is what gets searched there
        if (_keyword == null && BangPrefix.Match(text) is { Success: true } match && BrowserSearch.FindBang(match.Groups[1].Value) is { } bang)
        {
            SetKeyword(bang);
            SetAddressText(text[match.Length..]);
            AddressBox.CaretIndex = AddressBox.Text.Length;
            text = AddressBox.Text;
        }
        bool grew = text.Length > _lastTypedLength;
        _lastTypedLength = text.Length;
        if (grew && _keyword == null && AddressBox.CaretIndex == text.Length)
            CompleteInline(text);
        BuildSuggestions();
        _suggestTimer?.Stop();
        _suggestTimer?.Start();
    }

    // Typing the start of a site you have been to finishes it, the added part selected so typing on replaces it
    private void CompleteInline(string typed)
    {
        if (typed.Length < 2 || typed.Contains(' ') || typed.Contains('/') || typed.StartsWith('!'))
            return;
        string? completion = _state.History
            .Select(entry => Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? uri) ? (Host: uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host, Score: entry.Score()) : (Host: string.Empty, Score: 0))
            .Where(item => item.Host.Length > typed.Length && item.Host.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Host, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Sum(item => item.Score))
            .Select(group => group.Key)
            .FirstOrDefault();
        if (completion == null)
            return;
        SetAddressText(typed + completion[typed.Length..]);
        _lastTypedLength = typed.Length;
        AddressBox.Select(typed.Length, completion.Length - typed.Length);
    }

    private void BuildSuggestions()
    {
        string typed = TypedText().Trim();
        List<Suggestion> list = new();
        SearchEngine engine = BrowserSearch.Engine(Settings.Engine);
        if (_keyword is { } bang)
        {
            string verb = bang.Kind == BangKind.Ask ? "Ask" : "Search";
            list.Add(new Suggestion(bang.Kind == BangKind.Ask ? "\uE8BD" : "\uE721", typed.Length == 0 ? bang.Name : typed,
                typed.Length == 0 ? "Open the site" : $"{verb} {bang.Name}", newTab => Go(BrowserSearch.BangUrl(bang, typed), newTab)));
            foreach (string words in _webSuggestions.Take(6))
                list.Add(new Suggestion("\uE721", words, $"{verb} {bang.Name}", newTab => Go(BrowserSearch.BangUrl(bang, words), newTab)));
        }
        else if (typed.Length > 0)
        {
            string full = AddressBox.Text.Trim();
            (Bang? inline, string query) = BrowserSearch.SplitBang(typed);
            if (inline != null)
                list.Add(new Suggestion("\uE721", query.Length == 0 ? inline.Name : query, $"Search {inline.Name}", newTab => Go(BrowserSearch.BangUrl(inline, query), newTab)));
            else if (BrowserSearch.LooksLikeAddress(full) || Uri.TryCreate(full, UriKind.Absolute, out _))
                list.Add(new Suggestion("\uE774", full, "Open address", newTab => Go(BrowserSearch.Resolve(full, Settings.Engine), newTab)));
            else
                list.Add(new Suggestion("\uE721", typed, $"Search {engine.Name}", newTab => Go(BrowserSearch.SearchUrl(typed, Settings.Engine), newTab)));

            // A ! starts a bang: every one that matches, picking one makes the chip
            if (typed.StartsWith('!') && !typed.Contains(' '))
            {
                foreach (Bang match in BrowserSearch.MatchBangs(typed).Take(6))
                {
                    list.Add(new Suggestion(match.Kind == BangKind.Ask ? "\uE8BD" : "\uE721", "!" + match.Trigger,
                        $"{(match.Kind == BangKind.Ask ? "Ask" : "Search")} {match.Name}", _ => PickBang(match)) { KeepsOpen = true });
                }
            }
            else
            {
                HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
                foreach (BrowserTab tab in _tabs.Where(tab => !ReferenceEquals(tab, _active) && !tab.IsNewTab && Matches(tab.Title, tab.Url, typed)).Take(3))
                {
                    listed.Add(tab.Url);
                    list.Add(new Suggestion("\uE8A7", tab.Title, "Switch to this tab", _ => SwitchTo(tab)));
                }
                foreach (HistoryEntry entry in _state.History.Where(entry => !listed.Contains(entry.Url) && Matches(entry.Title, entry.Url, typed))
                    .OrderByDescending(entry => entry.Score()).Take(4))
                {
                    list.Add(new Suggestion("\uE81C", string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title, HostOf(entry.Url),
                        newTab => Go(entry.Url, newTab)) { HistoryUrl = entry.Url });
                }
                foreach (string words in _webSuggestions.Where(words => words.StartsWith(typed, StringComparison.OrdinalIgnoreCase) || typed.Length > 3).Take(Math.Max(0, 9 - list.Count)))
                    list.Add(new Suggestion("\uE721", words, string.Empty, newTab => Go(BrowserSearch.SearchUrl(words, Settings.Engine), newTab)));
            }
        }
        _suggestions = list.Take(9).ToList();
        _selected = 0;
        MarkSelected(_suggestions, _selected);
        SuggestList.ItemsSource = _suggestions;
        if (SuggestPopup.Child is FrameworkElement card)
            card.Width = Math.Max(AddressFrame.ActualWidth, 380);
        SuggestPopup.IsOpen = _suggestions.Count > 0 && AddressBox.IsKeyboardFocused && !_panelHidden;
    }

    private static bool Matches(string title, string url, string typed)
        => title.Contains(typed, StringComparison.OrdinalIgnoreCase) || url.Contains(typed, StringComparison.OrdinalIgnoreCase);

    private void PickBang(Bang bang)
    {
        SetKeyword(bang);
        SetAddressText(string.Empty);
        AddressBox.Focus();
        BuildSuggestions();
    }

    private static void MarkSelected(List<Suggestion> list, int selected)
    {
        for (int i = 0; i < list.Count; i++)
            list[i].IsSelected = i == selected;
    }

    private void Go(string url, bool newTab)
    {
        if (url.Length == 0)
            return;
        SuggestPopup.IsOpen = false;
        ClearKeyword();
        if (newTab)
            OpenInNewTab(url);
        else
            Navigate(url);
        _active?.View?.Focus();
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Down or Key.Up when SuggestPopup.IsOpen && _suggestions.Count > 0:
                _selected = (_selected + (key == Key.Down ? 1 : _suggestions.Count - 1)) % _suggestions.Count;
                MarkSelected(_suggestions, _selected);
                break;
            case Key.Tab when !shift && _keyword == null && BrowserSearch.FindBang(TypedText().Trim()) is { } typedBang && TypedText().Trim().StartsWith('!'):
                PickBang(typedBang);
                break;
            case Key.Tab when SuggestPopup.IsOpen && _suggestions.Count > 0:
                _selected = (_selected + (shift ? _suggestions.Count - 1 : 1)) % _suggestions.Count;
                MarkSelected(_suggestions, _selected);
                break;
            case Key.Enter or Key.Return:
                // Ctrl+Enter wraps a word into www.word.com; Alt+Enter opens in a new tab
                string typed = TypedText().Trim();
                if (control && _keyword == null && typed.Length > 0 && !typed.Contains(' ') && !typed.Contains('.'))
                    Go("https://www." + typed + ".com/", alt);
                else if (SuggestPopup.IsOpen && _selected >= 0 && _selected < _suggestions.Count)
                    Run(_suggestions[_selected], alt);
                else
                    Go(BrowserSearch.Resolve(AddressBox.Text, Settings.Engine, _keyword), alt);
                break;
            case Key.Escape:
                if (SuggestPopup.IsOpen)
                {
                    SuggestPopup.IsOpen = false;
                }
                else
                {
                    ShowAddress();
                    _active?.View?.Focus();
                }
                break;
            case Key.Back when _keyword != null && AddressBox.CaretIndex == 0 && AddressBox.SelectionLength == 0:
                ClearKeyword();
                BuildSuggestions();
                break;
            // Shift+Delete forgets the selected page from your history, like any browser
            case Key.Delete when shift && SuggestPopup.IsOpen && _selected < _suggestions.Count && _suggestions[_selected].HistoryUrl is { } forget:
                _state.History.RemoveAll(entry => entry.Url == forget);
                BuildSuggestions();
                ScheduleSave();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Run(Suggestion suggestion, bool newTab)
    {
        suggestion.Run(newTab);
        if (!suggestion.KeepsOpen)
        {
            SuggestPopup.IsOpen = false;
            TabSearchPopup.IsOpen = false;
        }
    }

    private void Suggestion_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Suggestion suggestion })
            return;
        e.Handled = true;
        Run(suggestion, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    // ---- Tab search ----

    private void TabSearch_Click(object sender, RoutedEventArgs e)
    {
        // A click on the button while the popup is open closes it first, that click should not open it again
        if (TabSearchPopup.IsOpen || DateTime.UtcNow - _tabSearchClosed < TimeSpan.FromMilliseconds(250))
            TabSearchPopup.IsOpen = false;
        else
            OpenTabSearch();
    }

    private void OpenTabSearch()
    {
        RevealChrome();
        TabSearchBox.Text = string.Empty;
        BuildTabSearch();
        TabSearchPopup.IsOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            TabSearchBox.Focus();
            Keyboard.Focus(TabSearchBox);
        }));
    }

    private void BuildTabSearch()
    {
        string query = TabSearchBox.Text.Trim();
        TabSearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        List<Suggestion> list = new();
        foreach (BrowserTab tab in _tabs.Where(tab => query.Length == 0 || Matches(tab.Title, tab.Url, query)))
        {
            string detail = tab.Host + (tab.IsActive ? " · Open now" : tab.IsHibernated ? " · Asleep" : tab.IsAudible ? " · Playing" : string.Empty);
            list.Add(new Suggestion(tab.IsNewTab ? "\uE710" : tab.IsPinned ? "\uE840" : "\uE774", tab.Title, detail, _ => SwitchTo(tab)));
        }
        foreach (SavedTab saved in _state.Closed.Where(saved => query.Length == 0 || Matches(saved.Title, saved.Url, query)).Take(8).ToList())
            list.Add(new Suggestion("\uE81C", string.IsNullOrWhiteSpace(saved.Title) ? HostOf(saved.Url) : saved.Title, "Recently closed", _ => ReopenTab(saved)));
        _tabResults = list;
        _tabSelected = 0;
        MarkSelected(_tabResults, _tabSelected);
        TabSearchList.ItemsSource = _tabResults;
    }

    private void TabSearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildTabSearch();

    private void TabSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up when _tabResults.Count > 0:
                _tabSelected = (_tabSelected + (e.Key == Key.Down ? 1 : _tabResults.Count - 1)) % _tabResults.Count;
                MarkSelected(_tabResults, _tabSelected);
                break;
            case Key.Enter or Key.Return when _tabSelected < _tabResults.Count:
                Run(_tabResults[_tabSelected], false);
                break;
            case Key.Escape:
                TabSearchPopup.IsOpen = false;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void TabSearchPopup_Closed(object? sender, EventArgs e)
    {
        _tabSearchClosed = DateTime.UtcNow;
        if (Settings.ZenMode)
            _zenTimer?.Start();
    }

    // ---- Find on page ----

    private void HookFind(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            core.Find.MatchCountChanged += (_, _) => Fenced(() => UpdateFindCount(tab));
            core.Find.ActiveMatchIndexChanged += (_, _) => Fenced(() => UpdateFindCount(tab));
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Find on page is not available: " + ex.Message);
        }
    }

    private void OpenFind()
    {
        if (_active is not { Core: not null })
            return;
        RevealChrome();
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        Keyboard.Focus(FindBox);
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0)
            StartFind();
    }

    private void CloseFind(bool focusPage)
    {
        if (FindBar.Visibility != Visibility.Visible)
            return;
        FindBar.Visibility = Visibility.Collapsed;
        FindCount.Text = string.Empty;
        foreach (BrowserTab tab in _tabs.Where(tab => tab.Core != null))
            Fenced(() => tab.Core!.Find.Stop());
        if (focusPage)
            _active?.View?.Focus();
    }

    private async void StartFind()
    {
        try
        {
            CoreWebView2? core = _active?.Core;
            CoreWebView2Environment? environment = _environment;
            if (core == null || environment == null)
                return;
            string term = FindBox.Text;
            if (term.Length == 0)
            {
                core.Find.Stop();
                FindCount.Text = string.Empty;
                return;
            }
            CoreWebView2FindOptions options = environment.CreateFindOptions();
            options.FindTerm = term;
            options.SuppressDefaultFindDialog = true;
            options.ShouldHighlightAllMatches = true;
            await core.Find.StartAsync(options);
            if (_active != null)
                UpdateFindCount(_active);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Find on page failed: " + ex.Message);
        }
    }

    private void UpdateFindCount(BrowserTab tab)
    {
        if (!ReferenceEquals(tab, _active) || tab.Core == null || FindBar.Visibility != Visibility.Visible)
            return;
        int count = tab.Core.Find.MatchCount;
        int index = tab.Core.Find.ActiveMatchIndex;
        FindCount.Text = FindBox.Text.Length == 0 ? string.Empty : count <= 0 ? "No matches" : $"{Math.Max(index, 1)} of {count}";
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindHint.Visibility = FindBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartFind();
    }

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter or Key.Return:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    FindStep(false);
                else
                    FindStep(true);
                break;
            case Key.Escape:
                CloseFind(true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void FindStep(bool forward) => Fenced(() =>
    {
        if (_active?.Core is not { } core)
            return;
        if (forward)
            core.Find.FindNext();
        else
            core.Find.FindPrevious();
    });

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(true);

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindStep(false);

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind(true);
}

// One row under the address bar or in tab search
public sealed class Suggestion : INotifyPropertyChanged
{
    private bool _isSelected;

    public Suggestion(string glyph, string text, string detail, Action<bool> run)
    {
        Glyph = glyph;
        Text = text;
        Detail = detail;
        Run = run;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Glyph { get; }
    public string Text { get; }
    public string Detail { get; }
    public Action<bool> Run { get; }
    public bool KeepsOpen { get; init; }
    public string? HistoryUrl { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
