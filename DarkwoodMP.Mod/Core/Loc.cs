using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DWMPHorde
{
    /// <summary>
    /// The mod's own player-facing text in the game's language. Text is written in English
    /// everywhere and translated only where it is shown (menu labels, HUD lines, IMGUI windows):
    /// code that matches on English status text keeps working, and an English reason a host
    /// sends reaches a Russian client in Russian. Russian is the one translation; every other
    /// language shows the English text. Tables: <c>Loc.Ru.cs</c>.
    /// </summary>
    public static partial class Loc
    {
        /// <summary>Vanilla's <c>LanguageCode</c> setting (EN, RU, …), refreshed every frame.</summary>
        public static string Language { get; private set; } = "EN";

        public static bool Russian => Language == "RU";

        public static void SetLanguage(string code)
        {
            Language = string.IsNullOrEmpty(code) ? "EN" : code;
        }

        /// <summary><paramref name="text"/> in the game's language.</summary>
        public static string T(string text) => Russian ? ToRussian(text) : text;

        private const int CacheMax = 512;
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(64); // process-scoped: translation memo, bounded

        /// <summary>
        /// Russian for <paramref name="text"/>: a whole-string entry, else the first pattern that
        /// matches (its parts translated in turn), else each line on its own; text with no entry
        /// stays as it is (player names, file paths, exception messages).
        /// </summary>
        public static string ToRussian(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            if (Cache.TryGetValue(text, out string hit))
                return hit;
            string ru = Translate(text);
            if (Cache.Count >= CacheMax)
                Cache.Clear();
            Cache[text] = ru;
            return ru;
        }

        /// <summary>True when <paramref name="text"/> has a Russian entry or pattern (tests).</summary>
        public static bool Knows(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            if (RuExact.ContainsKey(text))
                return true;
            for (int i = 0; i < RuPatterns.Length; i++)
            {
                if (RuPatterns[i].Regex.IsMatch(text))
                    return true;
            }
            return false;
        }

        private static string Translate(string text)
        {
            if (RuExact.TryGetValue(text, out string ru))
                return ru;
            for (int i = 0; i < RuPatterns.Length; i++)
            {
                Match m = RuPatterns[i].Regex.Match(text);
                if (m.Success)
                    return RuPatterns[i].Build(m);
            }
            if (text.IndexOf('\n') >= 0)
            {
                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = Translate(lines[i]);
                return string.Join("\n", lines);
            }
            return text;
        }

        private sealed class Pattern
        {
            public Regex Regex;
            public Func<Match, string> Build;
        }

        private static Pattern P(string regex, Func<Match, string> build)
            => new Pattern { Regex = new Regex("^" + regex + "$", RegexOptions.CultureInvariant | RegexOptions.Singleline), Build = build };

        /// <summary>Group <paramref name="i"/> as written (a number, a name).</summary>
        private static string V(Match m, int i) => m.Groups[i].Value;

        /// <summary>Group <paramref name="i"/> translated (a nested reason).</summary>
        private static string G(Match m, int i) => Translate(m.Groups[i].Value);
    }
}
