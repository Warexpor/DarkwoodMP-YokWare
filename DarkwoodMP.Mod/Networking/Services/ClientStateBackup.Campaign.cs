using System;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Campaign match, progress heuristics, and backup richness scoring.</summary>
    public static partial class ClientStateBackup
    {
        /// <summary>
        /// Soft age floor for legacy/unscoped spoil. Weekends (~2–3 days) must not
        /// false-positive; July-vs-September dual-box poison is months old.
        /// </summary>
        private const double LegacyBackupAncientDays = 14.0;

        /// <summary>Hotbar/inv stack size that never appears in normal play templates.</summary>
        private const int AbsurdItemStackAmount = 100;

        /// <summary>
        /// True when a backup looks like July-era template/spoil that must not overwrite
        /// a correct offline load (inv/hotbar/pose). CampaignId is the stable key —
        /// ContentFingerprint inequality is NOT stale (hashes churn every Save and
        /// host/client packages diverge after share).
        /// </summary>
        public static bool LooksLikeStaleBackupOnFreshWorld(ClientStateBackupData data)
        {
            if (data == null || !HasMeaningfulProgress(data)) return false;

            // Empty / unscoped legacy: refuse poison regardless of day (day>1 must not
            // open a migrate→hotbar path for Jul null-CampaignId files).
            if (string.IsNullOrEmpty(data.CampaignId))
                return LooksLikeLegacyPoisonSnapshot(data);

            // Campaign-scoped and matching: trust CampaignId. Optional belt — missing
            // fingerprint plus absurd pose vs already-loaded live body (client/offline
            // apply only; BackupPoseAbsurdVsLive no-ops on host).
            if (MatchesCurrentCampaign(data))
            {
                if (string.IsNullOrEmpty(data.ContentFingerprint)
                    && BackupPoseAbsurdVsLive(data))
                    return true;
                return false;
            }

            // Non-empty but wrong campaign — callers usually refuse via MatchesCurrentCampaign;
            // still treat poison-shaped snapshots as stale if they reach here.
            return LooksLikeLegacyPoisonSnapshot(data);
        }

        /// <summary>
        /// July dual-box poison / ancient template signals for empty or mismatched
        /// CampaignId backups. Does not use fingerprint inequality.
        /// </summary>
        public static bool LooksLikeLegacyPoisonSnapshot(ClientStateBackupData data)
        {
            if (data == null || !HasMeaningfulProgress(data)) return false;

            if (BackupTimestampAncientOrMissing(data))
                return true;
            if (BackupPoseAbsurdVsLive(data))
                return true;
            if (LooksLikeAbsurdItemStacks(data))
                return true;

            // Null/null fp + missing-or-ancient ts already returned above.
            // Under-reject harden: lvl-1 hotbar/inv Jul snapshots with a recent-but-fake
            // timestamp still look like template spoil when fingerprint is absent.
            if (string.IsNullOrEmpty(data.ContentFingerprint)
                && data.CurrentLevel >= 1
                && ((data.HotbarItems != null && data.HotbarItems.Count > 0)
                    || (data.InventoryItems != null && data.InventoryItems.Count > 0)))
                return true;

            if (string.IsNullOrEmpty(data.ContentFingerprint)
                && (data.CurrentLevel >= 2
                    || (data.Skills != null && data.Skills.Count >= 2)))
                return true;

            return false;
        }

        /// <summary>Missing/unparseable timestamp, or older than <see cref="LegacyBackupAncientDays"/>.</summary>
        private static bool BackupTimestampAncientOrMissing(ClientStateBackupData data)
        {
            DateTime ts = TryParseBackupTimestamp(data);
            if (ts == DateTime.MinValue)
                return true;
            DateTime utc = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
            return (DateTime.UtcNow - utc).TotalDays > LegacyBackupAncientDays;
        }

        /// <summary>
        /// Same thresholds as RestorePosition far-from-live skip. Client apply / offline
        /// only — host LoadBackupFileForPlayer / SendStoredClientBackupTo must never
        /// compare against host Player.Instance (peers far apart false-positive skip).
        /// </summary>
        private static bool BackupPoseAbsurdVsLive(ClientStateBackupData data)
        {
            // Host late-join push uses host body as "live"; gate pose-vs-live out.
            if (ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host)
                return false;

            Player player = Player.Instance;
            if (player == null || data == null) return false;
            Vector3 pos = new Vector3(data.PosX, data.PosY, data.PosZ);
            if (pos.sqrMagnitude < 0.01f) return false;
            Vector3 live = player.transform.position;
            if (live.sqrMagnitude < 0.01f) return false;
            float xz = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(live.x, live.z));
            return xz > 5000f || Mathf.Abs(pos.y - live.y) > 500f;
        }

        /// <summary>Extreme stacks that mark ancient spoil templates, not normal play.</summary>
        private static bool LooksLikeAbsurdItemStacks(ClientStateBackupData data)
        {
            if (data == null) return false;
            if (HasAbsurdStack(data.HotbarItems)) return true;
            if (HasAbsurdStack(data.InventoryItems)) return true;
            return false;
        }

        private static bool HasAbsurdStack(System.Collections.Generic.List<ItemEntry> items)
        {
            if (items == null) return false;
            for (int i = 0; i < items.Count; i++)
            {
                ItemEntry e = items[i];
                if (e != null && e.Amount >= AbsurdItemStackAmount)
                    return true;
            }
            return false;
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
