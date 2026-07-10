using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// One-shot host→client transfer of Darkwood save files when the host finishes
    /// generating a new world (or host presses Resend). Clients always write into
    /// reserved <see cref="ClientReceiveProfileId"/> (slot 5) so dual-box same-AppData
    /// does not overwrite the host's live campaign. Profile index is always merged
    /// from disk before saveGameProfiles (never wipe other PLAY slots).
    /// </summary>
    public sealed class WorldSaveShareService
    {
        public const int ChunkSize = 16 * 1024;
        /// <summary>Keep low — dual-box + ReliableOrdered flood stalls host framerate hard.</summary>
        private const int MaxChunksPerFrame = 2;
        private const float HostWaitForSaveSeconds = 2.5f;
        private const int MinProfileId = 1;
        private const int MaxProfileId = 5;
        /// <summary>
        /// Client always materializes host world into this slot when joining from the title menu.
        /// Dual Steam/SecondDarkwood installs share the same AppData LocalLow path — writing the
        /// host's live profN while the host is in-game corrupts their save mid-session.
        /// </summary>
        public const int ClientReceiveProfileId = 5;

        private static readonly string[] FileNames = { "savs.dat", "sav.dat", "savch.dat" };

        private readonly LanNetworkManager _net;
        private bool _hostShareRunning;
        private bool _clientReceiving;
        private bool _clientApplying;
        private Action _afterHostShare;
        /// <summary>-1 = all handshaked peers; else only that player id.</summary>
        private int _shareTargetPlayerId = -1;

        private WorldSaveBeginMessage _pendingBegin;
        private Dictionary<int, byte[][]> _chunkBuffers;
        private int _chunksReceived;
        private int _chunksExpected;

        public bool IsBusy => _hostShareRunning || _clientReceiving || _clientApplying;
        /// <summary>Client is mid download or apply of host world package.</summary>
        public bool IsClientReceivingOrApplying => _clientReceiving || _clientApplying;
        public string ProgressText { get; private set; } = string.Empty;

        public WorldSaveShareService(LanNetworkManager net)
        {
            _net = net;
        }

        public void Reset()
        {
            _hostShareRunning = false;
            _clientReceiving = false;
            _clientApplying = false;
            _afterHostShare = null;
            _chunkBuffers = null;
            _chunksReceived = 0;
            _chunksExpected = 0;
            ProgressText = string.Empty;
        }

        /// <summary>
        /// Host only: after new world gen finishes, wait for the game's own save, then push files.
        /// </summary>
        public void ScheduleHostShareAfterNewWorld()
        {
            ScheduleHostShare(waitForGameSave: true, afterShare: null, targetPlayerId: -1);
        }

        /// <summary>
        /// Host only: manual F2 resend — force-save then push (user initiated, hitch is OK).
        /// </summary>
        public void ScheduleHostResend()
        {
            // waitForGameSave=true enables the single force Save path (see HostShareCoroutine).
            ScheduleHostShare(waitForGameSave: true, afterShare: null, targetPlayerId: -1);
        }

        /// <summary>
        /// Host only: push current world files to one newly joined client (or all if id ≤ 0).
        /// Used when a peer handshakes while the host is already in-game.
        /// </summary>
        public void ScheduleHostShareToPlayer(int playerId)
        {
            if (playerId <= 0)
            {
                ScheduleHostResend();
                return;
            }
            ScheduleHostShare(waitForGameSave: false, afterShare: null, targetPlayerId: playerId);
        }

        /// <summary>
        /// Host only: push current save files, then run <paramref name="afterShare"/> (e.g. chapter LoadScene).
        /// </summary>
        public void ScheduleHostShareThen(Action afterShare, bool waitForGameSave = false)
        {
            ScheduleHostShare(waitForGameSave, afterShare, targetPlayerId: -1);
        }

        private void ScheduleHostShare(bool waitForGameSave, Action afterShare, int targetPlayerId)
        {
            if (_net == null || _net.Role != NetworkRole.Host)
                return;
            if (!_net.IsConnected || !_net.IsHandshakeComplete)
            {
                ProgressText = "No clients connected";
                _net.StatusText = ProgressText;
                ModLog.Event(LogCat.Save, "World share skipped — no connected clients");
                // Still run afterShare so host chapter load is not stuck.
                try { afterShare?.Invoke(); } catch (Exception ex) {
                    ModLog.Error(LogCat.Save, "afterShare with no clients", ex);
                }
                return;
            }
            if (_hostShareRunning)
            {
                ProgressText = "World share already in progress";
                // Chain: run after current share finishes (include same target)
                if (afterShare != null || targetPlayerId > 0)
                {
                    var prev = _afterHostShare;
                    int chainedTarget = targetPlayerId;
                    _afterHostShare = () =>
                    {
                        try { prev?.Invoke(); } catch { /* ignore */ }
                        try { afterShare?.Invoke(); } catch { /* ignore */ }
                        if (chainedTarget > 0)
                            ScheduleHostShareToPlayer(chainedTarget);
                    };
                }
                return;
            }

            _afterHostShare = afterShare;
            _shareTargetPlayerId = targetPlayerId;
            // Mute high-rate gameplay flood to joiners until they send first PlayerState.
            if (targetPlayerId > 0)
                _net.MarkPeerLoadingWorld(targetPlayerId);
            else
                _net.MarkAllClientPeersLoadingWorld();
            _net.StartCoroutine(HostShareCoroutine(waitForGameSave));
        }

        private IEnumerator HostShareCoroutine(bool waitForGameSave)
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
                }
            }

            // Resolve profile dir first — prefer sharing already-on-disk saves without a
            // full force Save (Save freezes the host for seconds on dual-box + large worlds).
            int profileId = GetHostProfileId();
            if (profileId < MinProfileId || profileId > MaxProfileId)
            {
                float waitedProf = 0f;
                while (waitedProf < 2f && (profileId < MinProfileId || profileId > MaxProfileId))
                {
                    waitedProf += Time.unscaledDeltaTime;
                    yield return null;
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
                FinishHostShare(runAfter: true);
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
                FinishHostShare(runAfter: true);
                yield break;
            }

            string savPath = Path.Combine(profDir, "sav.dat");
            string savsPath = Path.Combine(profDir, "savs.dat");
            bool hasAnyFiles = File.Exists(savPath) || File.Exists(savsPath);

            // CRITICAL (dual-box + mid-session join):
            // Force Save() runs "Save static" and freezes the host for seconds; on the same
            // AppData as the client it also contending disk with LoadScene — looks like a
            // random "event" and can brick both processes.
            // Horde base only force-saved for new-world share / manual Resend, and did NOT
            // auto-share on handshake. Late-join share must read existing files only.
            // waitForGameSave=true → after new world gen (game already saving) — allow force.
            if (waitForGameSave && Singleton<SaveManager>.Instance != null)
            {
                ModLog.Event(LogCat.Save, "Post-worldgen share: waiting then force-saving once");
                try
                {
                    LanNetworkManager._isRemoteSaveInProgress = true;
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
                        LanNetworkManager._isRemoteSaveInProgress = false;
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
                    FinishHostShare(runAfter: true);
                    yield break;
                }

                for (int i = 0; i < 3; i++)
                    yield return null;

                float waitedFiles = 0f;
                while (waitedFiles < 5f)
                {
                    if (File.Exists(savPath) || File.Exists(savsPath))
                        break;
                    waitedFiles += Time.unscaledDeltaTime;
                    yield return null;
                }
            }
            else if (hasAnyFiles)
            {
                ModLog.Event(LogCat.Save,
                    "Late-join share: using on-disk sav files (NO force Save — host stays playable)");
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
                FinishHostShare(runAfter: true);
                yield break;
            }

            // Pack one file per frame — ReadAllBytes+Deflate of ~9MB savs.dat on one frame
            // freezes the host mid-game (the hitch users call an "event"). Horde Resend
            // only ran when the host wasn't mid-combat dual-box load.
            var files = new List<PackedFile>();
            foreach (string name in FileNames)
            {
                string path = Path.Combine(profDir, name);
                if (!File.Exists(path))
                {
                    ModLog.Event(LogCat.Save, "Share skip missing file: " + path);
                    continue;
                }

                ProgressText = "Reading " + name + "…";
                _net.StatusText = ProgressText;
                yield return null;

                byte[] raw;
                try
                {
                    raw = File.ReadAllBytes(path);
                }
                catch (Exception ex)
                {
                    ModLog.Error(LogCat.Save, "Failed reading " + name, ex);
                    continue;
                }

                if (raw == null || raw.Length == 0)
                    continue;

                ProgressText = "Compressing " + name + " (" + (raw.Length / 1024) + " KB)…";
                _net.StatusText = ProgressText;
                yield return null;

                byte[] compressed = Deflate(raw);
                yield return null;

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
                        yield return null;
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
                FinishHostShare(runAfter: true);
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
                ChunkCounts = new int[files.Count]
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
                    }
                }
            }

            SendShare(target, NetMessageType.WorldSaveEnd, w =>
            {
                new WorldSaveEndMessage { Success = true }.Serialize(w);
            });

            ProgressText = "World shared → client profile " + profileId;
            _net.StatusText = ProgressText;
            ModLog.Event(LogCat.Save, "World save share complete (" + sent + " chunks → slot " + profileId + ")");
            FinishHostShare(runAfter: true);
        }

        private void SendShare(int targetPlayerId, NetMessageType type, System.Action<NetWriter> write)
        {
            if (targetPlayerId > 0)
            {
                _net.SendToPlayer(targetPlayerId, type, write, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else
            {
                _net.SendToAll(type, write, LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
        }

        private void FinishHostShare(bool runAfter)
        {
            _hostShareRunning = false;
            _shareTargetPlayerId = -1;
            if (!runAfter) return;
            Action after = _afterHostShare;
            _afterHostShare = null;
            if (after == null) return;
            try { after(); }
            catch (Exception ex) { ModLog.Error(LogCat.Save, "afterHostShare failed", ex); }
        }

        public void HandleBegin(WorldSaveBeginMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;

            // Already in chapter — ignore resend (would LoadScene and wipe the session).
            if (!Core.mainMenu && Player.Instance != null)
            {
                ModLog.Event(LogCat.Save, "Ignoring world share begin — already in game");
                return;
            }

            _clientReceiving = true;
            _clientApplying = false;
            _pendingBegin = msg;
            // Dual-box same AppData: never overwrite host's live profN mid-session.
            // Always materialize join package into reserved slot 5 on the client.
            if (msg.ProfileId != ClientReceiveProfileId)
            {
                ModLog.Event(LogCat.Save,
                    "Host shared slot " + msg.ProfileId + " — client will apply to reserved slot "
                    + ClientReceiveProfileId + " (same AppData dual-install safe)");
                msg.ProfileId = ClientReceiveProfileId;
                _pendingBegin = msg;
            }
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

            ProgressText = "Receiving world → slot " + _pendingBegin.ProfileId + "…";
            _net.StatusText = ProgressText;
            ModLog.Event(LogCat.Save,
                "Receiving host world for profile slot " + _pendingBegin.ProfileId
                + ": " + msg.FileCount + " files, " + _chunksExpected + " chunks, ch"
                + msg.ChapterId + " day" + msg.DayIndex);
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
                ProgressText = "Receiving world (slot " + _pendingBegin.ProfileId + ") "
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
            int profileId = _pendingBegin.ProfileId;
            ProgressText = "Applying host world → slot " + profileId + "…";
            _net.StatusText = ProgressText;
            yield return null;

            if (profileId < MinProfileId || profileId > MaxProfileId)
            {
                FailClientApply("Invalid host profile id: " + profileId);
                yield break;
            }

            // Verify chunks
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

            GameProfile target = EnsureProfileSlot(profileId, _pendingBegin.DayIndex, _pendingBegin.ChapterId);
            Core.currentProfile = target;
            string profDir = GetProfileDir(profileId);
            Directory.CreateDirectory(profDir);

            // One file per frame: inflate+write of 9MB savs freezes dual-box host too.
            for (int i = 0; i < _pendingBegin.FileCount; i++)
            {
                string name = _pendingBegin.FileNames[i];
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || (name != "sav.dat" && name != "savs.dat" && name != "savch.dat"))
                {
                    FailClientApply("Bad file name: " + name);
                    yield break;
                }

                ProgressText = "Inflating " + name + "…";
                _net.StatusText = ProgressText;
                yield return null;

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

                byte[] raw;
                try
                {
                    raw = Inflate(compressed);
                }
                catch (Exception ex)
                {
                    FailClientApply("Inflate failed " + name + ": " + ex.Message);
                    yield break;
                }

                if (_pendingBegin.UncompressedSizes[i] > 0 && raw.Length != _pendingBegin.UncompressedSizes[i])
                {
                    FailClientApply("Decompressed size mismatch for " + name);
                    yield break;
                }

                ProgressText = "Writing " + name + "…";
                _net.StatusText = ProgressText;
                yield return null;

                try
                {
                    string dest = Path.Combine(profDir, name);
                    string tmp = dest + ".dwmp_tmp";
                    File.WriteAllBytes(tmp, raw);
                    if (File.Exists(dest))
                        File.Delete(dest);
                    File.Move(tmp, dest);
                }
                catch (Exception ex)
                {
                    FailClientApply("Write failed " + name + ": " + ex.Message);
                    yield break;
                }

                ModLog.Event(LogCat.Save,
                    "Wrote host world file " + name + " → prof" + profileId + " (" + raw.Length + " bytes)");
                yield return null;
            }

            target.day = _pendingBegin.DayIndex;
            target.chapter = _pendingBegin.ChapterId;
            target.timeSaved = DateTime.Now.ToString();
            target.majorVersion = Core.majorVersion;
            target.minorVersion = Core.minorVersion;
            target.RCVersion = Core.RCVersion;
            target.fullRelease = true;
            target.Active = true;

            // Dual-box: do NOT saveGameProfiles() here — rewrites shared profs.dat while the
            // host is mid-session and can hitch/corrupt the live index. Memory-only is enough
            // for LoadScene; disk index can update after load.
            MergeProfileIntoMemoryOnly(target);
            Core.currentProfile = target;

            // CRITICAL: SaveManager still has paths for whatever profile was last active
            // (often empty on title). Without updateFilePaths(), Load/initLoadGame reads the
            // wrong profN → getGOsFromID NRE mid SaveManager.Load and a wedged client that
            // freezes the dual-box host until killed.
            try
            {
                SaveManager sm = Singleton<SaveManager>.Instance;
                if (sm != null)
                    sm.updateFilePaths();
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "updateFilePaths after world apply failed", ex);
            }

            Sync.WorldPhysicsSyncService.Reset();
            Sync.DreamSyncManager.OnDisconnected();
            Sync.MultiplayerMapManager.Reset();
            Sync.DreamSession.ResetIncludingCompletions();
            DeathStateTracker.Reset();

            int chapterId = _pendingBegin.ChapterId > 0 ? _pendingBegin.ChapterId : 1;

            ProgressText = "World ready — loading offline…";
            _net.StatusText = ProgressText;
            _chunkBuffers = null;

            // Join pipeline (strict order):
            //   1) world share (done) → 2) enter shared world offline → 3) co-op reconnect
            // Stop the transfer link BEFORE LoadScene so the dual-box host is not frozen
            // by a peer that stops PollEvents mid-load. ChapterSessionResume reconnects
            // after chapterN is up (handshake AlreadyInWorld → host skips re-share).
            try
            {
                ChapterSessionResume.EnsureSceneHook();
                if (_net != null && _net.IsConnected)
                {
                    ChapterSessionResume.CaptureForResume(_net);
                    ModLog.Event(LogCat.Session,
                        "Join pipeline phase 2: world on disk (slot " + profileId
                        + ") — disconnect transfer link, load offline, then phase-3 reconnect");
                    // StopNetwork resets WorldSaveShare; coroutine locals already hold load state.
                    _net.StopNetwork();
                }
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "Join pipeline disconnect-before-load failed", ex);
            }

            _clientApplying = false;
            yield return null;

            // Prefer vanilla Continue path (Yokyy): UI.initLoadGame with currentProfile set.
            // Still on title menu — do not close MainMenu first.
            UI ui = Singleton<UI>.Instance;
            if (ui != null && Core.mainMenu)
            {
                try
                {
                    MainMenu menu = Singleton<MainMenu>.Instance;
                    if (menu != null)
                        menu.creatingProfile = false;
                }
                catch { /* ignore */ }

                ModLog.Event(LogCat.Save,
                    "Join pipeline phase 2: native initLoadGame → slot " + profileId
                    + " ch" + chapterId + " (offline; profs.dat not rewritten)");
                ProgressText = "Loading host world (offline)…";
                yield return ui.StartCoroutine(ui.initLoadGame());
                yield break;
            }

            // Fallback: direct chapter scene load (same flags as ManualSaveGUI continue).
            Core.coreStarted = false;
            Core.mainMenu = false;
            Core.loadingGame = true;
            Core.loadedGame = true;
            Time.timeScale = 1f;

            if (Singleton<MainMenu>.Instance != null)
                Singleton<MainMenu>.Instance.close();

            ModLog.Event(LogCat.Save,
                "Join pipeline phase 2: LoadScene chapter" + chapterId
                + " slot " + profileId + " (offline fallback)");

            ProgressText = "Loading host world (offline)…";
            yield return null;
            yield return null;
            SceneManager.LoadScene("chapter" + chapterId);
        }

        private void FailClientApply(string reason)
        {
            // Audit C4: fail-loud — never silently continue into a divergent forest.
            string loud = WorldSharePolicy.FormatShareFailure(reason);
            ModLog.Error(LogCat.Save, "Failed to apply host world: " + reason);
            ProgressText = loud;
            if (_net != null)
                _net.StatusText = loud;
            _chunkBuffers = null;
            _clientApplying = false;
            _clientReceiving = false;
        }

        /// <summary>Update Core.profiles in RAM only — never touch profs.dat during join.</summary>
        private static void MergeProfileIntoMemoryOnly(GameProfile slot)
        {
            if (slot == null) return;
            List<GameProfile> profiles = Core.profiles != null
                ? new List<GameProfile>(Core.profiles)
                : new List<GameProfile>();
            for (int i = profiles.Count - 1; i >= 0; i--)
            {
                if (profiles[i] != null && profiles[i].id == slot.id)
                    profiles.RemoveAt(i);
            }
            profiles.Add(slot);
            profiles.Sort((a, b) =>
            {
                int aid = a != null ? a.id : 0;
                int bid = b != null ? b.id : 0;
                return aid.CompareTo(bid);
            });
            Core.profiles = profiles;
            Core.currentProfile = slot;
        }

        /// <summary>
        /// Find or create the GameProfile with the receive slot id (does not touch disk yet).
        /// </summary>
        private static GameProfile EnsureProfileSlot(int profileId, int day, int chapter)
        {
            if (Core.profiles != null)
            {
                for (int i = 0; i < Core.profiles.Count; i++)
                {
                    GameProfile p = Core.profiles[i];
                    if (p != null && p.id == profileId)
                    {
                        p.Active = true;
                        p.day = day;
                        p.chapter = chapter;
                        return p;
                    }
                }
            }

            var created = new GameProfile(profileId, _Active: true, day);
            created.chapter = chapter;
            created.fullRelease = true;
            created.majorVersion = Core.majorVersion;
            created.minorVersion = Core.minorVersion;
            created.RCVersion = Core.RCVersion;
            if (Core.profiles == null)
                Core.profiles = new List<GameProfile>();
            Core.profiles.Add(created);
            return created;
        }

        /// <summary>
        /// Merge <paramref name="slot"/> into the real on-disk profile list, then save.
        /// Never call bare saveGameProfiles() with a partial Core.profiles — that wipes PLAY slots.
        /// </summary>
        private static void MergeProfileIntoDiskIndexAndSave(GameProfile slot)
        {
            if (slot == null) return;

            List<GameProfile> profiles = LoadProfilesFromDisk();
            if (profiles == null)
                profiles = Core.profiles != null
                    ? new List<GameProfile>(Core.profiles)
                    : new List<GameProfile>();

            // Drop stale entry for this id, then add the updated slot.
            for (int i = profiles.Count - 1; i >= 0; i--)
            {
                if (profiles[i] != null && profiles[i].id == slot.id)
                    profiles.RemoveAt(i);
            }
            profiles.Add(slot);

            // Safety net: if a profN folder has sav.dat but is missing from the index,
            // re-register it so PLAY still shows real campaigns after a prior nuke.
            for (int id = MinProfileId; id <= MaxProfileId; id++)
            {
                if (id == slot.id) continue;
                bool listed = false;
                for (int i = 0; i < profiles.Count; i++)
                {
                    if (profiles[i] != null && profiles[i].id == id)
                    {
                        listed = true;
                        break;
                    }
                }
                if (listed) continue;

                string sav = Path.Combine(GetProfileDir(id), "sav.dat");
                if (!File.Exists(sav)) continue;

                var orphan = new GameProfile(id, _Active: true, 1);
                orphan.chapter = 1;
                orphan.fullRelease = true;
                orphan.majorVersion = Core.majorVersion;
                orphan.minorVersion = Core.minorVersion;
                orphan.RCVersion = Core.RCVersion;
                orphan.timeSaved = File.GetLastWriteTime(sav).ToString();
                profiles.Add(orphan);
                ModLog.Warn(LogCat.Save,
                    "Re-registered orphan profile slot " + id + " from disk sav.dat (was missing from profs.dat)");
            }

            profiles.Sort((a, b) =>
            {
                int aid = a != null ? a.id : 0;
                int bid = b != null ? b.id : 0;
                return aid.CompareTo(bid);
            });

            Core.profiles = profiles;
            Core.currentProfile = slot;

            var sm = Singleton<SaveManager>.Instance;
            if (sm == null)
            {
                ModLog.Warn(LogCat.Save, "SaveManager missing — could not persist merged profile index");
                return;
            }

            try { sm.updateFilePaths(); }
            catch (Exception ex) { ModLog.Warn(LogCat.Save, "updateFilePaths: " + ex.Message); }

            try
            {
                sm.saveGameProfiles();
                ModLog.Event(LogCat.Save,
                    "Saved profile index with " + profiles.Count + " slots (merged receive slot "
                    + slot.id + ")");
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "saveGameProfiles after merge failed", ex);
            }
        }

        /// <summary>
        /// Read the real profile index from disk (GetProfiles / loadGameProfiles), not in-memory Core.profiles.
        /// </summary>
        private static List<GameProfile> LoadProfilesFromDisk()
        {
            var sm = Singleton<SaveManager>.Instance;
            if (sm == null) return null;

            try
            {
                // Prefer private GetProfiles() — same as Yokyy; returns MainMenu.SaveState with .profiles
                var getProfiles = typeof(SaveManager).GetMethod("GetProfiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getProfiles != null)
                {
                    object state = getProfiles.Invoke(sm, null);
                    if (state != null)
                    {
                        var field = state.GetType().GetField("profiles",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (field != null && field.GetValue(state) is List<GameProfile> fromGet)
                            return new List<GameProfile>(fromGet);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "GetProfiles failed: " + ex.Message);
            }

            try
            {
                // Public fallback
                var load = typeof(SaveManager).GetMethod("loadGameProfiles",
                    BindingFlags.Public | BindingFlags.Instance);
                if (load != null)
                {
                    object state = load.Invoke(sm, null);
                    if (state != null)
                    {
                        var field = state.GetType().GetField("profiles",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (field != null && field.GetValue(state) is List<GameProfile> fromLoad)
                            return new List<GameProfile>(fromLoad);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "loadGameProfiles failed: " + ex.Message);
            }

            return null;
        }

        private static int GetHostProfileId()
        {
            if (Core.currentProfile != null && Core.currentProfile.id >= MinProfileId
                && Core.currentProfile.id <= MaxProfileId)
                return Core.currentProfile.id;

            // Fallbacks: active profiles list
            if (Core.profiles != null)
            {
                for (int i = 0; i < Core.profiles.Count; i++)
                {
                    GameProfile p = Core.profiles[i];
                    if (p != null && p.Active && p.id >= MinProfileId && p.id <= MaxProfileId)
                        return p.id;
                }
                for (int i = 0; i < Core.profiles.Count; i++)
                {
                    GameProfile p = Core.profiles[i];
                    if (p != null && p.id >= MinProfileId && p.id <= MaxProfileId)
                        return p.id;
                }
            }

            // Disk: newest profN with sav.dat
            try
            {
                string root = Path.Combine(Application.persistentDataPath, "1_4Save");
                int bestId = 0;
                long bestTime = 0;
                for (int id = MinProfileId; id <= MaxProfileId; id++)
                {
                    string sav = Path.Combine(root, "prof" + id, "sav.dat");
                    if (!File.Exists(sav)) continue;
                    long t = File.GetLastWriteTimeUtc(sav).Ticks;
                    if (t > bestTime)
                    {
                        bestTime = t;
                        bestId = id;
                    }
                }
                if (bestId > 0) return bestId;
            }
            catch { /* ignore */ }

            return 0;
        }

        private static string GetProfileDir(int profileId)
        {
            // Unity persistentDataPath = .../Acid Wizard Studio/Darkwood
            return Path.Combine(Application.persistentDataPath, "1_4Save", "prof" + profileId);
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
