using System.Collections.Generic;
using HarmonyLib;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Which language entry a line of dialogue came from. A player listening in on a talk
    /// (<see cref="DialogMirror"/>) was sent the talking player's finished text, so the whole
    /// talk read in that player's language. The talking player's game notes where each text it
    /// shows came from (the entry, and what vanilla put into it: a location's direction, the
    /// day), the mirror sends that with the text, and the listener looks the entry up in its own
    /// language. A text with no known entry is shown as it was sent.
    /// </summary>
    internal static class DialogTextKeys
    {
        /// <summary>The entry as it is.</summary>
        internal const byte ModePlain = 0;
        /// <summary>Vanilla <c>Journal.convertToLocationText</c>: where a location lies ({0}, {1}).</summary>
        internal const byte ModeLocation = 1;
        /// <summary>Vanilla's <c>currentDay</c> line: {0} is the day.</summary>
        internal const byte ModeDay = 2;

        private const int MaxNotes = 1500;

        private struct Entry
        {
            public string Key, Sheet;
        }

        private struct Located
        {
            public string Source, Location;
        }

        private static readonly Dictionary<string, Entry> _entryOf = new Dictionary<string, Entry>(); // reset-in: Reset
        private static readonly Dictionary<string, string> _unwrapped = new Dictionary<string, string>(); // reset-in: Reset
        private static readonly Dictionary<string, Located> _located = new Dictionary<string, Located>(); // reset-in: Reset

        public static void Reset()
        {
            _entryOf.Clear();
            _unwrapped.Clear();
            _located.Clear();
        }

        private static bool Noting
        {
            get
            {
                var net = ModRuntime.Network;
                return net != null && net.IsConnected;
            }
        }

        private static void Room()
        {
            if (_entryOf.Count + _unwrapped.Count + _located.Count > MaxNotes)
                Reset();
        }

        /// <summary>
        /// The entry behind a text on the talking player's window. <paramref name="shown"/> is a
        /// button's text or a written line (already broken into rows).
        /// </summary>
        internal static bool TryFind(string shown, out string key, out string sheet, out byte mode, out string arg)
        {
            key = sheet = arg = "";
            mode = ModePlain;
            if (string.IsNullOrEmpty(shown))
                return false;
            string source = _unwrapped.TryGetValue(shown, out string s) ? s : shown;
            if (_entryOf.TryGetValue(source, out Entry e))
            {
                key = e.Key;
                sheet = e.Sheet;
                return true;
            }
            if (_located.TryGetValue(source, out Located l) && _entryOf.TryGetValue(l.Source, out e))
            {
                key = e.Key;
                sheet = e.Sheet;
                mode = ModeLocation;
                arg = l.Location ?? "";
                return true;
            }
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null)
                return false;
            string day = ctrl.day.ToString();
            foreach (KeyValuePair<string, Entry> kv in _entryOf)
            {
                if (kv.Key.IndexOf("{0}", System.StringComparison.Ordinal) < 0 || kv.Key.Replace("{0}", day) != source)
                    continue;
                key = kv.Value.Key;
                sheet = kv.Value.Sheet;
                mode = ModeDay;
                return true;
            }
            return false;
        }

        /// <summary>The listener's own text for an entry (before it is broken into rows), or null to show what was sent.</summary>
        internal static string Local(string key, string sheet, byte mode, string arg)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(sheet) || !Language.Has(key, sheet))
                return null;
            string text = Language.Get(key, sheet);
            if (string.IsNullOrEmpty(text))
                return null;
            if (mode == ModeLocation)
            {
                var ui = Singleton<UI>.Instance;
                if (ui == null || ui.journal == null)
                    return null;
                text = ui.journal.convertToLocationText(text, arg, useColorFormatting: false);
            }
            else if (mode == ModeDay)
            {
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl == null)
                    return null;
                text = text.Replace("{0}", ctrl.day.ToString());
            }
            return text;
        }

        [HarmonyPatch(typeof(Language), nameof(Language.Get), new[] { typeof(string), typeof(string) })]
        private static class GetPatch
        {
            private static void Postfix(string key, string sheetTitle, string __result)
            {
                if (!Noting || string.IsNullOrEmpty(__result) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(sheetTitle))
                    return;
                Room();
                _entryOf[__result] = new Entry { Key = key, Sheet = sheetTitle };
            }
        }

        [HarmonyPatch(typeof(WritingText), nameof(WritingText.formattedText))]
        private static class RowsPatch
        {
            private static void Postfix(string sourceText, string __result)
            {
                if (!Noting || string.IsNullOrEmpty(__result) || __result == sourceText)
                    return;
                Room();
                _unwrapped[__result] = sourceText;
            }
        }

        [HarmonyPatch(typeof(Journal), nameof(Journal.convertToLocationText))]
        private static class LocationPatch
        {
            private static void Postfix(string text, string locationName, string __result)
            {
                if (!Noting || string.IsNullOrEmpty(__result) || __result == text)
                    return;
                Room();
                _located[__result] = new Located { Source = text, Location = locationName };
            }
        }
    }
}
