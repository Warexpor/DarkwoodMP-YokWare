using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Networking;

namespace DWMPHorde
{
    /// <summary>
    /// Shared co-op balance helpers: party size multiplier and type/NPC allowlists.
    /// Policy A — progression sinks + defense mats; named dream NPCs only.
    /// </summary>
    public static class CoopBalance
    {
        public static readonly HashSet<string> UpgradeItemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "meat",
            "exp_mushroom",
            "exp_bio1_nightMushroom_01"
        };

        /// <summary>Barricade construction mats (ConstructionIcon wood/door requirements).</summary>
        public static readonly HashSet<string> DefenseMatTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wood",
            "nail"
        };

        private static string _cachedAllowlistRaw;
        private static HashSet<string> _cachedNpcAllowlist;

        /// <summary>
        /// Party loot/NPC multiplier. Offline or Off → 1; Double → 2;
        /// ScaleWithPlayers → 1 + remote peer count (host + N clients = 1+N).
        /// </summary>
        public static int GetPartyMultiplier()
        {
            var mode = ModConfig.GetLootShareMode();
            if (mode == LootShareMode.Off)
                return 1;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return 1;

            if (mode == LootShareMode.Double)
                return 2;

            return 1 + net.ConnectedPlayerCount;
        }

        public static bool IsDefenseMatType(string type)
        {
            return !string.IsNullOrEmpty(type) && DefenseMatTypes.Contains(type);
        }

        public static bool IsUpgradeItemType(string type)
        {
            return !string.IsNullOrEmpty(type) && UpgradeItemTypes.Contains(type);
        }

        /// <summary>True if type is hideout fuel or barricade mat (loot-share candidate by type name).</summary>
        public static bool IsScaledLootType(string type)
        {
            return IsUpgradeItemType(type) || IsDefenseMatType(type);
        }

        public static bool IsNamedNpcAllowlisted(string nameOrPrefab)
        {
            if (string.IsNullOrEmpty(nameOrPrefab))
                return false;

            string key = NormalizeNpcName(nameOrPrefab);
            if (key.Length == 0)
                return false;

            EnsureNpcAllowlist();
            return _cachedNpcAllowlist.Contains(key);
        }

        /// <summary>Strip path, (Clone), whitespace → short name for allowlist match.</summary>
        public static string NormalizeNpcName(string nameOrPrefab)
        {
            if (string.IsNullOrEmpty(nameOrPrefab))
                return "";

            string s = nameOrPrefab.Trim();
            int slash = s.LastIndexOf('/');
            if (slash >= 0 && slash < s.Length - 1)
                s = s.Substring(slash + 1);
            int clone = s.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (clone >= 0)
                s = s.Substring(0, clone).Trim();
            return s;
        }

        public static bool IsAllowlistedPrefabPath(string prefab)
        {
            if (string.IsNullOrEmpty(prefab))
                return false;
            // Characters/ChomperBlack or characters/chomperblack
            string n = NormalizeNpcName(prefab);
            return IsNamedNpcAllowlisted(n);
        }

        public static void InvalidateAllowlistCache()
        {
            _cachedAllowlistRaw = null;
            _cachedNpcAllowlist = null;
        }

        private static void EnsureNpcAllowlist()
        {
            string raw = ModConfig.NamedNpcAllowlist?.Value ?? "ChomperBlack";
            if (_cachedNpcAllowlist != null &&
                string.Equals(_cachedAllowlistRaw, raw, StringComparison.Ordinal))
                return;

            _cachedAllowlistRaw = raw;
            _cachedNpcAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw))
            {
                _cachedNpcAllowlist.Add("ChomperBlack");
                return;
            }

            foreach (string part in raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string n = NormalizeNpcName(part);
                if (n.Length > 0)
                    _cachedNpcAllowlist.Add(n);
            }

            if (_cachedNpcAllowlist.Count == 0)
                _cachedNpcAllowlist.Add("ChomperBlack");
        }
    }
}
