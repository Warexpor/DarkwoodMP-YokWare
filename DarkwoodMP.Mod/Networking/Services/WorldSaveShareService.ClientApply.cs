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

            // World package start implies host was ready (compat if HostWorldReady missed).
            _net.NoteClientHostWorldReadyFromShareBegin();

            // Already in a chapter. A join resend must not wipe the session.
            // A host chapter change is the exception: the new save has to land.
            if (!GameScreen.AtTitle && Player.Instance != null
                && !Patches.ChapterTransitionHelpers.ChapterShareExpected)
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

            // The host re-ran a chapter share (a broadcast queued behind a running share): the
            // package being verified or held for the go is the older one. Supersede it; the apply
            // coroutine of the old package quits on the generation bump.
            if (Patches.ChapterTransitionHelpers.ChapterShareExpected
                && !_clientReceiving && (_clientApplying || _awaitingChapterGo))
            {
                ModLog.Event(LogCat.Save,
                    "World share begin supersedes the " + (_awaitingChapterGo ? "held" : "verifying")
                    + " chapter package (host re-ran the share)");
                _shareGeneration++;
                _clientApplying = false;
                _awaitingChapterGo = false;
                _verifiedPackage = null;
                _chunkBuffers = null;
                // The go-wait armed for the superseded package must not accept a go meant for this one.
                Patches.ChapterTransitionHelpers.ClientShareSuperseded();
            }

            // Overlapping Begin (auto-share + WorldRequest chain) used to allocate a fresh
            // null _chunkBuffers while apply still verified the prior package → Missing chunk 0:0.
            if (_clientReceiving || _clientApplying)
            {
                ModLog.Event(LogCat.Save,
                    "Ignoring world share begin — already "
                    + (_clientReceiving ? "receiving" : "applying")
                    + " a package (duplicate host push)");
                return;
            }

            _clientReceiving = true;
            _clientApplying = false;
            _awaitingSlotPick = false;
            _awaitingEnterWorld = false;
            _pendingBegin = msg;
            Sync.PersonalPrologue.ClearJoiner();
            Sync.PersonalPrologue.NoteOffered(msg.PrologueOffered, msg.CampaignId,
                joinFromTitle: GameScreen.AtTitle && !Patches.ChapterTransitionHelpers.ChapterShareExpected);
            Patches.ChapterTransitionHelpers.ClientNoteSharePass(msg.SharePass);
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
                // A file never needs more chunks than the inflate cap allows; a larger count is a
                // corrupt header (and would reserve unbounded memory before any data arrived).
                if (n < 0 || n > MaxInflatedFileBytes / ChunkSize + 1) n = 0;
                _chunkBuffers[i] = new byte[n][];
                _chunksExpected += n;
            }

            ProgressText = "Receiving host world…";
            _net.StatusText = ProgressText;
            Patches.ChapterTransitionHelpers.ClientShareProgress(force: true);
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
            if (msg.Data == null || msg.Data.Length > ChunkSize)
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
            // Chapter share: tell the host this peer is still receiving so its confirmation
            // deadline follows the transfer instead of the moment the host finished sending.
            Patches.ChapterTransitionHelpers.ClientShareProgress(force: false);
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
                Patches.ChapterTransitionHelpers.ClientChapterShareFailed("host reported failure");
                return;
            }

            _net.StartCoroutine(ClientApplyCoroutine(_shareGeneration));
        }

        private IEnumerator ClientApplyCoroutine(int gen)
        {
            _clientApplying = true;
            ProgressText = "Verifying host world package…";
            _net.StatusText = ProgressText;
            yield return null;
            // Link dropped (Reset) or a newer package took over while this one waited a frame.
            if (gen != _shareGeneration || _chunkBuffers == null) yield break;

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
            if (gen != _shareGeneration || _chunkBuffers == null) yield break;

            // Validate names and declared sizes and inflate under a cap once; the fingerprint and
            // the chapter path both use this result (no unchecked inflate on the main thread).
            List<VerifiedFile> verified = null;
            string verifyError = null;
            try
            {
                if (!TryInflatePackage(out verified, out verifyError))
                    verified = null;
            }
            catch (Exception ex)
            {
                verified = null;
                verifyError = ex.Message;
                ModLog.Error(LogCat.Save, "Package inflate failed", ex);
            }
            if (verified == null)
            {
                FailClientApply("host world package could not be verified: " + (verifyError ?? "unknown"));
                yield break;
            }

            string packageFp = null;
            try { packageFp = ComputePackageFingerprint(verified); }
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

            if (matchSlot >= MinProfileId && matchSlot <= MaxProfileId
                && !Patches.ChapterTransitionHelpers.ChapterShareExpected)
            {
                GameProfile target = EnsureProfileSlot(matchSlot, _pendingBegin.DayIndex, _pendingBegin.ChapterId, _pendingBegin.Difficulty);
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

                // The world files are the same, but the host's roll seeds must be the ones loaded.
                InstallKeyStore(matchSlot, verified);

                // Keep meta fingerprint current (same package, no rewrite).
                var meta = CoopWorldCopyMeta.TryLoad(matchSlot) ?? new CoopWorldCopyMeta
                {
                    IsCoopCopy = true,
                    JoinedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                };
                meta.IsCoopCopy = true;
                meta.OwnCampaign = false;
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

                ProgressText = "Same world already on Profile " + matchSlot + " — press Enter world";
                if (_net != null)
                    _net.StatusText = ProgressText;
                if (Patches.ChapterTransitionHelpers.ChapterShareExpected)
                    TryBeginEnterWorld(allowInGame: true);
                else
                    ModLog.Event(LogCat.Session,
                        "Join pipeline: exact same world on slot " + matchSlot
                        + " — skipped overwrite, waiting ENTER WORLD");
                yield break;
            }

            // Chapter share while in a world: write straight into the current profile. There is no
            // slot picker mid-game, so a failed write is reported to the host (it re-sends) instead
            // of falling through to the title-screen slot pick.
            if (Patches.ChapterTransitionHelpers.ChapterShareExpected && Core.currentProfile != null)
            {
                // Verified and inflated into memory only (above). The slot is NOT written here: this
                // client is still playing the old chapter and may wait minutes for the host's go (or
                // be told to leave), so the commit happens at the go (HandleChapterLoadGo) and never otherwise.
                _verifiedPackage = verified;
                _chunkBuffers = null;
                _clientApplying = false;
                _awaitingChapterGo = true;
                int verifiedChapter = _pendingBegin.ChapterId;
                ProgressText = "Chapter world received — waiting for the host";
                if (_net != null)
                    _net.StatusText = ProgressText;

                // Host is coordinating: ack, then wait for its go before touching the slot.
                if (Patches.ChapterTransitionHelpers.ClientChapterVerified(verifiedChapter))
                    yield break;

                // Single-peer resync (no go coming): commit and enter now.
                if (!TryCommitBufferedChapterAndEnter(out string commitError))
                    FailClientApply("chapter world could not be written: " + (commitError ?? "no profile"));
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
            _verifiedPackage = null;
            _awaitingChapterGo = false;
            _clientApplying = false;
            _clientReceiving = false;
            _awaitingSlotPick = false;
            _awaitingEnterWorld = false;
            // Mid-game chapter share: tell the host so it can re-send (no-op outside a chapter share).
            Patches.ChapterTransitionHelpers.ClientChapterShareFailed(reason);
        }

        /// <summary>
        /// Same-world reuse keeps the slot's sav/savs but takes the package's cosmetic roll seeds
        /// (<c>Sync.CosmeticRolls.KeyStoreFileName</c>): the slot's own file may be from an older
        /// build or missing, and a load must roll on the host's keys. A package without one clears
        /// the slot's.
        /// </summary>
        private static void InstallKeyStore(int profileId, List<VerifiedFile> files)
        {
            string path = Path.Combine(CoopWorldCopyMeta.ProfileDir(profileId), Sync.CosmeticRolls.KeyStoreFileName);
            byte[] raw = null;
            for (int i = 0; i < files.Count; i++)
            {
                if (string.Equals(files[i].Name, Sync.CosmeticRolls.KeyStoreFileName, StringComparison.Ordinal))
                    raw = files[i].Raw;
            }
            try
            {
                if (raw == null)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, raw);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "Could not install the host's cosmetic roll seeds into slot " + profileId, ex);
            }
        }
    }
}
