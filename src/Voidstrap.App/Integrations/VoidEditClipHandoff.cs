using System.Windows;
using System.Windows.Threading;
using Voidstrap.Integrations.Clipper;
using Voidstrap.UI;

namespace Voidstrap.Integrations;

internal static class VoidEditClipHandoff
{
    private static readonly object Gate = new();

    private static readonly List<string> Pending = new();

    private static bool _prompting;

    public static void Add(SavedClipInfo clip)
    {
        if (!App.Settings.Prop.VoidEditAskForRecordings || string.IsNullOrEmpty(clip.FullPath))
            return;
        lock (Gate)
        {
            if (!Pending.Contains(clip.FullPath, StringComparer.OrdinalIgnoreCase))
                Pending.Add(clip.FullPath);
        }
        if (!ClipperService.InGame)
            Prompt();
    }

    public static void Prompt()
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowPrompt));
    }

    private static void ShowPrompt()
    {
        const string LOG_IDENT = "VoidEditClipHandoff::ShowPrompt";
        List<string> clips;
        lock (Gate)
        {
            if (_prompting || Pending.Count == 0)
                return;
            _prompting = true;
            clips = Pending.Where(File.Exists).ToList();
            Pending.Clear();
        }
        try
        {
            if (clips.Count == 0)
                return;
            string message = string.Format(Strings.Ui_VoidEdit_AskRecordings_Prompt, clips.Count);
            if (Frontend.ShowMessageBox(message, MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            App.Logger.WriteLine(LOG_IDENT, "Opening " + clips.Count + " recordings in Void Edit");
            _ = OpenAllAsync(clips);
        }
        finally
        {
            lock (Gate)
                _prompting = false;
        }
    }

    private static async Task OpenAllAsync(List<string> clips)
    {
        foreach (string clip in clips)
            await VoidEditLauncher.OpenAsync(clip);
    }
}
