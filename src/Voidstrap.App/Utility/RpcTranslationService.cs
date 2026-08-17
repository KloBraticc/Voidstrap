using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DiscordRPC;

namespace Voidstrap.Utility
{
    public static class RpcTranslationService
    {
        public static bool IsEnabled =>
            App.Settings.Prop.AutoTranslate &&
            App.Settings.Prop.RpcAutoTranslate;

        private static string TargetLanguage => App.Settings.Prop.AutoTranslateLanguage;

        private static string? _lastTranslatedLanguage;

        public static void InvalidateLanguage()
        {
            _lastTranslatedLanguage = null;
        }

        public static async Task ApplyToPresenceAsync(DiscordRPC.RichPresence presence)
        {
            if (presence is null)
                return;

            string lang = TargetLanguage;

            if (_lastTranslatedLanguage == lang)
                return;

            if (!IsEnabled)
                return;

            var tasks = new List<Task<string>>();
            var fields = new List<Action<string>>();

            void AddField(Func<string> getter, Action<string> setter)
            {
                string val = getter();
                if (!string.IsNullOrEmpty(val))
                {
                    string captured = val;
                    tasks.Add(TranslationService.TranslateAsync(captured, lang));
                    fields.Add(translated => setter(translated));
                }
            }

            AddField(() => presence.Details, v => presence.Details = v);
            AddField(() => presence.State, v => presence.State = v);

            if (presence.Assets is not null)
            {
                AddField(() => presence.Assets.LargeImageText, v => presence.Assets.LargeImageText = v);
                AddField(() => presence.Assets.SmallImageText, v => presence.Assets.SmallImageText = v);
            }

            if (presence.Buttons is not null)
            {
                for (int i = 0; i < presence.Buttons.Length; i++)
                {
                    int idx = i;
                    AddField(() => presence.Buttons[idx].Label, v => presence.Buttons[idx].Label = v);
                }
            }

            if (tasks.Count == 0)
                return;

            try
            {
                string[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
                for (int i = 0; i < results.Length; i++)
                    fields[i](results[i]);

                _lastTranslatedLanguage = lang;
            }
            catch
            {
            }
        }
    }
}
