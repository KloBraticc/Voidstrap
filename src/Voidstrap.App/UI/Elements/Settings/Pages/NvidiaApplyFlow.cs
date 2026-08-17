using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Nvidia;
using Voidstrap.Models;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    internal static class NvidiaApplyFlow
    {
        public static async Task<bool> RunAsync(List<NvidiaEditorEntry> entries)
        {
            if (!NvidiaProfileInspector.IsAvailable)
            {
                Frontend.ShowMessageBox(
                    "The NVIDIA driver is not available on this system. " + NvidiaProfileInspector.UnavailableReason,
                    MessageBoxImage.Exclamation);
                return false;
            }

            NvidiaApplyResult result = Voidstrap.Utility.ProcessElevation.IsAdministrator()
                ? await Task.Run(() => NvidiaProfileManager.ApplyToDriver(entries))
                : await NvidiaProfileManager.ApplyElevatedAsync(entries);

            Frontend.ShowMessageBox(
                Describe(result),
                result.Ok ? MessageBoxImage.Asterisk : MessageBoxImage.Exclamation);
            return result.Ok;
        }

        public static string Describe(NvidiaApplyResult result)
        {
            if (result.Failures.Count == 0)
                return result.Message;

            StringBuilder text = new StringBuilder(result.Message);
            text.Append('\n');
            int shown = 0;
            foreach (string failure in result.Failures)
            {
                if (shown++ == 6)
                {
                    text.Append("\n... and ").Append(result.Failures.Count - 6).Append(" more");
                    break;
                }
                text.Append("\n- ").Append(failure);
            }
            return text.ToString();
        }
    }
}
