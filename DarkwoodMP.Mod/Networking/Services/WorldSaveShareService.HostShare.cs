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
    /// Host share coroutine, send helper, and finish (split for ownership).
    /// </summary>
    public sealed partial class WorldSaveShareService
    {
        private IEnumerator HostShareCoroutine(bool waitForGameSave, int gen)
        {
            _hostShareRunning = true;
            int target = _shareTargetPlayerId;
            ProgressText = waitForGameSave ? "Preparing world share…"
                : (target > 0 ? ("Sending world to player " + target + "…") : "Resending world…");
            ModLog.Event(LogCat.Save,
                waitForGameSave
                    ? "New world finished — preparing save share for clients"
                    : (target > 0
                        ? ("Auto world share to joining player " + target)
                        : "Manual world resend requested"));

            if (waitForGameSave)
            {
                float waited = 0f;
                while (waited < HostWaitForSaveSeconds)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                    if (gen != _shareGeneration) yield break;
                }
            }

            // Resolve the profile directory first. Prefer sharing already-on-disk saves without a
            // full force Save (Save freezes the host for seconds on dual-box + large worlds).
            int profileId = GetHostProfileId();
            if (profileId < MinProfileId || profileId > MaxProfileId)
            {
                float waitedProf = 0f;
                while (waitedProf < 2f && (profileId < MinProfileId || profileId > MaxProfileId))
                {
                    waitedProf += Time.unscaledDeltaTime;
                    yield return null;
                    if (gen != _shareGeneration) yield break;
                    profileId = GetHostProfileId();
                }
            }
            if (profileId < MinProfileId || profileId > MaxProfileId)
            {
                ModLog.Error(LogCat.Save, "Host profile id invalid: " + profileId
                    + " (currentProfile null?). Cannot share world.");
                ProgressText = WorldSharePolicy.FormatShareFailure("bad host profile id " + profileId);
                _net.StatusText = ProgressText;
                // Notify clients so they do not wait forever on a silent empty transfer.
                try
                {
                    SendShare(target, NetMessageType.WorldSaveEnd, w =>
                    {
                        new WorldSaveEndMessage { Success = false }.Serialize(w);
                    });
                }
                catch { /* ignore */ }
                FinishHostShare(gen, runAfter: true);
                yield break;
            }

            string profDir = GetProfileDir(profileId);
            if (string.IsNullOrEmpty(profDir) || !Directory.Exists(profDir))
            {
                ModLog.Error(LogCat.Save, "Host profile directory missing: " + profDir
                    + " persistentDataPath=" + Application.persistentDataPath);
                ProgressText = WorldSharePolicy.FormatShareFailure("no profile dir " + (profDir ?? "(null)"));
                _net.StatusText = ProgressText;
                try
                {
                    SendShare(target, NetMessageType.WorldSaveEnd, w =>
                    {
                        new WorldSaveEndMessage { Success = false }.Serialize(w);
                    });
                }
                catch { /* ignore */ }
                FinishHostShare(gen, runAfter: true);
                yield break;
            }

            string savPath = Path.Combine(profDir, "sav.dat");
            string savsPath = Path.Combine(profDir, "savs.dat");
            bool hasAnyFiles = File.Exists(savPath) || File.Exists(savsPath);

            // CRITICAL (dual-box + mid-session join):
            // Force Save() freezes the host for seconds ("Save static"). Prefer on-disk when
            // sav.dat + savs.dat are a consistent pair. Skewed pairs (e.g. only dynamic written)
            // make client SaveManager.Load NRE with "ERROR WHEN LOADING DYNAMIC AND STATIC SAVE"
            // and leave loadingGame stuck, preventing phase-3 reconnect.
            // waitForGameSave / manual resend always force; late-join forces only when needed.
            bool forceForConsistency = !waitForGameSave && hasAnyFiles
                && OnDiskSavPairNeedsForceSave(savPath, savsPath);
            if ((waitForGameSave || forceForConsistency) && Singleton<SaveManager>.Instance != null)
            {
                ModLog.Event(LogCat.Save, waitForGameSave
                    ? "Post-worldgen/resend share: force-saving once"
                    : "Late-join share: sav/savs inconsistent on disk — force-saving once");
                try
                {
                    LanNetworkManager.RemoteSaveInProgress = true;
                    try
                    {
                        Singleton<SaveManager>.Instance.Save(
                            doJson: true,
                            doSaveProfile: true,
                            force: true,
                            forceSaveStatic: true,
                            showSavingIndicator: false);
                    }
                    finally
                    {
                        LanNetworkManager.RemoteSaveInProgress = false;
                    }
                }
                catch (Exception ex)
                {
                    ModLog.Error(LogCat.Save, "Host save before world share failed", ex);
                    ProgressText = WorldSharePolicy.FormatShareFailure("host save error: " + ex.Message);
                    _net.StatusText = ProgressText;
                    try
                    {
                        SendShare(target, NetMessageType.WorldSaveEnd, w =>
                        {
                            new WorldSaveEndMessage { Success = false }.Serialize(w);
                        });
                    }
                    catch { /* ignore */ }
                    FinishHostShare(gen, runAfter: true);
                    yield break;
                }

                for (int i = 0; i < 3; i++)
                {
                    yield return null;
                    if (gen != _shareGeneration) yield break;
                }

                float waitedFiles = 0f;
                while (waitedFiles < 5f)
                {
                    if (File.Exists(savPath) || File.Exists(savsPath))
                        break;
                    waitedFiles += Time.unscaledDeltaTime;
                    yield return null;
                    if (gen != _shareGeneration) yield break;
                }
            }
            else if (hasAnyFiles)
            {
                ModLog.Event(LogCat.Save,
                    "Late-join share: using on-disk sav files (consistent pair — no force Save)");
            }
            else
            {
                ModLog.Error(LogCat.Save,
                    "No sav.dat/savs.dat on disk for prof" + profileId
                    + " — host should quicksave once, then client rejoin / F2 Resend");
                ProgressText = WorldSharePolicy.FormatShareFailure(
                    "no save files for prof" + profileId + " — host: save once then Resend");
                _net.StatusText = ProgressText;
                try
                {
                    SendShare(target, NetMessageType.WorldSaveEnd, w =>
                    {
                        new WorldSaveEndMessage { Success = false }.Serialize(w);
                    });
                }
                catch { /* ignore */ }
                FinishHostShare(gen, runAfter: true);
                yield break;
            }

            LogSavPairTimestamps(savPath, savsPath);

            // Read the whole save set in ONE main-thread step. Save also runs on the main thread, so
            // no Save (SaveSync fan-out, sleep, F3) can land between two files and hand clients a
            // sav/savs pair from different moments. Only Deflate runs off-thread, one file per frame.
            List<KeyValuePair<string, byte[]>> snapshot = null;
            Exception readEx = null;
            for (int attempt = 0; attempt < 3 && snapshot == null; attempt++)
            {
                if (attempt > 0)
                {
                    yield return null;
                    if (gen != _shareGeneration) yield break;
                }
                try { snapshot = ReadSaveSetSnapshot(profDir); }
                catch (Exception ex) { readEx = ex; }
            }
            if (snapshot == null)
            {
                ModLog.Error(LogCat.Save, "Failed reading save files for share (prof" + profileId + ")", readEx);
                ProgressText = WorldSharePolicy.FormatShareFailure(
                    "could not read save files: " + (readEx != null ? readEx.Message : "unknown"));
                _net.StatusText = ProgressText;
                try
                {
                    SendShare(target, NetMessageType.WorldSaveEnd, w =>
                    {
                        new WorldSaveEndMessage { Success = false }.Serialize(w);
                    });
                }
                catch { /* ignore */ }
                FinishHostShare(gen, runAfter: true);
                yield break;
            }

            var files = new List<PackedFile>();
            foreach (KeyValuePair<string, byte[]> entry in snapshot)
            {
                string name = entry.Key;
                byte[] raw = entry.Value;
                if (raw == null || raw.Length == 0)
                    continue;

                ProgressText = "Compressing " + name + " (" + (raw.Length / 1024) + " KB)…";
                _net.StatusText = ProgressText;
                yield return null;
                if (gen != _shareGeneration) yield break;

                byte[] compressed = null;
                Exception packEx = null;
                bool packDone = false;
                byte[] rawCapture = raw;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { compressed = Deflate(rawCapture); }
                    catch (Exception ex) { packEx = ex; }
                    finally { packDone = true; }
                });
                while (!packDone)
                {
                    yield return null;
                    if (gen != _shareGeneration) yield break;
                }

                if (packEx != null)
                {
                    ModLog.Error(LogCat.Save, "Failed compressing " + name, packEx);
                    continue;
                }
                if (compressed == null || compressed.Length == 0)
                    continue;

                yield return null;
                if (gen != _shareGeneration) yield break;

                int chunkCount = (compressed.Length + ChunkSize - 1) / ChunkSize;
                if (chunkCount < 1) chunkCount = 1;

                var chunks = new byte[chunkCount][];
                for (int c = 0; c < chunkCount; c++)
                {
                    int offset = c * ChunkSize;
                    int len = Math.Min(ChunkSize, compressed.Length - offset);
                    var slice = new byte[len];
                    Buffer.BlockCopy(compressed, offset, slice, 0, len);
                    chunks[c] = slice;
                    // Slice large compressed buffers across frames too
                    if ((c & 31) == 31)
                    {
                        yield return null;
                        if (gen != _shareGeneration) yield break;
                    }
                }

                files.Add(new PackedFile
                {
                    Name = name,
                    UncompressedSize = raw.Length,
                    CompressedSize = compressed.Length,
                    Chunks = chunks
                });
                ModLog.Event(LogCat.Save,
                    "Packed " + name + " raw=" + raw.Length + " compressed=" + compressed.Length
                    + " chunks=" + chunkCount);
                yield return null;
                if (gen != _shareGeneration) yield break;
            }

            if (files.Count == 0)
            {
                ModLog.Error(LogCat.Save, "No save files found to share for prof" + profileId);
                ProgressText = WorldSharePolicy.FormatShareFailure("no save files for prof" + profileId);
                _net.StatusText = ProgressText;
                try
                {
                    SendShare(target, NetMessageType.WorldSaveEnd, w =>
                    {
                        new WorldSaveEndMessage { Success = false }.Serialize(w);
                    });
                }
                catch { /* ignore */ }
                FinishHostShare(gen, runAfter: true);
                yield break;
            }

            int chapter = 1;
            int day = 1;
            if (Singleton<WorldGenerator>.Instance != null)
                chapter = Singleton<WorldGenerator>.Instance.chapterID;
            else if (Core.currentProfile != null)
                chapter = Core.currentProfile.chapter;
            if (Core.currentProfile != null)
                day = Core.currentProfile.day;
            if (Singleton<Controller>.Instance != null && Singleton<Controller>.Instance.day > 0)
                day = Singleton<Controller>.Instance.day;

            var begin = new WorldSaveBeginMessage
            {
                ProfileId = profileId,
                ChapterId = chapter,
                DayIndex = day,
                FileCount = files.Count,
                FileNames = new string[files.Count],
                UncompressedSizes = new int[files.Count],
                CompressedSizes = new int[files.Count],
                ChunkCounts = new int[files.Count],
                CampaignId = CoopWorldCopyMeta.GetOrCreateCampaignId(profileId),
                Difficulty = Core.currentProfile != null ? (int)Core.currentProfile.difficulty : 0,
                PrologueOffered = Sync.PersonalPrologue.HostOffersPrologue(),
                // A broadcast opens a new pass; a per-peer re-send belongs to the running one, so the
                // other peers' acks for the broadcast stay valid.
                SharePass = _shareTargetPlayerId > 0
                    ? Patches.ChapterTransitionHelpers.HostSharePass
                    : Patches.ChapterTransitionHelpers.NextHostSharePass()
            };
            int totalChunks = 0;
            for (int i = 0; i < files.Count; i++)
            {
                begin.FileNames[i] = files[i].Name;
                begin.UncompressedSizes[i] = files[i].UncompressedSize;
                begin.CompressedSizes[i] = files[i].CompressedSize;
                begin.ChunkCounts[i] = files[i].Chunks.Length;
                totalChunks += files[i].Chunks.Length;
            }

            ModLog.Event(LogCat.Save,
                "Sharing world → " + (target > 0 ? ("player " + target) : "all clients")
                + " profile slot " + profileId
                + ": " + files.Count + " files, " + totalChunks + " chunks, ch" + chapter + " day" + day);

            if (target <= 0)
                _broadcastRecipients = new HashSet<int>(_net.EnumeratePeerIds());
            SendShare(target, NetMessageType.WorldSaveBegin, w => begin.Serialize(w));

            int sent = 0;
            int frameBudget = 0;
            for (int fi = 0; fi < files.Count; fi++)
            {
                var pf = files[fi];
                for (int ci = 0; ci < pf.Chunks.Length; ci++)
                {
                    byte fileIndex = (byte)fi;
                    int chunkIndex = ci;
                    byte[] data = pf.Chunks[ci];
                    SendShare(target, NetMessageType.WorldSaveChunk, w =>
                    {
                        new WorldSaveChunkMessage
                        {
                            FileIndex = fileIndex,
                            ChunkIndex = chunkIndex,
                            Data = data
                        }.Serialize(w);
                    });

                    sent++;
                    frameBudget++;
                    ProgressText = "Sending world (slot " + profileId + ") "
                        + (int)(100f * sent / totalChunks) + "%";
                    _net.StatusText = ProgressText;

                    if (frameBudget >= MaxChunksPerFrame)
                    {
                        frameBudget = 0;
                        yield return null;
                        if (gen != _shareGeneration) yield break;
                    }
                }
            }

            SendShare(target, NetMessageType.WorldSaveEnd, w =>
            {
                new WorldSaveEndMessage { Success = true }.Serialize(w);
            });
            if (begin.PrologueOffered)
            {
                // A recipient new to the world plays the prologue offline before it comes back. Only
                // one that got the whole world: a download cut short brings nobody to wait for.
                IEnumerable<int> recipients = target > 0 ? new[] { target } : (IEnumerable<int>)_broadcastRecipients;
                foreach (int id in recipients)
                    if (_net.HasPeer(id) && _net.TryGetStableClientKeyForPlayer(id, out string key))
                        Sync.PersonalPrologue.HostNoteShared(key);
            }

            ProgressText = "World shared → client profile " + profileId;
            _net.StatusText = ProgressText;
            ModLog.Event(LogCat.Save, "World save share complete (" + sent + " chunks → slot " + profileId + ")");
            FinishHostShare(gen, runAfter: true);
        }

        private void SendShare(int targetPlayerId, NetMessageType type, System.Action<NetWriter> write)
        {
            if (targetPlayerId > 0)
            {
                _net.SendToPlayer(targetPlayerId, type, write, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else if (_broadcastRecipients != null)
            {
                // Begin already went to a fixed set; a peer that joined since would get chunks
                // with no Begin. Send only to the peers that did get it (a departed one is a no-op).
                foreach (int id in _broadcastRecipients)
                    _net.SendToPlayer(id, type, write, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                _net.SendToAll(type, write, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }

        private void FinishHostShare(int gen, bool runAfter)
        {
            // A share from a torn-down session must not clear the running flag or fire callbacks
            // that now belong to the next session's share.
            if (gen != _shareGeneration)
                return;
            _hostShareRunning = false;
            _shareTargetPlayerId = -1;
            _broadcastRecipients = null;
            _hostShareCoroutine = null;
            Action after = runAfter ? _afterHostShare : null;
            _afterHostShare = null;
            if (after != null && _rerunBroadcast && gen == _shareGeneration)
            {
                // The chapter callback (host commit wait) belongs after the re-run that sends the
                // fresh files; starting it now would wait on acks for a package being superseded,
                // and the re-run's own callback would then be dropped by the running-wait guard.
                _rerunAfter += after;
                after = null;
            }
            if (after != null)
            {
                try { after(); }
                catch (Exception ex) { ModLog.Error(LogCat.Save, "afterHostShare failed", ex); }
            }

            // A broadcast was requested mid-share: send to everyone now with fresh files.
            if (_rerunBroadcast && gen == _shareGeneration && !_hostShareRunning)
            {
                bool wait = _rerunWaitForSave;
                Action rerunAfter = _rerunAfter;
                _rerunBroadcast = false;
                _rerunWaitForSave = false;
                _rerunAfter = null;
                ModLog.Event(LogCat.Save, "World share finished — re-running broadcast queued during it");
                // Acks counted for the share that just ended do not vouch for the files sent now.
                Patches.ChapterTransitionHelpers.HostShareRestarted();
                ScheduleHostShare(wait, rerunAfter, targetPlayerId: -1);
            }
        }
    }
}
