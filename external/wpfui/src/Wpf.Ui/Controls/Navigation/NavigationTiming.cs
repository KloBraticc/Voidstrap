using System;

namespace Wpf.Ui.Controls.Navigation;

public static class NavigationTiming
{
    public static event Action<Type, double>? PageCreated;

    internal static void Report(Type pageType, double milliseconds)
    {
        PageCreated?.Invoke(pageType, milliseconds);
    }
}
