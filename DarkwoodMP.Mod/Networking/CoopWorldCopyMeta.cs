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
    public sealed class CoopWorldCopyMeta
    {
        public const string FileName = "dwmp_coop_meta.json";

        public bool IsCoopCopy = true;
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
        public long SavBytes;
        public long SavsBytes;

        public static string PathForProfile(int profileId)
        {
            string root = Application.persistentDataPath + "/1_4Save/prof" + profileId;
            return Path.Combine(root, FileName);
        }

        public static string ProfileDir(int profileId) =>
            Application.persistentDataPath + "/1_4Save/prof" + profileId;

        public static CoopWorldCopyMeta TryLoad(int profileId)
        {
            try
            {
                string path = PathForProfile(profileId);
                if (!File.Exists(path))
                    return null;
                return JsonConvert.DeserializeObject<CoopWorldCopyMeta>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "CoopWorldCopyMeta load slot " + profileId + ": " + ex.Message);
                return null;
            }
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
                File.WriteAllText(path, JsonConvert.SerializeObject(meta, Formatting.Indented));
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "CoopWorldCopyMeta write slot " + profileId + ": " + ex.Message);
            }
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
        /// Fingerprint in-memory join package (compressed chunks in file order).
        /// Same bytes as host packed → match local disk copy without re-write.
        /// </summary>
        public static string FingerprintPackage(
            int chapter, int day,
            int[] uncompressedSizes,
            System.Collections.Generic.Dictionary<int, byte[][]> chunkBuffers,
            int fileCount)
        {
            try
            {
                using (var sha = SHA1.Create())
                {
                    byte[] header = Encoding.UTF8.GetBytes("ch" + chapter + "|d" + day + "|");
                    sha.TransformBlock(header, 0, header.Length, null, 0);
                    for (int i = 0; i < fileCount; i++)
                    {
                        int usize = uncompressedSizes != null && i < uncompressedSizes.Length
                            ? uncompressedSizes[i] : 0;
                        byte[] sz = BitConverter.GetBytes(usize);
                        sha.TransformBlock(sz, 0, sz.Length, null, 0);
                        if (chunkBuffers == null || !chunkBuffers.TryGetValue(i, out byte[][] chunks) || chunks == null)
                            continue;
                        for (int c = 0; c < chunks.Length; c++)
                        {
                            if (chunks[c] == null || chunks[c].Length == 0) continue;
                            sha.TransformBlock(chunks[c], 0, chunks[c].Length, null, 0);
                        }
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return ToHex(sha.Hash);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "FingerprintPackage failed: " + ex.Message);
                return null;
            }
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

                var meta = TryLoad(pid);
                // Always stamp if files exist under this profile — join copy or host campaign in co-op.
                if (meta == null)
                {
                    if (!SlotHasSaveFiles(pid))
                        return;
                    meta = new CoopWorldCopyMeta
                    {
                        IsCoopCopy = true,
                        JoinedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                        Note = "Co-op session save (auto-stamped)."
                    };
                }

                string fp = FingerprintProfileSlot(pid);
                string dir = ProfileDir(pid);
                string sav = Path.Combine(dir, "sav.dat");
                string savs = Path.Combine(dir, "savs.dat");

                meta.IsCoopCopy = true;
                meta.LastRefreshedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                meta.Day = Core.currentProfile.day;
                meta.Chapter = Core.currentProfile.chapter;
                meta.WorldSeed = meta.Chapter * 100000 + meta.Day;
                meta.ContentFingerprint = fp;
                meta.SavBytes = File.Exists(sav) ? new FileInfo(sav).Length : 0;
                meta.SavsBytes = File.Exists(savs) ? new FileInfo(savs).Length : 0;
                Write(pid, meta);

                ModLog.Event(LogCat.Save,
                    "Permanent co-op copy updated after Save → slot " + pid
                    + " day=" + meta.Day + " ch=" + meta.Chapter
                    + " fp=" + (fp != null && fp.Length > 12 ? fp.Substring(0, 12) : fp)
                    + " sav=" + meta.SavBytes + "b savs=" + meta.SavsBytes + "b");
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "RefreshAfterLocalSave failed: " + ex.Message);
            }
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
        public int Day;
        public int Chapter;
        public string TimeSaved;
        public string CoopNote;
    }
}
