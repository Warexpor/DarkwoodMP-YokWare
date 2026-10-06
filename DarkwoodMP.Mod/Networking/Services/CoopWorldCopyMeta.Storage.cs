using System;
using System.IO;
using DWMPHorde.Logging;
using Newtonsoft.Json;

namespace DWMPHorde.Networking
{
    /// <summary>Meta file IO: corrupt-file handling and atomic, change-only writes.</summary>
    public sealed partial class CoopWorldCopyMeta
    {
        private enum LoadResult { Missing, Ok, Corrupt }

        private static LoadResult Load(int profileId, out CoopWorldCopyMeta meta)
        {
            meta = null;
            string path = PathForProfile(profileId);
            try
            {
                if (!File.Exists(path))
                    return LoadResult.Missing;
                meta = JsonConvert.DeserializeObject<CoopWorldCopyMeta>(File.ReadAllText(path));
                return meta != null ? LoadResult.Ok : LoadResult.Corrupt;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "CoopWorldCopyMeta load slot " + profileId + ": " + ex.Message);
                return File.Exists(path) ? LoadResult.Corrupt : LoadResult.Missing;
            }
        }

        /// <summary>
        /// The meta file exists but does not parse. Its campaign id is what the client backups and
        /// the peers' handshakes are keyed to, so it is never silently replaced: keep a copy of the
        /// bad file and recover the id from its text when it is still there. Null = refuse.
        /// </summary>
        private static CoopWorldCopyMeta SalvageCorrupt(int profileId)
        {
            string path = PathForProfile(profileId);
            try
            {
                string bad = path + ".bad";
                if (!File.Exists(bad))
                    File.Copy(path, bad);
                string text = File.ReadAllText(path);
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    "\"CampaignId\"\\s*:\\s*\"([0-9a-fA-F]{32})\"");
                if (!m.Success)
                {
                    ModLog.Error(LogCat.Save,
                        "Co-op meta for prof" + profileId + " is unreadable and holds no campaign id — "
                        + "left as is (copy: " + bad + "); fix or delete it to mint a new campaign");
                    return null;
                }
                ModLog.Warn(LogCat.Save,
                    "Co-op meta for prof" + profileId + " was unreadable — recovered campaign id "
                    + m.Groups[1].Value.Substring(0, 8) + "… (copy of bad file: " + bad + ")");
                return new CoopWorldCopyMeta
                {
                    IsCoopCopy = false,
                    OwnCampaign = false,
                    CampaignId = m.Groups[1].Value.ToLowerInvariant(),
                    Note = "Recovered from an unreadable meta file."
                };
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "Co-op meta salvage failed for prof" + profileId, ex);
                return null;
            }
        }

        /// <summary>Loaded meta, salvaged meta for a corrupt file, or null (missing / refused).</summary>
        private static CoopWorldCopyMeta LoadOrSalvage(int profileId, out bool refused)
        {
            refused = false;
            LoadResult r = Load(profileId, out CoopWorldCopyMeta meta);
            if (r == LoadResult.Ok)
                return meta;
            if (r == LoadResult.Missing)
                return null;
            meta = SalvageCorrupt(profileId);
            refused = meta == null;
            return meta;
        }

        public static void Write(int profileId, CoopWorldCopyMeta meta)
        {
            if (meta == null) return;
            try
            {
                string path = PathForProfile(profileId);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                string json = JsonConvert.SerializeObject(meta, Formatting.Indented);
                try
                {
                    if (File.Exists(path) && string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
                        return; // unchanged — no rewrite
                }
                catch { /* rewrite */ }
                WriteAllTextAtomic(path, json);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "CoopWorldCopyMeta write slot " + profileId + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Write through a temp file and swap it in, so a crash leaves the old or the new file,
        /// never a truncated one.
        /// </summary>
        internal static void WriteAllTextAtomic(string path, string text)
        {
            string tmp = path + ".dwmp_new";
            File.WriteAllText(tmp, text);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null);
                    return;
                }
                catch (Exception)
                {
                    // File.Replace is unsupported on some file systems: fall back to delete + move.
                    File.Delete(path);
                }
            }
            File.Move(tmp, path);
        }
    }
}
