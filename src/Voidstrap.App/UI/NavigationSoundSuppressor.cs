using System.Reflection;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace Voidstrap.UI;

public static class NavigationSoundSuppressor
{
    private static readonly FieldInfo? NavigatingInitiator = typeof(NavigatingCancelEventArgs).GetField(
        "_isNavInitiator",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    private static readonly FieldInfo? CompletedInitiator = typeof(NavigationEventArgs).GetField(
        "_isNavigationInitiator",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    public static void Attach(Frame frame)
    {
        frame.Navigating -= OnNavigating;
        frame.Navigating += OnNavigating;
        frame.LoadCompleted -= OnLoadCompleted;
        frame.LoadCompleted += OnLoadCompleted;
    }

    public static void Detach(Frame frame)
    {
        frame.Navigating -= OnNavigating;
        frame.LoadCompleted -= OnLoadCompleted;
    }

    private static void OnNavigating(object sender, NavigatingCancelEventArgs e)
    {
        if (e.IsNavigationInitiator)
            Clear(NavigatingInitiator, e);
    }

    private static void OnLoadCompleted(object sender, NavigationEventArgs e)
    {
        if (e.IsNavigationInitiator)
            Clear(CompletedInitiator, e);
    }

    private static void Clear(FieldInfo? field, object args)
    {
        try
        {
            field?.SetValue(args, false);
        }
        catch (Exception ex) when (ex is FieldAccessException or ArgumentException)
        {
            App.Logger.WriteLine("NavigationSoundSuppressor::Clear", "The navigation sound could not be muted: " + ex.Message);
        }
    }
}
