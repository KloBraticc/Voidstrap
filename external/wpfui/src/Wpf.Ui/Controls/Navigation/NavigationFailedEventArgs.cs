using System;

namespace Wpf.Ui.Controls.Navigation;

public sealed class NavigationFailedEventArgs : EventArgs
{
    public NavigationFailedEventArgs(string? pageTag, Exception exception)
    {
        PageTag = pageTag;
        Exception = exception;
    }

    public string? PageTag { get; }

    public Exception Exception { get; }
}
