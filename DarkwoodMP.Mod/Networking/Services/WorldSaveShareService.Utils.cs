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
        /// <summary>SHA1 of inflated savs+sav package bytes (matches disk fingerprint).</summary>
        private string ComputeUncompressedPackageFingerprint()
        {
            // Build ordered raw blobs: savs.dat then sav.dat (same order as FingerprintFiles).
            byte[] savsRaw = null;
            byte[] savRaw = null;
            for (int i = 0; i < _pendingBegin.FileCount; i++)
            {
                string name = _pendingBegin.FileNames != null && i < _pendingBegin.FileNames.Length
                    ? _pendingBegin.FileNames[i] : "";
                byte[][] chunks = _chunkBuffers[i];
                int totalLen = 0;
                for (int c = 0; c < chunks.Length; c++)
                    totalLen += chunks[c].Length;
                byte[] compressed = new byte[totalLen];
                int off = 0;
                for (int c = 0; c < chunks.Length; c++)
                {
                    Buffer.BlockCopy(chunks[c], 0, compressed, off, chunks[c].Length);
                    off += chunks[c].Length;
                }
                byte[] raw = Inflate(compressed);
                if (string.Equals(name, "savs.dat", StringComparison.OrdinalIgnoreCase))
                    savsRaw = raw;
                else if (string.Equals(name, "sav.dat", StringComparison.OrdinalIgnoreCase))
                    savRaw = raw;
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

        private static byte[] Inflate(byte[] compressed)
        {
            using (var input = new MemoryStream(compressed))
            using (var ds = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                ds.CopyTo(output);
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
