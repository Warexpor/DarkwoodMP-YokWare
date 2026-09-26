using System;
using System.IO;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Install-scoped LAN client identity for host backup disk keys when SteamID64 is absent.
    /// Minted once under persistentDataPath (not WorldSaveShare'd). Survives PlayerId reshuffles
    /// without machine-fingerprint false merges across separate game installs.
    /// </summary>
    public static partial class ClientStateBackup
    {
        private const string LanClientKeyFileName = "dwmp_lan_client_key.txt";

        /// <summary>
        /// 32-hex GUID for this install. Stable across cold sessions; distinct per
        /// Steam vs SecondDarkwood persistent roots.
        /// </summary>
        public static string GetOrCreateLanClientKey()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, LanClientKeyFileName);
                if (File.Exists(path))
                {
                    string existing = SanitizeStableClientKey(File.ReadAllText(path));
                    if (!string.IsNullOrEmpty(existing))
                        return existing;
                }
                string minted = Guid.NewGuid().ToString("N");
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, minted);
                ModRuntime.LegacyInfo(
                    "[ClientBackup] minted LAN StableClientKey " + minted.Substring(0, 8) + "…");
                return minted;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ClientBackup] LAN key mint failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Hex-only path-safe key; null if missing/invalid.</summary>
        public static string SanitizeStableClientKey(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var sb = new System.Text.StringBuilder(32);
            string t = raw.Trim();
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                    sb.Append(char.ToLowerInvariant(c));
                if (sb.Length >= 64) break;
            }
            // Accept 8–64 hex (GUID N is 32); reject tiny junk that would collide.
            return sb.Length >= 8 ? sb.ToString() : null;
        }

        /// <summary>Host-side path keyed by StableClientKey + campaign (LAN / no Steam).</summary>
        public static string GetBackupFilePathForStableKey(string stableKey)
        {
            string key = SanitizeStableClientKey(stableKey);
            if (string.IsNullOrEmpty(key))
                return GetLocalSelfBackupPath();
            string campaign = SanitizeCampaignIdForPath(
                CoopWorldCopyMeta.GetOrCreateCampaignIdForCurrentProfile());
            if (string.IsNullOrEmpty(campaign))
                return GetProfileBackupDirectory() + "/client_backup_k" + key + ".json";
            return GetProfileBackupDirectory() + "/client_backup_k" + key + "_" + campaign + ".json";
        }

        private static string GetLegacyStableBackupPath(string stableKey)
        {
            string key = SanitizeStableClientKey(stableKey);
            if (string.IsNullOrEmpty(key)) return null;
            return GetProfileBackupDirectory() + "/client_backup_k" + key + ".json";
        }
    }
}
