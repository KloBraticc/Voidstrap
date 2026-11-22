using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.UI.Elements.Dialogs;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Overlay;

internal sealed record SessionDockActionDefinition(
    string Key,
    Func<string> Label,
    string DefaultGlyph,
    string DefaultSymbol,
    bool CanHide = true
);

public partial class SessionDock
{
    internal static IReadOnlyList<SessionDockActionDefinition> DockActionDefinitions { get; } =
    [
        new("chat", () => Strings.Ui_Session_ChatWithFriends, "\uE8BD", "Chat24"),
        new("browser", () => Strings.Ui_Session_Servers, "\uE753", "Globe24"),
        new("games", () => Strings.Ui_Session_Games, "\uE7FC", "Games24"),
        new("history", () => Strings.ContextMenu_GameHistory_Title, "\uE81C", "History24"),
        new("music", () => Strings.Ui_Session_Music, "\uE8D6", "MusicNote224"),
        new("details", () => Strings.Ui_SessionNotify_ServerDetails, "\uE968", "Info24"),
        new("reviews", () => Strings.Ui_Session_CommunityRating, "\uE734", "Star24"),
        new("web", () => Strings.Ui_Session_Browser, "\uE774", "Globe24"),
        new("clips", () => Strings.Ui_Clipper_Title, "\uE714", "VideoClip24"),
        new("layout", () => Strings.Ui_Session_OverlayLayout, "\uE759", "Apps24", false),
        new("adjustments", () => Strings.Ui_Nav_Settings, "\uE713", "Settings24", false),
    ];

    private readonly Dictionary<string, object?> _defaultDockActionContents = new(StringComparer.Ordinal);

    internal static IReadOnlyList<string> GetDockActionOrder(OverlayLayoutSettings layout)
    {
        HashSet<string> validKeys = new HashSet<string>(DockActionDefinitions.Select(item => item.Key), StringComparer.Ordinal);
        HashSet<string> added = new HashSet<string>(StringComparer.Ordinal);
        List<string> order = new List<string>();
        foreach (string key in layout.DockActionOrder ?? [])
        {
            if (validKeys.Contains(key) && added.Add(key))
                order.Add(key);
        }
        foreach (SessionDockActionDefinition definition in DockActionDefinitions)
        {
            if (added.Add(definition.Key))
                order.Add(definition.Key);
        }
        return order;
    }

    private void InitializeDockActionLayout()
    {
        Resources[SidebarIconPickerDialog.IconFontResourceKey] = SidebarIconPickerDialog.FullIconFont;
        foreach (SessionDockActionDefinition definition in DockActionDefinitions)
        {
            if (FindDockActionButton(definition.Key) is { } button)
                _defaultDockActionContents[definition.Key] = button.Content;
        }
        RefreshDockActionLayoutLive();
    }

    internal void RefreshDockActionLayoutLive()
    {
        if (_closed)
            return;

        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        layout.DockHiddenActions ??= [];
        layout.DockActionIcons ??= [];
        layout.DockActionIconImages ??= [];
        ApplyDockActionOrder(layout);
        foreach (SessionDockActionDefinition definition in DockActionDefinitions)
        {
            Wpf.Ui.Controls.Button? button = FindDockActionButton(definition.Key);
            if (button == null)
                continue;

            bool visible = IsDockActionAvailable(definition.Key)
                && (!definition.CanHide || !layout.DockHiddenActions.Contains(definition.Key, StringComparer.Ordinal));
            button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible && _panels.TryGetValue(PanelKeyFor(definition.Key), out SessionPanelWindow? panel) && panel.IsRequested)
                panel.CloseFromDock();

            ApplyDockActionIcon(button, definition, layout);
        }
        UpdateActiveButtons();
        QueueFitWidth();
    }

    internal void RefreshDockActionVisibilityLive(string key)
    {
        if (_closed || FindDockActionButton(key) is not { } button)
            return;
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        SessionDockActionDefinition? definition = DockActionDefinitions.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        if (definition == null)
            return;

        bool visible = IsDockActionAvailable(key)
            && (!definition.CanHide || !(layout.DockHiddenActions ?? []).Contains(key, StringComparer.Ordinal));
        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && _panels.TryGetValue(PanelKeyFor(key), out SessionPanelWindow? panel) && panel.IsRequested)
            panel.CloseFromDock();
        QueueFitWidth();
        UpdateActiveButtons();
    }

    private static bool IsDockActionAvailable(string key)
    {
        return key switch
        {
            "web" => Voidstrap.Utility.Platform.IsWindows,
            "clips" => App.Settings.Prop.ClipperEnabled && Voidstrap.Integrations.Clipper.ClipperService.Supported,
            _ => true,
        };
    }

    private Wpf.Ui.Controls.Button? FindDockActionButton(string key)
    {
        return ActionButtons.Children
            .OfType<Wpf.Ui.Controls.Button>()
            .FirstOrDefault(button => string.Equals(button.Tag as string, key, StringComparison.Ordinal));
    }

    private void ApplyDockActionOrder(OverlayLayoutSettings layout)
    {
        List<UIElement> children = ActionButtons.Children.Cast<UIElement>().ToList();
        Dictionary<string, UIElement> actionButtons = new Dictionary<string, UIElement>(StringComparer.Ordinal);
        List<int> actionPositions = new List<int>();
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is FrameworkElement { Tag: string key } && IsDockActionKey(key) && actionButtons.TryAdd(key, children[i]))
                actionPositions.Add(i);
        }

        List<UIElement> orderedButtons = GetDockActionOrder(layout)
            .Where(actionButtons.ContainsKey)
            .Select(key => actionButtons[key])
            .ToList();
        bool changed = false;
        for (int i = 0; i < actionPositions.Count; i++)
        {
            if (!ReferenceEquals(children[actionPositions[i]], orderedButtons[i]))
            {
                children[actionPositions[i]] = orderedButtons[i];
                changed = true;
            }
        }
        if (!changed)
            return;

        ActionButtons.Children.Clear();
        foreach (UIElement child in children)
            ActionButtons.Children.Add(child);
    }

    private static bool IsDockActionKey(string key) =>
        DockActionDefinitions.Any(definition => string.Equals(definition.Key, key, StringComparison.Ordinal));

    private void ApplyDockActionIcon(Wpf.Ui.Controls.Button button, SessionDockActionDefinition definition, OverlayLayoutSettings layout)
    {
        const string LOG_IDENT = "SessionDock::ApplyDockActionIcon";
        if (layout.DockActionIconImages.TryGetValue(definition.Key, out string? imagePath))
        {
            BitmapSource? image = SafeImaging.FromFile(imagePath, 48);
            if (image != null)
            {
                button.Content = new Image
                {
                    Source = image,
                    Width = 18,
                    Height = 18,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
                };
                RenderOptions.SetBitmapScalingMode((Image)button.Content, BitmapScalingMode.HighQuality);
                return;
            }
            App.Logger?.WriteLine(LOG_IDENT, "Could not load the dock icon image " + imagePath);
        }

        if (
            layout.DockActionIcons.TryGetValue(definition.Key, out string? iconName)
            && Enum.TryParse(iconName, out SymbolRegular icon)
            && SidebarIconPickerDialog.CanRender(icon)
        )
        {
            SymbolIcon symbolIcon = new SymbolIcon { Symbol = icon, FontSize = 18 };
            symbolIcon.Foreground = TryFindResource("TextFillColorPrimaryBrush") as Brush;
            button.Content = symbolIcon;
            return;
        }

        if (_defaultDockActionContents.TryGetValue(definition.Key, out object? defaultContent))
            button.Content = defaultContent;
    }
}
