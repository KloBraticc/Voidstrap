using System;
using System.Windows;
using System.Windows.Controls;

namespace Voidstrap.UI.Elements.Bootstrapper;

internal static class ProgressBarStyler
{
    public static void Apply(ProgressBar bar)
    {
        if (bar == null)
            return;

        try
        {
            if (bar.Style == null)
            {
                if (Application.Current?.TryFindResource(typeof(System.Windows.Controls.ProgressBar)) is Style resolved)
                {
                    bar.Style = resolved;
                }
                else
                {
                    ResourceDictionary dictionary = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/Wpf.Ui;component/Styles/Controls/ProgressBar.xaml", UriKind.Absolute)
                    };

                    if (dictionary[typeof(System.Windows.Controls.ProgressBar)] is Style fallback)
                        bar.Style = fallback;
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("ProgressBarStyler::Apply", "Could not apply the progress bar style: " + ex.Message);
        }

        try
        {
            bar.SetResourceReference(Control.ForegroundProperty, "AccentColorSecondaryBrush");
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("ProgressBarStyler::Apply", "Could not apply the accent brush: " + ex.Message);
        }
    }
}
