using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Package fingerprint and deflate helpers (split for ownership).
    /// </summary>
    public sealed partial class WorldSaveShareService
    {
        /// <summary>Hard ceiling for one inflated save file (a real sav.dat is a few MB).</summary>
        internal const int MaxInflatedFileBytes = 64 * 1024 * 1024;

        /// <summary>SHA1 of the verified (inflated) savs+sav bytes; matches the disk fingerprint.</summary>
        private static string ComputePackageFingerprint(List<VerifiedFile> files)
        {
            // Same order as CoopWorldCopyMeta.FingerprintFiles: savs.dat then sav.dat.
            byte[] savsRaw = null;
            byte[] savRaw = null;
            for (int i = 0; i < files.Count; i++)
            {
                if (string.Equals(files[i].Name, "savs.dat", StringComparison.OrdinalIgnoreCase))
                    savsRaw = files[i].Raw;
                else if (string.Equals(files[i].Name, "sav.dat", StringComparison.OrdinalIgnoreCase))
                    savRaw = files[i].Raw;
            }

            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                HashRaw(sha, savsRaw);
                HashRaw(sha, savRaw);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = sha.Hash;
                if (hash == null) return null;
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Host: every save file of the profile, read in one synchronous step (name order of
        /// <see cref="FileNames"/>). Opened share-friendly so a reader never trips a sharing
        /// violation against the game's own handle; throws if any present file cannot be read.
        /// </summary>
        private static List<KeyValuePair<string, byte[]>> ReadSaveSetSnapshot(string profDir)
        {
            var result = new List<KeyValuePair<string, byte[]>>(FileNames.Length);
            foreach (string name in FileNames)
            {
                string path = Path.Combine(profDir, name);
                if (!File.Exists(path))
                {
                    ModLog.Event(LogCat.Save, "Share skip missing file: " + path);
                    continue;
                }
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    if (len > MaxInflatedFileBytes)
                        throw new IOException(name + " is too large to share (" + len + " bytes)");
                    var buf = new byte[len];
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int n = fs.Read(buf, off, buf.Length - off);
                        if (n <= 0)
                            throw new IOException("short read on " + name);
                        off += n;
                    }
                    result.Add(new KeyValuePair<string, byte[]>(name, buf));
                }
            }
            return result;
        }

        private static void HashRaw(System.Security.Cryptography.HashAlgorithm sha, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                byte[] z = BitConverter.GetBytes(0L);
                sha.TransformBlock(z, 0, z.Length, null, 0);
                return;
            }
            byte[] len = BitConverter.GetBytes((long)data.Length);
            sha.TransformBlock(len, 0, len.Length, null, 0);
            sha.TransformBlock(data, 0, data.Length, null, 0);
        }

        /// <summary>Local slot whose on-disk sav/savs hash equals package fingerprint.</summary>
        private static int FindLocalSlotWithSameWorld(string packageFp)
        {
            if (string.IsNullOrEmpty(packageFp))
                return 0;

            // Prefer meta fingerprint match first (fast).
            int fromMeta = CoopWorldCopyMeta.FindMatchingSlot(packageFp, 0, 0);
            if (fromMeta > 0)
            {
                // Verify disk still matches (player may have deleted files).
                string disk = CoopWorldCopyMeta.FingerprintProfileSlot(fromMeta);
                if (string.Equals(disk, packageFp, StringComparison.OrdinalIgnoreCase))
                    return fromMeta;
            }

            // Full scan: any slot with identical sav files (even without meta).
            for (int id = MinProfileId; id <= MaxProfileId; id++)
            {
                if (!CoopWorldCopyMeta.SlotHasSaveFiles(id))
                    continue;
                string disk = CoopWorldCopyMeta.FingerprintProfileSlot(id);
                if (string.Equals(disk, packageFp, StringComparison.OrdinalIgnoreCase))
                {
                    ModLog.Event(LogCat.Save,
                        "Same-world match via disk fingerprint on slot " + id);
                    return id;
                }
            }
            return 0;
        }

        private static byte[] Deflate(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                    ds.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Inflate with a hard output cap: <paramref name="declaredSize"/> when the sender declared
        /// one, else <see cref="MaxInflatedFileBytes"/>. A stream that would exceed it (a deflate
        /// bomb or a lying header) fails without allocating past the cap.
        /// </summary>
        private static byte[] Inflate(byte[] compressed, int declaredSize)
        {
            int cap = declaredSize > 0 ? Math.Min(declaredSize, MaxInflatedFileBytes) : MaxInflatedFileBytes;
            using (var input = new MemoryStream(compressed))
            using (var ds = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream(declaredSize > 0 ? cap : 64 * 1024))
            {
                byte[] buf = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = ds.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > cap)
                        throw new InvalidDataException("inflated data exceeds " + cap + " bytes");
                    output.Write(buf, 0, n);
                }
                return output.ToArray();
            }
        }

        private sealed class PackedFile
        {
            public string Name;
            public int UncompressedSize;
            public int CompressedSize;
            public byte[][] Chunks;
        }
    }
}
