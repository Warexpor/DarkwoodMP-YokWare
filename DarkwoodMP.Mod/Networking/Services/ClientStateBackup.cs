using System;
using System.Collections.Generic;

namespace DWMPHorde.Networking
{
    [Serializable]
    public class ClientStateBackupData
    {
        /// <summary>Network player id of the client this snapshot belongs to (0 = unknown/local-only).</summary>
        public int PlayerId;
        /// <summary>
        /// Stable co-op campaign id from <see cref="CoopWorldCopyMeta.CampaignId"/>.
        /// Backups are save and campaign scoped; restore is refused on mismatch.
        /// </summary>
        public string CampaignId;
        /// <summary>
        /// Host world package fingerprint at collect time. Refuses restore when the
        /// loaded save was rewound / swapped within the same CampaignId.
        /// </summary>
        public string ContentFingerprint;
        public string Timestamp;
        public int Day;
        public int GameTimeMinutes;
        public float PosX, PosY, PosZ;
        public float Health, Stamina;
        public int Experience, CurrentLevel;
        public int HealthUpgrades, StaminaUpgrades, HotbarUpgrades, InventoryUpgrades;
        public int Lives;
        public float Saturation;
        public bool FedToday;
        public int LastTimeAte;
        /// <summary>Unspent skill points (per-player).</summary>
        public int SkillPoints;
        public List<string> Recipes;
        public List<SkillEntry> Skills;
        public List<string> AvailableSkillNames;
        public List<ItemEntry> InventoryItems;
        public List<ItemEntry> HotbarItems;
        /// <summary>
        /// Per-player morning trader standing (NightTrader / The Three).
        /// not overwritten by host ReputationBulkSync.
        /// </summary>
        public List<NpcRepEntry> NightTraderReputations;
    }

    [Serializable]
    public class NpcRepEntry
    {
        public string Name;
        public int Reputation;
    }

    [Serializable]
    public class ItemEntry
    {
        public int Slot;
        public string Type;
        public float Durability;
        public int Amount;
        public bool IsRecipe;
        public string RecipeFor;
    }

    [Serializable]
    public class SkillEntry
    {
        public string Name;
        public int TimesUsed;
    }

    /// <summary>
    /// Per-client character snapshot for rejoin / host push.
    /// Collect/Serialize: <c>ClientStateBackup.Collect</c>. Paths/IO: <c>ClientStateBackup.Paths</c>.
    /// Campaign/progress: <c>ClientStateBackup.Campaign</c>. Restore: <c>ClientStateBackup.Restore</c>.
    /// </summary>
    public static partial class ClientStateBackup
    {
    }
}
