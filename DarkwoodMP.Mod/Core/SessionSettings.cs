using DWMPHorde.Config;

namespace DWMPHorde
{
    /// <summary>
    /// Gameplay settings every peer must agree on. The host owns them: a connected client
    /// applies the host's values (<see cref="ApplyFromHost"/>) instead of reading its own
    /// <see cref="ModConfig"/>, so friendly fire, loot share and the party multiplier behave
    /// the same on every side. Offline, as host, and before the host has sent anything the
    /// local <see cref="ModConfig"/> values apply; <see cref="ResetToLocal"/> restores that on
    /// every session boundary.
    /// </summary>
    public static class SessionSettings
    {
        private static bool _hostApplied;
        private static bool _hostFriendlyFire;
        private static bool _hostDoubleItems;
        private static LootShareMode _hostLootShare;
        private static int _hostPartyMultiplier;

        /// <summary>True once a host value set is in effect (connected client only).</summary>
        public static bool HostApplied => _hostApplied;

        public static bool FriendlyFireEnabled
        {
            get
            {
                if (_hostApplied)
                    return _hostFriendlyFire;
                return ModConfig.FriendlyFireEnabled == null || ModConfig.FriendlyFireEnabled.Value;
            }
        }

        public static bool DoubleItemsEnabled
        {
            get
            {
                if (_hostApplied)
                    return _hostDoubleItems;
                return ModConfig.DoubleItemsEnabled == null || ModConfig.DoubleItemsEnabled.Value;
            }
        }

        /// <summary>Effective loot-share mode: <see cref="LootShareMode.Off"/> whenever double items is off.</summary>
        public static LootShareMode LootShareMode
        {
            get
            {
                if (_hostApplied)
                    return _hostDoubleItems ? _hostLootShare : LootShareMode.Off;
                return ModConfig.GetLootShareMode();
            }
        }

        /// <summary>
        /// Host-announced party multiplier, or 0 when none was announced (callers then derive it
        /// from the shared roster, see <see cref="CoopBalance.GetPartyMultiplier"/>).
        /// </summary>
        public static int PartyMultiplier => _hostApplied ? _hostPartyMultiplier : 0;

        /// <summary>Client: take the host's values. <paramref name="partyMultiplier"/> below 1 means "derive locally".</summary>
        public static void ApplyFromHost(bool friendlyFire, LootShareMode lootShare, bool doubleItems,
            int partyMultiplier)
        {
            _hostFriendlyFire = friendlyFire;
            _hostLootShare = lootShare;
            _hostDoubleItems = doubleItems;
            _hostPartyMultiplier = partyMultiplier > 0 ? partyMultiplier : 0;
            _hostApplied = true;
        }

        /// <summary>Session boundary: fall back to this install's own config.</summary>
        public static void ResetToLocal()
        {
            _hostApplied = false;
            _hostFriendlyFire = false;
            _hostDoubleItems = false;
            _hostLootShare = LootShareMode.Off;
            _hostPartyMultiplier = 0;
        }
    }
}
