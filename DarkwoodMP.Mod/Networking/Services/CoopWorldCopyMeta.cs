using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DWMPHorde.Logging;
using Newtonsoft.Json;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Sidecar next to sav.dat marking a profile as a permanent co-op world copy.
    /// Updated on every coordinated/local Save so the copy tracks session progress.
    /// Survives until the player deletes the profile (vanilla delete or wipe folder).
    /// </summary>
    [Serializable]
    public sealed partial class CoopWorldCopyMeta
    {
        public const string FileName = "dwmp_coop_meta.json";

        public bool IsCoopCopy = true;
        /// <summary>
        /// The campaign id was minted here for this install's own world (host / solo), not taken
        /// from a host package. Such a profile is never labelled a co-op copy.
        /// </summary>
        public bool OwnCampaign;
        public int HostProfileId;
        public int Chapter;
        public int Day;
        public int WorldSeed;
        public string HostAddress;
        public string JoinedAt;
        public string LastRefreshedAt;
        public string Note;
        /// <summary>SHA1 hex of sav.dat+savs.dat (or join package). Same host package → skip overwrite.</summary>
        public string ContentFingerprint;
        /// <summary>
        /// Stable co-op campaign id (GUID). Minted once per world; never overwritten on
        /// RefreshAfterLocalSave. Client backups and restores are keyed to this — not to
        /// ContentFingerprint (which changes every Save).
        /// </summary>
        public string CampaignId;
        public long SavBytes;
        public long SavsBytes;
        /// <summary>
        /// Dreams the party has played in this world (party-once). Vanilla keeps only each
        /// player's own level slots and the random pool; a played story dream (the bunker) had
        /// no record that outlived the session. Merged on every save, read back by the host.
        /// </summary>
        public System.Collections.Generic.List<string> CompletedDreams;

        /// <summary>
        /// A world received from a host (join pipeline). Older builds also stamped IsCoopCopy on the
        /// host's own campaign; those never carry a source HostProfileId.
        /// </summary>
        [JsonIgnore]
        public bool IsReceivedCoopCopy => IsCoopCopy && !OwnCampaign && HostProfileId > 0;

        public static string PathForProfile(int profileId)
        {
            string root = Application.persistentDataPath + "/1_4Save/prof" + profileId;
            return Path.Combine(root, FileName);
        }

        public static string ProfileDir(int profileId) =>
            Application.persistentDataPath + "/1_4Save/prof" + profileId;

        public static CoopWorldCopyMeta TryLoad(int profileId)
        {
            return Load(profileId, out CoopWorldCopyMeta meta) == LoadResult.Ok ? meta : null;
        }

        public static bool SlotHasSaveFiles(int profileId)
        {
            string root = ProfileDir(profileId);
            return File.Exists(Path.Combine(root, "sav.dat"));
        }

        /// <summary>Fingerprint raw sav.dat + savs.dat bytes (order: savs then sav, if present).</summary>
        public static string FingerprintFiles(string savsPath, string savPath)
        {
            try
            {
                using (var sha = SHA1.Create())
                {
                    HashFile(sha, savsPath);
                    HashFile(sha, savPath);
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return ToHex(sha.Hash);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "FingerprintFiles failed: " + ex.Message);
                return null;
            }
        }

        public static string FingerprintProfileSlot(int profileId)
        {
            string dir = ProfileDir(profileId);
            return FingerprintFiles(
                Path.Combine(dir, "savs.dat"),
                Path.Combine(dir, "sav.dat"));
        }

        /// <summary>
        /// Find local co-op profile whose saved fingerprint matches the host package.
        /// Also verifies on-disk hash when meta fingerprint is present.
        /// </summary>
        public static int FindMatchingSlot(string packageFingerprint, int chapter, int day)
        {
            if (string.IsNullOrEmpty(packageFingerprint))
                return 0;

            for (int id = 1; id <= 5; id++)
            {
                if (!SlotHasSaveFiles(id))
                    continue;
                var meta = TryLoad(id);
                if (meta == null || !meta.IsCoopCopy)
                    continue;
                if (!string.IsNullOrEmpty(meta.ContentFingerprint)
                    && string.Equals(meta.ContentFingerprint, packageFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    ModLog.Event(LogCat.Save,
                        "Same-world match: package fingerprint == meta on slot " + id
                        + " (ch" + chapter + " day" + day + ")");
                    return id;
                }
            }
            return 0;
        }

        /// <summary>
        /// Current profile's stable campaign id, or null if no profile / not stamped yet.
        /// </summary>
        public static string TryGetCurrentCampaignId()
        {
            if (Core.currentProfile == null) return null;
            int pid = Core.currentProfile.id;
            if (pid < 1 || pid > 5) return null;
            return TryLoad(pid)?.CampaignId;
        }

        /// <summary>
        /// Current profile's sav package fingerprint, or null if unset.
        /// </summary>
        public static string TryGetCurrentContentFingerprint()
        {
            if (Core.currentProfile == null) return null;
            int pid = Core.currentProfile.id;
            if (pid < 1 || pid > 5) return null;
            return TryLoad(pid)?.ContentFingerprint;
        }

        /// <summary>
        /// CampaignId already stamped on the active profile, or null. Never mints or writes
        /// (handshake identity check must not invent an id for a foreign save).
        /// </summary>
        public static string TryGetCampaignIdForCurrentProfile()
        {
            if (Core.currentProfile == null) return null;
            int pid = Core.currentProfile.id;
            if (pid < 1 || pid > 5) return null;
            string id = TryLoad(pid)?.CampaignId;
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>
        /// Ensure active profile has a CampaignId (mint if missing). Host + client use this
        /// before writing client backups / sharing world.
        /// </summary>
        public static string GetOrCreateCampaignIdForCurrentProfile()
        {
            if (Core.currentProfile == null) return null;
            return GetOrCreateCampaignId(Core.currentProfile.id);
        }

        /// <summary>
        /// The profile's campaign id, minted (and written) only when the profile has none yet.
        /// An unreadable meta file is never re-minted (see <see cref="SalvageCorrupt"/>).
        /// </summary>
        public static string GetOrCreateCampaignId(int profileId)
        {
            if (profileId < 1 || profileId > 5) return null;
            try
            {
                var meta = LoadOrSalvage(profileId, out bool refused);
                if (refused)
                    return null;
                bool changed = false;
                if (meta == null)
                {
                    // No meta yet: this is the install's own world, not a copy received from a host.
                    meta = new CoopWorldCopyMeta
                    {
                        IsCoopCopy = false,
                        OwnCampaign = true,
                        JoinedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                        Note = "Campaign id auto-stamped."
                    };
                    changed = true;
                }
                else if (!string.IsNullOrEmpty(meta.Note) && meta.Note.StartsWith("Recovered", StringComparison.Ordinal))
                {
                    changed = true; // rewrite the salvaged id over the unreadable file
                }

                if (string.IsNullOrEmpty(meta.CampaignId))
                {
                    meta.CampaignId = Guid.NewGuid().ToString("N");
                    changed = true;
                    ModLog.Event(LogCat.Save,
                        "Minted CampaignId " + meta.CampaignId.Substring(0, 8) + "… for prof" + profileId);
                }

                if (changed)
                    Write(profileId, meta);
                return meta.CampaignId;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "GetOrCreateCampaignId failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Force a new campaign id (brand-new world gen on this profile).</summary>
        /// <param name="newWorld">A freshly generated world: the slot's played dreams belonged to
        /// the world before and are dropped. False for another copy of the same world (manual
        /// slot load), whose dreams were played.</param>
        public static string MintNewCampaignId(int profileId, bool newWorld = false)
        {
            if (profileId < 1 || profileId > 5) return null;
            try
            {
                // Deliberate re-mint (new world / manual slot load). A corrupt file is kept as .bad.
                var meta = LoadOrSalvage(profileId, out _) ?? new CoopWorldCopyMeta
                {
                    JoinedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };
                string prev = meta.CampaignId;
                meta.CampaignId = Guid.NewGuid().ToString("N");
                // A world generated or loaded here is this install's own campaign.
                meta.IsCoopCopy = false;
                meta.OwnCampaign = true;
                meta.HostProfileId = 0;
                meta.Note = "New world — new campaign id.";
                // The old world's played dreams stayed in the slot: a new world counted the bunker
                // as played, and the first level-2 dream rolled a random one instead.
                if (newWorld)
                    meta.CompletedDreams = new System.Collections.Generic.List<string>();
                Write(profileId, meta);
                ModLog.Event(LogCat.Save,
                    "MintNewCampaignId prof" + profileId
                    + (string.IsNullOrEmpty(prev) ? "" : (" (was " + prev.Substring(0, Math.Min(8, prev.Length)) + "…)"))
                    + " → " + meta.CampaignId.Substring(0, 8) + "…");
                return meta.CampaignId;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "MintNewCampaignId failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Set CampaignId on a profile (join pipeline from host Begin).</summary>
        public static void SetCampaignId(int profileId, string campaignId)
        {
            if (profileId < 1 || profileId > 5 || string.IsNullOrEmpty(campaignId))
                return;
            try
            {
                var meta = LoadOrSalvage(profileId, out _) ?? new CoopWorldCopyMeta
                {
                    IsCoopCopy = true,
                    JoinedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };
                meta.OwnCampaign = false;
                // Prefer host-authoritative id; do not replace with a different one mid-campaign.
                if (!string.IsNullOrEmpty(meta.CampaignId)
                    && !string.Equals(meta.CampaignId, campaignId, StringComparison.OrdinalIgnoreCase))
                {
                    ModLog.Event(LogCat.Save,
                        "CampaignId replace on prof" + profileId + ": "
                        + meta.CampaignId.Substring(0, Math.Min(8, meta.CampaignId.Length))
                        + " → " + campaignId.Substring(0, Math.Min(8, campaignId.Length)));
                    // Another world landed in this slot: its played dreams are the host's, not the old world's.
                    meta.CompletedDreams = null;
                }
                meta.CampaignId = campaignId;
                meta.IsCoopCopy = true;
                Write(profileId, meta);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "SetCampaignId failed: " + ex.Message);
            }
        }

        /// <summary>
        /// After any successful local Save on a co-op profile: refresh meta + content fingerprint.
        /// Keeps the permanent local copy identity in sync with on-disk sav files.
        /// </summary>
        public static void RefreshAfterLocalSave()
        {
            try
            {
                if (Core.currentProfile == null)
                    return;
                int pid = Core.currentProfile.id;
                if (pid < 1 || pid > 5)
                    return;

                var meta = LoadOrSalvage(pid, out bool refused);
                if (refused)
                    return; // unreadable meta with no recoverable id: never re-mint behind the user's back
                // Always stamp if files exist under this profile — join copy or host campaign in co-op.
                if (meta == null)
                {
                    if (!SlotHasSaveFiles(pid))
                        return;
                    // A connected client saving a profile without meta is on a received copy;
                    // the host (or solo) is saving its own campaign.
                    bool client = ModRuntime.Network != null
                        && ModRuntime.Network.Role == NetworkRole.Client;
                    meta = new CoopWorldCopyMeta
                    {
                        IsCoopCopy = client,
                        OwnCampaign = !client,
                        JoinedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                        Note = "Co-op session save (auto-stamped)."
                    };
                }

                // Preserve stable campaign id across Save fingerprint churn.
                if (string.IsNullOrEmpty(meta.CampaignId))
                    meta.CampaignId = Guid.NewGuid().ToString("N");

                string dir = ProfileDir(pid);
                string sav = Path.Combine(dir, "sav.dat");
                string savs = Path.Combine(dir, "savs.dat");

                // Played dreams only grow (union): a client copy keeps the host's set too, so a
                // copy promoted to host by migration still knows them.
                string[] played = Sync.DreamSession.GetCompletedPresets();
                if (played.Length > 0)
                {
                    if (meta.CompletedDreams == null)
                        meta.CompletedDreams = new System.Collections.Generic.List<string>();
                    foreach (string n in played)
                    {
                        if (!string.IsNullOrEmpty(n) && !meta.CompletedDreams.Contains(n))
                            meta.CompletedDreams.Add(n);
                    }
                }

                meta.LastRefreshedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                meta.Day = Core.currentProfile.day;
                meta.Chapter = Core.currentProfile.chapter;
                meta.WorldSeed = meta.Chapter * 100000 + meta.Day;
                meta.SavBytes = File.Exists(sav) ? new FileInfo(sav).Length : 0;
                meta.SavsBytes = File.Exists(savs) ? new FileInfo(savs).Length : 0;

                // Hashing the ~12 MB of save files took a few hundred ms on the main thread: a
                // hitch at every save on every peer. It runs on a worker and lands a moment later.
                // Meanwhile the previous one stays: a client's state backup taken in between
                // needs one (a backup without it is refused as a template).
                Controller ctl = Singleton<Controller>.Instance;
                if (WorldSaveGuards.IsQuitting || ctl == null)
                {
                    meta.ContentFingerprint = FingerprintProfileSlot(pid);
                    Write(pid, meta);
                    LogRefreshed(pid, meta);
                    return;
                }
                Write(pid, meta);
                int gen = ++_fingerprintGeneration;
                ctl.StartCoroutine(FingerprintInBackground(pid, gen, savs, sav));
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "RefreshAfterLocalSave failed: " + ex.Message);
            }
        }

        private static int _fingerprintGeneration; // process-scoped: newest pending refresh wins

        private struct FileStamp
        {
            public long Length;
            public long WriteTicks;

            public static FileStamp Of(string path)
            {
                var fi = new FileInfo(path);
                return fi.Exists ? new FileStamp { Length = fi.Length, WriteTicks = fi.LastWriteTimeUtc.Ticks } : default;
            }

            public bool Same(FileStamp o) => Length == o.Length && WriteTicks == o.WriteTicks;
        }

        private static System.Collections.IEnumerator FingerprintInBackground(int pid, int gen, string savs, string sav)
        {
            FileStamp savsBefore = FileStamp.Of(savs), savBefore = FileStamp.Of(sav);
            string fp = null;
            bool done = false;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { fp = FingerprintFilesQuiet(savs, sav); }
                catch { fp = null; }
                finally { done = true; }
            });
            while (!done)
                yield return null;
            // A newer save started its own hash, or the files changed under this one.
            if (gen != _fingerprintGeneration || fp == null)
                yield break;
            if (!savsBefore.Same(FileStamp.Of(savs)) || !savBefore.Same(FileStamp.Of(sav)))
                yield break;
            try
            {
                var meta = LoadOrSalvage(pid, out bool refused);
                if (refused || meta == null)
                    yield break;
                meta.ContentFingerprint = fp;
                meta.SavBytes = savBefore.Length;
                meta.SavsBytes = savsBefore.Length;
                Write(pid, meta);
                LogRefreshed(pid, meta);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "RefreshAfterLocalSave fingerprint failed: " + ex.Message);
            }
        }

        /// <summary><see cref="FingerprintFiles"/> without logging (worker thread).</summary>
        private static string FingerprintFilesQuiet(string savsPath, string savPath)
        {
            using (var sha = SHA1.Create())
            {
                HashFile(sha, savsPath);
                HashFile(sha, savPath);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static void LogRefreshed(int pid, CoopWorldCopyMeta meta)
        {
            string fp = meta.ContentFingerprint;
            ModLog.Event(LogCat.Save,
                "Permanent co-op copy updated after Save → slot " + pid
                + " day=" + meta.Day + " ch=" + meta.Chapter
                + " campaign=" + (meta.CampaignId.Length > 8 ? meta.CampaignId.Substring(0, 8) : meta.CampaignId)
                + " fp=" + (fp != null && fp.Length > 12 ? fp.Substring(0, 12) : fp)
                + " sav=" + meta.SavBytes + "b savs=" + meta.SavsBytes + "b");
        }

        private static void HashFile(HashAlgorithm sha, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                byte[] z = BitConverter.GetBytes(0);
                sha.TransformBlock(z, 0, z.Length, null, 0);
                return;
            }
            byte[] len = BitConverter.GetBytes(new FileInfo(path).Length);
            sha.TransformBlock(len, 0, len.Length, null, 0);
            using (var fs = File.OpenRead(path))
            {
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    sha.TransformBlock(buf, 0, n, null, 0);
            }
        }

        private static string ToHex(byte[] hash)
        {
            if (hash == null) return null;
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>UI/listing snapshot for one PLAY profile slot.</summary>
    public struct ProfileSlotInfo
    {
        public int Id;
        public bool HasSave;
        public bool IsCoopCopy;
        public bool IsEmpty;
        public bool MatchesIncomingPackage;
        /// <summary>Slot meta CampaignId differs from the host package CampaignId.</summary>
        public bool CampaignMismatchWithHost;
        public int Day;
        public int Chapter;
        public string TimeSaved;
        public string CoopNote;
    }
}
