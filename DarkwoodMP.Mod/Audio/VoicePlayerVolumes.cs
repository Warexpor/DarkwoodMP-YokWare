using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DWMPHorde.Config;
using DWMPHorde.Sync;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// How loud this player hears each other player (Multiplayer > Settings > Voice > Players),
    /// 0 (muted) to 2, kept by the name they go by so it holds across sessions and rejoins.
    /// Config <c>[Voice] VoicePlayerVolumes</c>: <c>name=volume</c> entries separated by <c>|</c>.
    /// </summary>
    internal static class VoicePlayerVolumes
    {
        private static readonly Dictionary<string, float> _byName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase); // process-scoped: parsed config
        private static string _parsed; // process-scoped: the config text _byName was read from

        internal static float Get(int playerId) => Get(PlayerNames.Shown(playerId));

        internal static float Get(string name)
        {
            Load();
            return name != null && _byName.TryGetValue(Key(name), out float v) ? v : 1f;
        }

        internal static void Set(int playerId, float volume)
        {
            Load();
            string key = Key(PlayerNames.Shown(playerId));
            volume = Math.Max(0f, Math.Min(2f, volume));
            if (Math.Abs(volume - 1f) < 0.001f)
                _byName.Remove(key);
            else
                _byName[key] = volume;
            Save();
        }

        /// <summary>Names may hold anything typed; the two separators are swapped out of the key.</summary>
        private static string Key(string name) => (name ?? "").Replace('|', '/').Replace('=', '-').Trim();

        private static void Load()
        {
            string text = ModConfig.VoicePlayerVolumes?.Value ?? "";
            if (text == _parsed)
                return;
            _parsed = text;
            _byName.Clear();
            foreach (string entry in text.Split('|'))
            {
                int eq = entry.LastIndexOf('=');
                if (eq <= 0)
                    continue;
                if (float.TryParse(entry.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    _byName[entry.Substring(0, eq).Trim()] = Math.Max(0f, Math.Min(2f, v));
            }
        }

        private static void Save()
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, float> kv in _byName)
            {
                if (sb.Length > 0)
                    sb.Append('|');
                sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("0.##", CultureInfo.InvariantCulture));
            }
            _parsed = sb.ToString();
            if (ModConfig.VoicePlayerVolumes != null)
                ModConfig.VoicePlayerVolumes.Value = _parsed;
        }
    }
}
