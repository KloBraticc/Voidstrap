using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Voidstrap.Integrations.GameChat
{
    public static class GameChatFilter
    {
        public const string DefaultFilterPreference = "default";

        public static readonly HashSet<string> ValidFilterPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "strict",
            "default",
            "relaxed"
        };

        private sealed class Thresholds
        {
            public double Toxicity;
            public double Insult;
            public double Profanity;
            public double SevereToxicity;
            public double IdentityAttack;
            public double Threat;
            public double SexuallyExplicit;
        }

        private static double Score(JsonElement scores, string attribute)
        {
            if (scores.ValueKind != JsonValueKind.Object)
                return 0;
            if (!scores.TryGetProperty(attribute, out JsonElement attr))
                return 0;
            if (!attr.TryGetProperty("summaryScore", out JsonElement summary))
                return 0;
            if (!summary.TryGetProperty("value", out JsonElement value))
                return 0;
            return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
        }

        public static bool ShouldHideMessageByFilter(JsonElement scores)
        {
            if (scores.ValueKind != JsonValueKind.Object)
                return false;

            string preference = GetCurrentFilterPreference();
            Thresholds thresholds = preference switch
            {
                "strict" => new Thresholds
                {
                    Toxicity = 0.60,
                    Insult = 0.65,
                    Profanity = 0.55,
                    SevereToxicity = 0.30,
                    IdentityAttack = 0.30,
                    Threat = 0.65,
                    SexuallyExplicit = 0.30,
                },
                "relaxed" => new Thresholds
                {
                    Toxicity = 0.95,
                    Insult = 0.95,
                    Profanity = 0.95,
                    SevereToxicity = 0.70,
                    IdentityAttack = 0.60,
                    Threat = 0.80,
                    SexuallyExplicit = 0.70,
                },
                _ => new Thresholds
                {
                    Toxicity = 0.85,
                    Insult = 0.85,
                    Profanity = 0.70,
                    SevereToxicity = 0.50,
                    IdentityAttack = 0.40,
                    Threat = 0.80,
                    SexuallyExplicit = 0.50,
                },
            };

            return
                Score(scores, "TOXICITY") >= thresholds.Toxicity ||
                Score(scores, "INSULT") >= thresholds.Insult ||
                Score(scores, "PROFANITY") >= thresholds.Profanity ||
                Score(scores, "SEVERE_TOXICITY") >= thresholds.SevereToxicity ||
                Score(scores, "IDENTITY_ATTACK") >= thresholds.IdentityAttack ||
                Score(scores, "THREAT") >= thresholds.Threat ||
                Score(scores, "SEXUALLY_EXPLICIT") >= thresholds.SexuallyExplicit;
        }

        public static string GetCurrentFilterPreference()
        {
            string current = App.Settings.Prop.GameChatFilter;
            if (string.IsNullOrWhiteSpace(current))
                return SetPreference(DefaultFilterPreference);
            string normalized = current.Trim().ToLowerInvariant();
            if (!ValidFilterPreferences.Contains(normalized))
                return SetPreference(DefaultFilterPreference);
            return normalized;
        }

        public static string SetPreference(string preference)
        {
            App.Settings.Prop.GameChatFilter = preference;
            App.Settings.SaveDeferred();
            return preference;
        }
    }
}
