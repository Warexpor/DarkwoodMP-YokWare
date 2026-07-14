using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Networking;

namespace DWMPHorde
{
    /// <summary>
    /// Shared co-op balance helpers: party size multiplier and type/NPC allowlists.
    /// Loot share: hideout fuels from EN_Items (not wood/nail, not regular dog meat).
    /// </summary>
    public static class CoopBalance
    {
        /// <summary>
        /// Hideout furnace fuels — type keys from EN_Items.bytes (*_name).
        /// Also scaled: any vanilla prefab with isExpItem (ItemDoublePickupPatch).
        /// </summary>
        public static readonly HashSet<string> UpgradeItemTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "exp_mushroom",              // Odd-looking mushroom
            "exp_nightMushroom",         // Odd-looking, glowing mushroom
            "exp_meat_mutated",          // Odd meat
            "exp_bio2_meat_mutated",     // Odd meat (ch2)
            "exp_bio3_meat_mutated",     // Odd meat (ch3)
            "chicken_egg_red",          // Red egg
            "exp_piskle",               // ?
            "lifePotion",               // Embryo
            "dead_rat",                 // Dead rat
            "fish",                     // Fish
            "exp_cockroach_mutated",    // Insect
            // Large odd mushrooms (same fuel role, not on the short list UI)
            "exp_bio2_mushroom_01",
            "exp_bio2_nightMushroom_01",
            "exp_bio3_mushroom_meat_01",
            "exp_bio3_nightMushroom_01"
        };

        /// <summary>Was wood/nail; empty — barricade mats stay 1×.</summary>
        public static readonly HashSet<string> DefenseMatTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string _cachedAllowlistRaw;
        private static HashSet<string> _cachedNpcAllowlist;

        /// <summary>
        /// Party loot/NPC multiplier. Offline or Off → 1;
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

        /// <summary>True if type is an allowlisted hideout fuel (loot-share by type name).</summary>
        public static bool IsScaledLootType(string type)
        {
            return IsUpgradeItemType(type);
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
