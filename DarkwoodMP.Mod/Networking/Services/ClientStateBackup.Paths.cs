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

        /// <summary>Save a remote client's backup on the host (or any peer-keyed store).</summary>
        public static void SaveBackupFile(string json, int playerId)
        {
            try
            {
                // Ensure JSON CampaignId matches disk key when host stamps current campaign.
                try
                {
                    var parsed = DeserializeFromJson(json);
                    if (parsed != null)
                    {
                        string cur = CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile();
                        if (!string.IsNullOrEmpty(cur)
                            && !string.Equals(parsed.CampaignId, cur, StringComparison.OrdinalIgnoreCase))
                        {
                            // Prefer payload's campaign if set (client's view); else stamp host.
                            if (string.IsNullOrEmpty(parsed.CampaignId))
                            {
                                parsed.CampaignId = cur;
                                json = SerializeToJson(parsed);
                            }
                        }
                    }
                }
                catch { /* keep raw json */ }

                string path = GetBackupFilePathForPlayer(playerId);
                // If JSON has its own CampaignId, write under that key (host world may differ
                // only if misconfigured; prefer the embedded ID for the file name).
                try
                {
                    var parsed = DeserializeFromJson(json);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.CampaignId) && playerId > 0)
                    {
                        string c = SanitizeCampaignIdForPath(parsed.CampaignId);
                        if (!string.IsNullOrEmpty(c))
                            path = GetProfileBackupDirectory() + "/client_backup_p" + playerId + "_" + c + ".json";
                    }
                }
                catch { /* use path from GetBackupFilePathForPlayer */ }

                File.WriteAllText(path, json);
                ModRuntime.LegacyInfo("[ClientBackup] saved player " + playerId + " → " + path);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to save player " + playerId + ": " + ex);
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

        /// <summary>Load host-stored backup for a specific network player id (current campaign only).</summary>
        public static ClientStateBackupData LoadBackupFileForPlayer(int playerId)
        {
            try
            {
                string path = GetBackupFilePathForPlayer(playerId);
                ClientStateBackupData data = TryReadBackup(path);
                if (data == null && playerId > 0)
                    data = TryReadBackup(GetLegacyPlayerBackupPath(playerId));
                if (data == null)
                {
                    string legacy = GetLegacyBackupFilePath();
                    if (playerId > 0)
                        data = TryReadBackup(legacy);
                }
                if (data == null) return null;
                data = MigrateLegacyCampaignIfNeeded(data, playerId);
                if (data == null)
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip p" + playerId
                        + " backup — campaign mismatch (file=(none/mismatched) current="
                        + (CoopWorldCopyMeta.TryGetCurrentCampaignId() ?? "(none)") + ")");
                    return null;
                }
                if (!HasMeaningfulProgress(data))
                {
                    ModRuntime.LegacyInfo(
                        "[ClientBackup] skip p" + playerId + " backup — empty/no progress");
                    return null;
                }
                return data;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogError("[ClientBackup] failed to load player " + playerId + ": " + ex);
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
