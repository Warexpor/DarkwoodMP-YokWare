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
    /// Client receive handlers and apply coroutine (split for ownership).
    /// </summary>
    public sealed partial class WorldSaveShareService
    {
        public void HandleBegin(WorldSaveBeginMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;

            // Already in chapter; ignore resend because it would LoadScene and wipe the session.
            if (!Core.mainMenu && Player.Instance != null)
            {
                ModLog.Event(LogCat.Save, "Ignoring world share begin — already in game");
                return;
            }

            // Already have the package or permanent copy ready; ignore duplicate host resends
            // (title-wait WorldRequest used to force a second download + overwrite).
            if (_awaitingSlotPick || _awaitingEnterWorld)
            {
                ModLog.Event(LogCat.Save,
                    "Ignoring world share begin — already "
                    + (_awaitingSlotPick ? "awaiting slot pick" : "awaiting ENTER WORLD"));
                return;
            }

            _clientReceiving = true;
            _clientApplying = false;
            _awaitingSlotPick = false;
            _awaitingEnterWorld = false;
            _pendingBegin = msg;
            // Host profile ID is metadata only; the client picks a permanent local slot after download.
            _hostSourceProfileId = msg.ProfileId;
            if (_hostSourceProfileId < MinProfileId || _hostSourceProfileId > MaxProfileId)
                _hostSourceProfileId = 0;
            _chunksReceived = 0;
            _chunksExpected = 0;
            _chunkBuffers = new Dictionary<int, byte[][]>();

            for (int i = 0; i < msg.FileCount; i++)
            {
                int n = msg.ChunkCounts != null && i < msg.ChunkCounts.Length ? msg.ChunkCounts[i] : 0;
                if (n < 0 || n > 100000) n = 0;
                _chunkBuffers[i] = new byte[n][];
                _chunksExpected += n;
            }

            ProgressText = "Receiving host world…";
            _net.StatusText = ProgressText;
            ModLog.Event(LogCat.Save,
                "Receiving host world (host slot " + _hostSourceProfileId
                + "): " + msg.FileCount + " files, " + _chunksExpected + " chunks, ch"
                + msg.ChapterId + " day" + msg.DayIndex
                + " — client will pick permanent local profile after download");
        }

        public void HandleChunk(WorldSaveChunkMessage msg)
        {
            if (_net.Role != NetworkRole.Client || !_clientReceiving || _chunkBuffers == null)
                return;

            if (!_chunkBuffers.TryGetValue(msg.FileIndex, out byte[][] chunks))
                return;
            if (msg.ChunkIndex < 0 || msg.ChunkIndex >= chunks.Length)
                return;
            if (msg.Data == null)
                return;

            if (chunks[msg.ChunkIndex] == null)
                _chunksReceived++;
            chunks[msg.ChunkIndex] = msg.Data;

            if (_chunksExpected > 0)
            {
                ProgressText = "Receiving host world "
                    + (int)(100f * _chunksReceived / _chunksExpected) + "%";
                _net.StatusText = ProgressText;
            }
        }

        public void HandleEnd(WorldSaveEndMessage msg)
        {
            if (_net.Role != NetworkRole.Client || !_clientReceiving)
                return;

            _clientReceiving = false;

            if (!msg.Success)
            {
                string loud = WorldSharePolicy.FormatShareFailure("host reported failure");
                ProgressText = loud;
                _net.StatusText = loud;
                ModLog.Error(LogCat.Save, "Host reported world share failure");
                return;
            }

            _net.StartCoroutine(ClientApplyCoroutine());
        }

        private IEnumerator ClientApplyCoroutine()
        {
            _clientApplying = true;
            ProgressText = "Verifying host world package…";
            _net.StatusText = ProgressText;
            yield return null;

            // Verify chunks, then hold them in RAM until the user picks a permanent local profile slot.
            for (int i = 0; i < _pendingBegin.FileCount; i++)
            {
                if (!_chunkBuffers.TryGetValue(i, out byte[][] chunks))
                {
                    FailClientApply("Missing file buffer " + i);
                    yield break;
                }
                for (int c = 0; c < chunks.Length; c++)
                {
                    if (chunks[c] == null)
                    {
                        FailClientApply("Missing chunk " + i + ":" + c);
                        yield break;
                    }
                }
            }

            // Compare the uncompressed package with local permanent copies; skip overwrite
            // when the client already has the exact same world save on disk.
            ProgressText = "Checking for matching local world…";
            if (_net != null)
                _net.StatusText = ProgressText;
            yield return null;

            string packageFp = null;
            try { packageFp = ComputeUncompressedPackageFingerprint(); }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "Package fingerprint failed: " + ex.Message);
            }
            _pendingPackageFingerprint = packageFp;

            int matchSlot = 0;
            if (!string.IsNullOrEmpty(packageFp))
            {
                matchSlot = FindLocalSlotWithSameWorld(packageFp);
            }

            if (matchSlot >= MinProfileId && matchSlot <= MaxProfileId)
            {
                GameProfile target = EnsureProfileSlot(matchSlot, _pendingBegin.DayIndex, _pendingBegin.ChapterId);
                Core.currentProfile = target;
                try
                {
                    SaveManager sm = Singleton<SaveManager>.Instance;
                    if (sm != null)
                        sm.updateFilePaths();
                }
                catch { /* ignore */ }

                MergeProfileIntoDiskIndexAndSave(target);
                Core.currentProfile = target;

                // Keep meta fingerprint current (same package, no rewrite).
                var meta = CoopWorldCopyMeta.TryLoad(matchSlot) ?? new CoopWorldCopyMeta
                {
                    IsCoopCopy = true,
                    JoinedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };
                meta.IsCoopCopy = true;
                meta.HostProfileId = _hostSourceProfileId;
                meta.Chapter = _pendingBegin.ChapterId;
                meta.Day = _pendingBegin.DayIndex;
                meta.WorldSeed = _pendingBegin.ChapterId * 100000 + _pendingBegin.DayIndex;
                meta.ContentFingerprint = packageFp;
                meta.LastRefreshedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                meta.Note = "Exact match with host package — reused permanent copy (no overwrite).";
                if (!string.IsNullOrEmpty(_pendingBegin.CampaignId))
                    meta.CampaignId = _pendingBegin.CampaignId;
                else if (string.IsNullOrEmpty(meta.CampaignId))
                    meta.CampaignId = CoopWorldCopyMeta.GetOrCreateCampaignId(matchSlot);
                CoopWorldCopyMeta.Write(matchSlot, meta);

                int chapterId = _pendingBegin.ChapterId > 0 ? _pendingBegin.ChapterId : 1;
                _chunkBuffers = null;
                _clientApplying = false;
                _awaitingSlotPick = false;
                _awaitingEnterWorld = true;
                _enterProfileId = matchSlot;
                _enterChapterId = chapterId;

                ProgressText = "Same world already on Profile " + matchSlot + " — press ENTER WORLD";
                if (_net != null)
                    _net.StatusText = ProgressText;
                ModLog.Event(LogCat.Session,
                    "Join pipeline: exact same world on slot " + matchSlot
                    + " — skipped overwrite, waiting ENTER WORLD");
                yield break;
            }

            _clientApplying = false;
            _awaitingSlotPick = true;
            ProgressText = "Pick a profile slot for permanent world copy";
            if (_net != null)
                _net.StatusText = ProgressText;
            ModLog.Event(LogCat.Session,
                "Join pipeline: package verified (ch" + _pendingBegin.ChapterId
                + " day" + _pendingBegin.DayIndex
                + ") — waiting for permanent profile slot pick before ENTER WORLD"
                + (string.IsNullOrEmpty(packageFp) ? "" : " fp=" + packageFp.Substring(0, Math.Min(12, packageFp.Length))));
        }

        private void FailClientApply(string reason)
        {
            // Stop rather than silently continuing into a different world.
            string loud = WorldSharePolicy.FormatShareFailure(reason);
            ModLog.Error(LogCat.Save, "Failed to apply host world: " + reason);
            ProgressText = loud;
            if (_net != null)
                _net.StatusText = loud;
            _chunkBuffers = null;
            _clientApplying = false;
            _clientReceiving = false;
            _awaitingSlotPick = false;
            _awaitingEnterWorld = false;
        }
    }
}
