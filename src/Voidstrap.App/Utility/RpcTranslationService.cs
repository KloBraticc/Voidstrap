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

    }
}
