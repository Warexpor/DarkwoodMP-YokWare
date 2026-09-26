using System;
using System.IO;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Disk paths and save/load IO for client character backups.</summary>
    public static partial class ClientStateBackup
    {
        /// <summary>Profile save directory for the active Darkwood profile (creates if needed).</summary>
        public static string GetProfileBackupDirectory()
        {
            string saveDir = Application.persistentDataPath + "/1_4Save";
            string profileName = "prof" + (Core.currentProfile?.id ?? 1);
            string dir = saveDir + "/" + profileName;
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning($"[ClientStateBackup] mkdir failed: {ex.Message}");
            }
            return dir;
        }

        private static string SanitizeCampaignIdForPath(string campaignId)
        {
            if (string.IsNullOrEmpty(campaignId)) return null;
            // GUID "N" is hex-only; strip anything else for path safety.
            var sb = new System.Text.StringBuilder(campaignId.Length);
            for (int i = 0; i < campaignId.Length; i++)
            {
                char c = campaignId[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// Host-side path for a remote client's backup, keyed by network PlayerId + campaign.
        /// Prefer <see cref="GetBackupFilePathForSteam"/> when SteamID64 is known —
        /// PlayerId reshuffles across cold sessions and restores the wrong inventory.
        /// </summary>
        public static string GetBackupFilePathForPlayer(int playerId)
        {
            if (playerId <= 0)
                return GetLocalSelfBackupPath();
            string campaign = SanitizeCampaignIdForPath(
                CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile());
            if (string.IsNullOrEmpty(campaign))
                return GetProfileBackupDirectory() + "/client_backup_p" + playerId + ".json";
            return GetProfileBackupDirectory() + "/client_backup_p" + playerId + "_" + campaign + ".json";
        }

        /// <summary>
        /// Host-side path keyed by SteamID64 + campaign (stable across PlayerId reshuffles).
        /// </summary>
        public static string GetBackupFilePathForSteam(ulong steamId)
        {
            if (steamId == 0)
                return GetLocalSelfBackupPath();
            string campaign = SanitizeCampaignIdForPath(
                CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile());
            if (string.IsNullOrEmpty(campaign))
                return GetProfileBackupDirectory() + "/client_backup_s" + steamId + ".json";
            return GetProfileBackupDirectory() + "/client_backup_s" + steamId + "_" + campaign + ".json";
        }

        /// <summary>Parse SteamID64 from backup JSON field; 0 if missing/invalid.</summary>
        public static ulong TryParseSteamId(string steamIdRaw)
        {
            if (string.IsNullOrEmpty(steamIdRaw)) return 0;
            return ulong.TryParse(steamIdRaw.Trim(), out ulong sid) ? sid : 0;
        }

        private static string GetLegacySteamBackupPath(ulong steamId) =>
            GetProfileBackupDirectory() + "/client_backup_s" + steamId + ".json";

        /// <summary>
        /// Local-only path for this machine's snapshot, keyed by current campaign.
        /// </summary>
        public static string GetLocalSelfBackupPath()
        {
            string campaign = SanitizeCampaignIdForPath(
                CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile());
            if (string.IsNullOrEmpty(campaign))
                return GetProfileBackupDirectory() + "/client_backup_self.json";
            return GetProfileBackupDirectory() + "/client_backup_self_" + campaign + ".json";
        }

        /// <summary>Legacy single-file path (pre multi-client / pre-campaign). Load fallback only.</summary>
        public static string GetLegacyBackupFilePath()
        {
            return GetProfileBackupDirectory() + "/client_backup.json";
        }

        private static string GetLegacyPlayerBackupPath(int playerId) =>
            GetProfileBackupDirectory() + "/client_backup_p" + playerId + ".json";

        private static string GetLegacySelfBackupPath() =>
            GetProfileBackupDirectory() + "/client_backup_self.json";

        /// <summary>
        /// Older backups omit CampaignId. Stamp the current campaign and re-save
        /// so host stop rejecting with "file=(none)".
        /// </summary>
        private static ClientStateBackupData MigrateLegacyCampaignIfNeeded(
            ClientStateBackupData data, int playerIdForPath)
        {
            if (data == null) return null;
            if (MatchesCurrentCampaign(data))
                return data;

            string current = CoopWorldCopyMeta.TryGetCurrentCampaignId();
            if (string.IsNullOrEmpty(current) || !string.IsNullOrEmpty(data.CampaignId))
                return null; // mismatched non-empty id, or no campaign to adopt

            // Refuse BEFORE stamping CampaignId — empty-CampaignId Jul poison must not
            // become "matched" then slip past day>1 / fingerprint checks (hotbar/inv).
            if (LooksLikeLegacyPoisonSnapshot(data))
            {
                ModRuntime.LegacyInfo(
                    "[ClientBackup] refuse legacy migrate — empty-CampaignId poison/spoil snapshot");
                return null;
            }

            data.CampaignId = current;
            try
            {
                string json = SerializeToJson(data);
                if (playerIdForPath > 0)
                    SaveBackupFile(json, playerIdForPath);
                else
                    SaveLocalSelfBackupFile(json);
                ModRuntime.LegacyInfo(
                    "[ClientBackup] migrated legacy backup → campaign " + current);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning(
                    "[ClientBackup] legacy campaign migrate failed: " + ex.Message);
            }
            return data;
        }

        /// <summary>
        /// Save a remote client's backup on the host.
        /// Key order: SteamID64 → StableClientKey (LAN) → PlayerId (same-session soft reconnect).
        /// </summary>
        public static void SaveBackupFile(
            string json, int playerId, ulong steamId = 0, string stableClientKey = null)
        {
            try
            {
                ClientStateBackupData parsed = null;
                string stableKey = SanitizeStableClientKey(stableClientKey);
                try
                {
                    parsed = DeserializeFromJson(json);
                    if (parsed != null)
                    {
                        if (steamId == 0)
                            steamId = TryParseSteamId(parsed.SteamId);
                        if (string.IsNullOrEmpty(stableKey))
                            stableKey = SanitizeStableClientKey(parsed.StableClientKey);
                        if (steamId != 0
                            && (string.IsNullOrEmpty(parsed.SteamId)
                                || TryParseSteamId(parsed.SteamId) != steamId))
                        {
                            parsed.SteamId = steamId.ToString();
                            json = SerializeToJson(parsed);
                        }
                        if (!string.IsNullOrEmpty(stableKey)
                            && !string.Equals(parsed.StableClientKey, stableKey, StringComparison.OrdinalIgnoreCase))
                        {
                            parsed.StableClientKey = stableKey;
                            json = SerializeToJson(parsed);
                        }

                        string cur = CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile();
                        if (!string.IsNullOrEmpty(cur)
                            && !string.Equals(parsed.CampaignId, cur, StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrEmpty(parsed.CampaignId))
                            {
                                if (LooksLikeLegacyPoisonSnapshot(parsed))
                                {
                                    ModRuntime.LegacyInfo(
                                        "[ClientBackup] refuse save stamp — empty-CampaignId poison/spoil snapshot p"
                                        + playerId
                                        + (steamId != 0 ? " s" + steamId : "")
                                        + (stableKey != null ? " k" + stableKey.Substring(0, Math.Min(8, stableKey.Length)) : ""));
                                    return;
                                }
                                parsed.CampaignId = cur;
                                json = SerializeToJson(parsed);
                            }
                        }
                    }
                }
                catch { /* keep raw json */ }

                string path;
                string keyTag;
                if (steamId != 0)
                {
                    path = GetBackupFilePathForSteam(steamId);
                    keyTag = " steam=" + steamId;
                    try
                    {
                        if (parsed == null)
                            parsed = DeserializeFromJson(json);
                        if (parsed != null && !string.IsNullOrEmpty(parsed.CampaignId))
                        {
                            string c = SanitizeCampaignIdForPath(parsed.CampaignId);
                            if (!string.IsNullOrEmpty(c))
                                path = GetProfileBackupDirectory() + "/client_backup_s" + steamId + "_" + c + ".json";
                        }
                    }
                    catch { /* use GetBackupFilePathForSteam */ }
                }
                else if (!string.IsNullOrEmpty(stableKey))
                {
                    path = GetBackupFilePathForStableKey(stableKey);
                    keyTag = " k" + stableKey.Substring(0, Math.Min(8, stableKey.Length));
                    try
                    {
                        if (parsed == null)
                            parsed = DeserializeFromJson(json);
                        if (parsed != null && !string.IsNullOrEmpty(parsed.CampaignId))
                        {
                            string c = SanitizeCampaignIdForPath(parsed.CampaignId);
                            if (!string.IsNullOrEmpty(c))
                                path = GetProfileBackupDirectory() + "/client_backup_k" + stableKey + "_" + c + ".json";
                        }
                    }
                    catch { /* use GetBackupFilePathForStableKey */ }
                }
                else
                {
                    path = GetBackupFilePathForPlayer(playerId);
                    keyTag = " player " + playerId;
                    try
                    {
                        if (parsed == null)
                            parsed = DeserializeFromJson(json);
                        if (parsed != null && !string.IsNullOrEmpty(parsed.CampaignId) && playerId > 0)
                        {
                            string c = SanitizeCampaignIdForPath(parsed.CampaignId);
                            if (!string.IsNullOrEmpty(c))
                                path = GetProfileBackupDirectory() + "/client_backup_p" + playerId + "_" + c + ".json";
                        }
                    }
                    catch { /* use path from GetBackupFilePathForPlayer */ }
                }

                File.WriteAllText(path, json);
                ModRuntime.LegacyInfo("[ClientBackup] saved" + keyTag + " → " + path);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError(
                    "[ClientBackup] failed to save"
                    + (steamId != 0 ? " steam=" + steamId : " player " + playerId)
                    + ": " + ex);
            }
        }

        /// <summary>Save this machine's local self backup (ManualSave / pre-load / exit).</summary>
        public static void SaveLocalSelfBackupFile(string json)
        {
            try
            {
                // Never clobber a good self file with an empty collect (title/load race).
                try
                {
                    var incoming = DeserializeFromJson(json);
                    if (incoming != null && !HasMeaningfulProgress(incoming))
                    {
                        var existing = TryReadBackup(GetLocalSelfBackupPath())
                            ?? TryReadBackup(GetLegacySelfBackupPath());
                        if (existing != null && HasMeaningfulProgress(existing)
                            && MatchesCurrentCampaign(existing))
                        {
                            ModRuntime.LegacyInfo(
                                "[ClientBackup] refuse overwrite local self with empty snapshot");
                            return;
                        }
                    }
                }
                catch { /* write anyway */ }

                string path = GetLocalSelfBackupPath();
                try
                {
                    var parsed = DeserializeFromJson(json);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.CampaignId))
                    {
                        string c = SanitizeCampaignIdForPath(parsed.CampaignId);
                        if (!string.IsNullOrEmpty(c))
                            path = GetProfileBackupDirectory() + "/client_backup_self_" + c + ".json";
                    }
                }
                catch { /* default path */ }

                File.WriteAllText(path, json);
                ModRuntime.LegacyInfo("[ClientBackup] saved local self → " + path);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to save local self: " + ex);
            }
        }

        /// <summary>
        /// Load host-stored backup for a peer (current campaign only).
        /// Prefer SteamID64 → StableClientKey → PlayerId (PlayerId only when
        /// <paramref name="allowPlayerIdFallback"/> — soft reconnect same session).
        /// </summary>
        public static ClientStateBackupData LoadBackupFileForPlayer(
            int playerId,
            ulong steamId = 0,
            string stableClientKey = null,
            bool allowPlayerIdFallback = true)
        {
            try
            {
                ClientStateBackupData data = null;
                string stableKey = SanitizeStableClientKey(stableClientKey);
                string tag = "p" + playerId
                    + (steamId != 0 ? " s" + steamId : "")
                    + (stableKey != null ? " k" + stableKey.Substring(0, Math.Min(8, stableKey.Length)) : "");

                if (steamId != 0)
                {
                    data = TryReadBackup(GetBackupFilePathForSteam(steamId));
                    if (data == null)
                        data = TryReadBackup(GetLegacySteamBackupPath(steamId));
                }
                if (data == null && !string.IsNullOrEmpty(stableKey))
                {
                    data = TryReadBackup(GetBackupFilePathForStableKey(stableKey));
                    if (data == null)
                        data = TryReadBackup(GetLegacyStableBackupPath(stableKey));
                }
                if (data == null && allowPlayerIdFallback && playerId > 0)
                {
                    data = TryReadBackup(GetBackupFilePathForPlayer(playerId));
                    if (data == null)
                        data = TryReadBackup(GetLegacyPlayerBackupPath(playerId));
                }
                // One-shot migrate PlayerId-keyed → Steam / StableClientKey.
                if (data != null && (steamId != 0 || !string.IsNullOrEmpty(stableKey)))
                {
                    try
                    {
                        bool migrated = false;
                        if (steamId != 0 && TryParseSteamId(data.SteamId) != steamId)
                        {
                            data.SteamId = steamId.ToString();
                            migrated = true;
                        }
                        if (!string.IsNullOrEmpty(stableKey)
                            && !string.Equals(data.StableClientKey, stableKey, StringComparison.OrdinalIgnoreCase))
                        {
                            data.StableClientKey = stableKey;
                            migrated = true;
                        }
                        if (migrated)
                        {
                            SaveBackupFile(SerializeToJson(data), playerId, steamId, stableKey);
                            if (steamId != 0)
                                ModRuntime.LegacyInfo("[ClientBackup] migrated " + tag + " → steam key");
                            else
                                ModRuntime.LegacyInfo("[ClientBackup] migrated " + tag + " → LAN k key");
                        }
                    }
                    catch (Exception migEx)
                    {
                        ModRuntime.Log?.LogWarning(
                            "[ClientBackup] key migrate failed: " + migEx.Message);
                    }
                }
                // Never fall back to shared client_backup.json for a remote player id —
                // that file is host/self-shaped and caused wrong-player pushes (p5 got
                // host Jul-9 shotgun backup on 2026-09-26 dual-box).
                if (data == null) return null;
                data = MigrateLegacyCampaignIfNeeded(data, playerId);
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip " + tag
                        + " backup — campaign mismatch (file=(none/mismatched) current="
                        + (CoopWorldCopyMeta.TryGetCurrentCampaignId() ?? "(none)") + ")");
                    return null;
                }
                if (!HasMeaningfulProgress(data))
                {
                    ModRuntime.LegacyInfo("[ClientBackup] skip " + tag + " backup — empty/no progress");
                    return null;
                }
                if (LooksLikeStaleBackupOnFreshWorld(data))
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip " + tag + " backup — stale/legacy-poison snapshot");
                    return null;
                }
                return data;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError(
                    "[ClientBackup] failed to load p" + playerId
                    + (steamId != 0 ? " s" + steamId : "") + ": " + ex);
                return null;
            }
        }

        /// <summary>Load local self backup for the current campaign; legacy fallback if unscoped.</summary>
        public static ClientStateBackupData LoadLocalSelfBackupFile()
        {
            try
            {
                ClientStateBackupData data = TryReadBackup(GetLocalSelfBackupPath());
                if (data == null)
                    data = TryReadBackup(GetLegacySelfBackupPath());
                if (data == null)
                    data = TryReadBackup(GetLegacyBackupFilePath());
                if (data == null) return null;
                data = MigrateLegacyCampaignIfNeeded(data, 0);
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip local self — campaign mismatch (file=(none/mismatched) current="
                        + (CoopWorldCopyMeta.TryGetCurrentCampaignId() ?? "(none)") + ")");
                    return null;
                }
                if (!HasMeaningfulProgress(data))
                {
                    ModRuntime.LegacyInfo("[ClientBackup] skip local self — empty/no progress");
                    return null;
                }
                if (LooksLikeStaleBackupOnFreshWorld(data))
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip local self — stale/legacy-poison snapshot");
                    return null;
                }
                return data;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to load local self: " + ex);
                return null;
            }
        }

        private static ClientStateBackupData TryReadBackup(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            return DeserializeFromJson(json);
        }
    }
}
