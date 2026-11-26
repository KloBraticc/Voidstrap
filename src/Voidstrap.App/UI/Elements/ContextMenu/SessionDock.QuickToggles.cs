using System.Windows;
using Voidstrap.Models.Persistable;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.Integrations.Overlays;
using Voidstrap.UI.Elements.Crosshair;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionDock
{
    private const string QuickPrefix = "quick:";

    private static bool IsQuickAction(string action) => action.StartsWith(QuickPrefix, StringComparison.Ordinal);

    private static bool QuickShown(string key)
    {
        AppSettings prop = App.Settings.Prop;
        return key switch
        {
            "location" => prop.DockToggleServerLocation,
            "clock" => prop.DockToggleClock,
            "crosshair" => prop.DockToggleCrosshair,
            "keystrokes" => prop.DockToggleKeystrokes,
            "music" => prop.DockToggleMusicIsland,
            _ => false,
        };
    }

    private static bool QuickState(string key)
    {
        AppSettings prop = App.Settings.Prop;
        return key switch
        {
            "location" => prop.ShowServerDetailsUI,
            "clock" => prop.CurrentTimeDisplay,
            "crosshair" => prop.Crosshair,
            "keystrokes" => prop.KeystrokesOverlayEnabled,
            "music" => prop.MusicIslandOverlay,
            _ => false,
        };
    }

    private static void FlipQuickState(string key)
    {
        AppSettings prop = App.Settings.Prop;
        switch (key)
        {
            case "location":
                prop.ShowServerDetailsUI = !prop.ShowServerDetailsUI;
                OverlayWindow.Reconcile();
                break;
            case "clock":
                prop.CurrentTimeDisplay = !prop.CurrentTimeDisplay;
                OverlayWindow.Reconcile();
                break;
            case "crosshair":
                prop.Crosshair = !prop.Crosshair;
                SessionOverlayLayoutEditor.SyncCrosshairIni();
                CrosshairWindow.Reconcile();
                break;
            case "keystrokes":
                prop.KeystrokesOverlayEnabled = !prop.KeystrokesOverlayEnabled;
                KeystrokeOverlayWindow.Reconcile();
                break;
            case "music":
                prop.MusicIslandOverlay = !prop.MusicIslandOverlay;
                MusicIslandOverlayWindow.Reconcile();
                break;
            default:
                return;
        }
        App.Settings.SaveDeferred();
    }

    private void ApplyQuickToggleVisibility()
    {
        foreach (object child in ActionButtons.Children)
        {
            if (child is FrameworkElement { Tag: string action } button && IsQuickAction(action))
                button.Visibility = QuickShown(action[QuickPrefix.Length..]) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void RefreshQuickToggles()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshQuickToggles));
            return;
        }
        if (_closed)
            return;
        ApplyQuickToggleVisibility();
        QueueFitWidth();
        UpdateActiveButtons();
    }

    private void RunQuickToggle(string action)
    {
        const string LOG_IDENT = "SessionDock::RunQuickToggle";
        try
        {
            FlipQuickState(action[QuickPrefix.Length..]);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The shortcut could not change the setting: " + ex.Message);
        }
        UpdateActiveButtons();
    }
}
