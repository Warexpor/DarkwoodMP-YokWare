using System;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Campaign match, progress heuristics, and backup richness scoring.</summary>
    public static partial class ClientStateBackup
    {
        /// <summary>
        /// True when backup looks like a prior playthrough applied onto a fresh day-1 world
        /// (CampaignId reused because mint was skipped). Used to refuse host push / restore.
        /// </summary>
        public static bool LooksLikeStaleBackupOnFreshWorld(ClientStateBackupData data)
        {
            if (data == null || !HasMeaningfulProgress(data)) return false;
            var ctrl = Singleton<Controller>.Instance;
            int day = ctrl != null ? ctrl.day : (Core.currentProfile != null ? Core.currentProfile.day : 0);
            if (day > 1) return false;
            // Day-1 world with a progressed character (lvl/skills/inv) from another fingerprint.
            string curFp = CoopWorldCopyMeta.TryGetCurrentContentFingerprint();
            if (string.IsNullOrEmpty(curFp) || string.IsNullOrEmpty(data.ContentFingerprint))
                return data.CurrentLevel >= 2 || (data.Skills != null && data.Skills.Count >= 2);
            if (string.Equals(curFp, data.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
                return false;
            return data.CurrentLevel >= 1
                || (data.Skills != null && data.Skills.Count > 0)
                || (data.InventoryItems != null && data.InventoryItems.Count > 0);
        }

        /// <summary>True when backup JSON belongs to the active campaign (or both unscoped legacy).
        /// Fingerprint matching was removed because host and client package hashes diverge after share.
        /// </summary>
        public static bool MatchesCurrentCampaign(ClientStateBackupData data)
        {
            if (data == null) return false;
            string current = CoopWorldCopyMeta.TryGetCurrentCampaignId();
            if (string.IsNullOrEmpty(current) && string.IsNullOrEmpty(data.CampaignId))
                return true; // legacy both sides
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(data.CampaignId))
                return false;
            return string.Equals(current, data.CampaignId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when backup has meaningful progression (not an empty wipe snapshot).</summary>
        public static bool HasMeaningfulProgress(ClientStateBackupData data)
        {
            if (data == null) return false;
            if (data.CurrentLevel > 0 || data.Experience > 0) return true;
            if (data.SkillPoints > 0) return true;
            if (data.Skills != null && data.Skills.Count > 0) return true;
            if (data.InventoryItems != null && data.InventoryItems.Count > 0) return true;
            if (data.HotbarItems != null && data.HotbarItems.Count > 0) return true;
            return false;
        }

        /// <summary>Rough richness score for choosing between two backups.</summary>
        public static int ProgressScore(ClientStateBackupData data)
        {
            if (data == null) return 0;
            int score = data.CurrentLevel * 1000 + data.Experience
                + data.SkillPoints * 50
                + (data.Skills?.Count ?? 0) * 100
                + (data.InventoryItems?.Count ?? 0) * 10
                + (data.HotbarItems?.Count ?? 0) * 10;
            return score;
        }

        /// <summary>Parse backup Timestamp for freshness compares (0 on failure).</summary>
        public static DateTime TryParseBackupTimestamp(ClientStateBackupData data)
        {
            if (data == null || string.IsNullOrEmpty(data.Timestamp))
                return DateTime.MinValue;
            if (DateTime.TryParse(data.Timestamp, out DateTime dt))
                return dt;
            return DateTime.MinValue;
        }
    }
}
