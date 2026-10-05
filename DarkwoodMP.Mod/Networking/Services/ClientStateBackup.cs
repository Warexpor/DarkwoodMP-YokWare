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
        /// SteamID64 string when collected on a Steam session. Host disk key that survives
        /// PlayerId reshuffles across cold sessions.
        /// </summary>
        public string SteamId;
        /// <summary>
        /// Install-scoped LAN identity (32-hex). Host disk key when SteamId is absent —
        /// survives PlayerId reshuffles without endpoint/machine-fingerprint false merges.
        /// </summary>
        public string StableClientKey;
        /// <summary>
        /// Stable co-op campaign id from <see cref="CoopWorldCopyMeta.CampaignId"/>.
        /// Backups are save and campaign scoped; restore is refused on mismatch.
        /// </summary>
        public string CampaignId;
        /// <summary>
        /// Host world package fingerprint at collect time. Diagnostic / optional
        /// poison signal only — CampaignId is the restore key; fingerprint inequality
        /// is not treated as stale (hashes churn every Save; host/client diverge).
        /// </summary>
        public string ContentFingerprint;
        public string Timestamp;
        /// <summary>Chapter the snapshot was taken in (0 = older JSON). A pose from another chapter's map is not restored.</summary>
        public int Chapter;
        /// <summary>This player's home oven (vanilla Player.experienceMachine, also the respawn home).</summary>
        public bool HasHomeOven;
        public float HomeOvenX, HomeOvenY, HomeOvenZ;
        public int Day;
        public int GameTimeMinutes;
        public float PosX, PosY, PosZ;
        public float Health, Stamina;
        public int Experience, CurrentLevel;
        /// <summary>
        /// This player's own level-dream slots (Dreams.hadDreamAtLvl2/3/5/6/7 as bits, see
        /// DreamSession.LvlFlag*). The world the client loads carries the host's slots.
        /// -1 = older backup: every dream level already passed counts as had.
        /// </summary>
        public int DreamLvlFlags = -1;
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
        /// Hotbar slot index that was selected at Collect (vanilla InvSlot.selected).
        /// Inventory.SaveState omits this; co-op restores onto a host-loaded body whose
        /// selected flag may be wrong, so we persist it explicitly.
        /// −1 = unset (older JSON) — restore leaves leftover selected flags alone.
        /// </summary>
        public int HotbarSelectedSlot = -1;
        /// <summary>
        /// Active CharacterEffects (bleed, poison, hunger, wards, …) — vanilla
        /// Player.SaveState.chEffS. Absent/empty on older backups.
        /// </summary>
        public List<EffectEntry> ActiveEffects;
        /// <summary>
        /// Personal blue map pins (MultiplayerMapManager.LocalMarkers). Mod-only;
        /// NetworkReset clears them on disconnect so cold rejoin needs backup.
        /// </summary>
        public List<MarkerEntry> LocalMapMarkers;
        /// <summary>
        /// Per-player morning trader standing (NightTrader / The Three).
        /// not overwritten by host ReputationBulkSync.
        /// </summary>
        public List<NpcRepEntry> NightTraderReputations;
        /// <summary>
        /// Personal craft counts (vanilla Player.SaveState.craftedItems /
        /// timesCraftedLimit). WorldSaveShare loads the HOST list first.
        /// Null on older backups — restore skips.
        /// </summary>
        public List<CraftedEntry> CraftedItems;
        /// <summary>
        /// Vanilla PlayerSkills.SaveState.canActivateSkill (active-skill cooldown gate).
        /// Older JSON defaults true — restore never locks skills from legacy.
        /// </summary>
        public bool CanActivateSkill = true;
    }

    [Serializable]
    public class CraftedEntry
    {
        public string Type;
        public int Count;
    }

    [Serializable]
    public class EffectEntry
    {
        /// <summary>CharacterEffectType as int (Json.NET-friendly).</summary>
        public int Type;
        public float Duration;
        public float Modifier;
        public float Interval;
        public float TimeElapsed;
    }

    [Serializable]
    public class MarkerEntry
    {
        public float X, Y, Z;
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
        /// <summary>
        /// Vanilla InvItemClass.SaveState.shouldBeActive — flashlight (and similar
        /// toggle lights) stay on across rejoin/migration when the slot is selected.
        /// </summary>
        public bool ShouldBeActive;
        /// <summary>
        /// Workbench ItemUpgrade names (vanilla SaveState.upgrades). Null/empty on
        /// older backups — restore skips (same as no upgrades).
        /// </summary>
        public List<string> Upgrades;
        /// <summary>
        /// Vanilla timeDeactivated (regen-when-inactive cooldown). Absolute Time.time
        /// at collect; restore clamps so a stale wall-clock never blocks regen forever.
        /// </summary>
        public float TimeDeactivated;
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
