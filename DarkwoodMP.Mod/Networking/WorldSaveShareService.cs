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
    /// One-shot host→client transfer of Darkwood save files when the host finishes
    /// generating a new world (or host presses Resend). Client picks a local PLAY profile
    /// for a <b>permanent</b> world copy (mid-menu), then ENTER WORLD offline-loads that
    /// slot. Profile index is always merged from disk before saveGameProfiles.
    /// Dual-box SecondDarkwood uses isolated save roots so host live slots are never hit.
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
        /// Legacy default receive slot (pre permanent-copy picker). Still used as a soft
        /// fallback suggestion when PreferredCoopCopySlot is unset and all slots are full.
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
        /// <summary>Host's profile id from the share package (meta only; client picks local slot).</summary>
        private int _hostSourceProfileId;
        /// <summary>Cached uncompressed package fingerprint while slot-pick buffers are held.</summary>
        private string _pendingPackageFingerprint;

        /// <summary>
        /// Download complete in RAM; waiting for permanent profile slot pick (mid-menu).
        /// </summary>
        private bool _awaitingSlotPick;
        /// <summary>
        /// Files on disk + profile paths ready; waiting for user to press ENTER WORLD
        /// before offline Load (phase 2). Transfer link still up so host keeps peer muted.
        /// </summary>
        private bool _awaitingEnterWorld;
        private int _enterProfileId;
        private int _enterChapterId;

        public bool IsBusy => _hostShareRunning || _clientReceiving || _clientApplying
            || _awaitingSlotPick || _awaitingEnterWorld;
        /// <summary>Client is mid download, slot pick, or apply of host world package.</summary>
        public bool IsClientReceivingOrApplying =>
            _clientReceiving || _clientApplying || _awaitingSlotPick;
        /// <summary>Chunks ready; show permanent slot picker before writing.</summary>
        public bool IsAwaitingSlotPick => _awaitingSlotPick;
        /// <summary>World package written; client must click ENTER WORLD to offline-load.</summary>
        public bool IsAwaitingEnterWorld => _awaitingEnterWorld;
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
            // Phase-2 enter captures locals then StopNetwork → Reset; do not wipe mid-enter apply.
            if (!_clientApplying)
            {
                _chunkBuffers = null;
                _chunksReceived = 0;
                _chunksExpected = 0;
                _awaitingSlotPick = false;
                _awaitingEnterWorld = false;
                _enterProfileId = 0;
                _enterChapterId = 0;
                _hostSourceProfileId = 0;
                _pendingPackageFingerprint = null;
            }
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
            // Force Save() freezes the host for seconds ("Save static"). Prefer on-disk when
            // sav.dat + savs.dat are a consistent pair. Skewed pairs (e.g. only dynamic written)
            // make client SaveManager.Load NRE with "ERROR WHEN LOADING DYNAMIC AND STATIC SAVE"
            // and leave loadingGame stuck — phase-3 reconnect never fires.
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
                FinishHostShare(runAfter: true);
                yield break;
            }

            LogSavPairTimestamps(savPath, savsPath);

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

            // Already have package / permanent copy ready — ignore duplicate host resends
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
            // Host profile id is metadata only — client picks a permanent local slot after download.
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

            // Verify chunks — hold in RAM until user picks a permanent local profile slot.
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

            // Fingerprint uncompressed package vs local permanent copies — skip overwrite
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

        /// <summary>Snapshot of PLAY slots 1–5 for the join mid-menu.</summary>
        public ProfileSlotInfo[] GetProfileSlotInfos()
        {
            var result = new ProfileSlotInfo[MaxProfileId - MinProfileId + 1];
            List<GameProfile> disk = LoadProfilesFromDisk();
            for (int id = MinProfileId; id <= MaxProfileId; id++)
            {
                var info = new ProfileSlotInfo { Id = id };
                bool hasFiles = CoopWorldCopyMeta.SlotHasSaveFiles(id);
                GameProfile gp = null;
                if (disk != null)
                {
                    for (int i = 0; i < disk.Count; i++)
                    {
                        if (disk[i] != null && disk[i].id == id)
                        {
                            gp = disk[i];
                            break;
                        }
                    }
                }
                if (gp == null && Core.profiles != null)
                {
                    for (int i = 0; i < Core.profiles.Count; i++)
                    {
                        if (Core.profiles[i] != null && Core.profiles[i].id == id)
                        {
                            gp = Core.profiles[i];
                            break;
                        }
                    }
                }

                CoopWorldCopyMeta coop = CoopWorldCopyMeta.TryLoad(id);
                info.HasSave = hasFiles || (gp != null && gp.Active && gp.day > 0);
                info.IsEmpty = !info.HasSave;
                info.IsCoopCopy = coop != null && coop.IsCoopCopy;
                info.Day = gp != null ? gp.day : (coop != null ? coop.Day : 0);
                info.Chapter = gp != null ? gp.chapter : (coop != null ? coop.Chapter : 0);
                info.TimeSaved = gp != null ? (gp.timeSaved ?? "") : "";
                if (coop != null)
                {
                    info.CoopNote = "Co-op copy"
                        + (string.IsNullOrEmpty(coop.LastRefreshedAt)
                            ? (string.IsNullOrEmpty(coop.JoinedAt) ? "" : " · joined " + coop.JoinedAt)
                            : " · refreshed " + coop.LastRefreshedAt)
                        + (string.IsNullOrEmpty(coop.HostAddress) ? "" : " · " + coop.HostAddress);
                    // Same-as-incoming uses cached package fingerprint (not re-inflate every OnGUI).
                    if (_awaitingSlotPick && !string.IsNullOrEmpty(_pendingPackageFingerprint)
                        && !string.IsNullOrEmpty(coop.ContentFingerprint))
                    {
                        info.MatchesIncomingPackage = string.Equals(
                            coop.ContentFingerprint, _pendingPackageFingerprint,
                            StringComparison.OrdinalIgnoreCase);
                    }
                }
                result[id - MinProfileId] = info;
            }
            return result;
        }

        /// <summary>
        /// Write verified package into a permanent local profile slot.
        /// Pass overwriteConfirmed=true after UI confirm when the slot already has data.
        /// </summary>
        public bool TryCommitPermanentSlot(int profileId, bool overwriteConfirmed, out string error)
        {
            error = null;
            if (!_awaitingSlotPick || _chunkBuffers == null)
            {
                error = "No host world package waiting for a slot";
                return false;
            }
            if (profileId < MinProfileId || profileId > MaxProfileId)
            {
                error = "Invalid profile slot (use 1–5)";
                return false;
            }
            if (CoopWorldCopyMeta.SlotHasSaveFiles(profileId) && !overwriteConfirmed)
            {
                error = "Slot " + profileId + " already has a save — confirm overwrite";
                return false;
            }

            try
            {
                GameProfile target = EnsureProfileSlot(profileId, _pendingBegin.DayIndex, _pendingBegin.ChapterId);
                Core.currentProfile = target;
                string profDir = GetProfileDir(profileId);
                Directory.CreateDirectory(profDir);

                for (int i = 0; i < _pendingBegin.FileCount; i++)
                {
                    string name = _pendingBegin.FileNames[i];
                    if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                        || (name != "sav.dat" && name != "savs.dat" && name != "savch.dat"))
                    {
                        error = "Bad file name: " + name;
                        return false;
                    }

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
                    if (_pendingBegin.UncompressedSizes[i] > 0
                        && raw.Length != _pendingBegin.UncompressedSizes[i])
                    {
                        error = "Decompressed size mismatch for " + name;
                        return false;
                    }

                    string dest = Path.Combine(profDir, name);
                    string tmp = dest + ".dwmp_tmp";
                    File.WriteAllBytes(tmp, raw);
                    if (File.Exists(dest))
                        File.Delete(dest);
                    File.Move(tmp, dest);

                    ModLog.Event(LogCat.Save,
                        "Permanent co-op copy: wrote " + name + " → prof" + profileId
                        + " (" + raw.Length + " bytes)");
                }

                target.day = _pendingBegin.DayIndex;
                target.chapter = _pendingBegin.ChapterId;
                target.timeSaved = DateTime.Now.ToString();
                target.majorVersion = Core.majorVersion;
                target.minorVersion = Core.minorVersion;
                target.RCVersion = Core.RCVersion;
                target.fullRelease = true;
                target.Active = true;
                // Mark so PLAY list can tell campaign vs co-op if we ever surface it in vanilla UI.
                target.bool1 = true;

                MergeProfileIntoDiskIndexAndSave(target);
                Core.currentProfile = target;

                try
                {
                    SaveManager sm = Singleton<SaveManager>.Instance;
                    if (sm != null)
                        sm.updateFilePaths();
                }
                catch (Exception ex)
                {
                    ModLog.Error(LogCat.Save, "updateFilePaths after permanent copy failed", ex);
                }

                string hostAddr = "";
                try
                {
                    if (ModConfig.ConnectAddress != null)
                        hostAddr = ModConfig.ConnectAddress.Value ?? "";
                }
                catch { /* ignore */ }

                string diskFp = CoopWorldCopyMeta.FingerprintProfileSlot(profileId);
                string savPath = Path.Combine(profDir, "sav.dat");
                string savsPath = Path.Combine(profDir, "savs.dat");
                var existing = CoopWorldCopyMeta.TryLoad(profileId);
                string joinedAt = existing != null && !string.IsNullOrEmpty(existing.JoinedAt)
                    ? existing.JoinedAt
                    : DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                CoopWorldCopyMeta.Write(profileId, new CoopWorldCopyMeta
                {
                    IsCoopCopy = true,
                    HostProfileId = _hostSourceProfileId,
                    Chapter = _pendingBegin.ChapterId,
                    Day = _pendingBegin.DayIndex,
                    WorldSeed = _pendingBegin.ChapterId * 100000 + _pendingBegin.DayIndex,
                    HostAddress = hostAddr,
                    JoinedAt = joinedAt,
                    LastRefreshedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    ContentFingerprint = diskFp,
                    SavBytes = File.Exists(savPath) ? new FileInfo(savPath).Length : 0,
                    SavsBytes = File.Exists(savsPath) ? new FileInfo(savsPath).Length : 0,
                    Note = "Permanent local copy of co-op world. Updated on every session Save. Delete PLAY profile to remove."
                });

                Sync.WorldPhysicsSyncService.Reset();
                Sync.DreamSyncManager.OnDisconnected();
                Sync.MultiplayerMapManager.Reset();
                Sync.DreamSession.ResetIncludingCompletions();
                DeathStateTracker.Reset();

                int chapterId = _pendingBegin.ChapterId > 0 ? _pendingBegin.ChapterId : 1;
                _chunkBuffers = null;
                _awaitingSlotPick = false;
                _awaitingEnterWorld = true;
                _enterProfileId = profileId;
                _enterChapterId = chapterId;

                ProgressText = "Permanent copy on Profile " + profileId + " — press ENTER WORLD";
                if (_net != null)
                    _net.StatusText = ProgressText;
                ModLog.Event(LogCat.Session,
                    "Join pipeline: permanent world on slot " + profileId
                    + " ch" + chapterId + " — waiting for ENTER WORLD");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ModLog.Error(LogCat.Save, "TryCommitPermanentSlot failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Menu: user confirmed enter after download. Starts phase 2 offline load → phase 3.
        /// </summary>
        public bool TryBeginEnterWorld()
        {
            if (!_awaitingEnterWorld)
                return false;
            if (_net == null)
                return false;
            if (!Core.mainMenu)
            {
                ModLog.Warn(LogCat.Save, "TryBeginEnterWorld ignored — not on main menu");
                return false;
            }

            int profileId = _enterProfileId;
            int chapterId = _enterChapterId > 0 ? _enterChapterId : 1;
            _awaitingEnterWorld = false;
            _net.StartCoroutine(ClientOfflineEnterCoroutine(profileId, chapterId));
            return true;
        }

        /// <summary>
        /// Phase 2: drop transfer link, offline Load, ChapterSessionResume → phase 3 reconnect.
        /// </summary>
        private IEnumerator ClientOfflineEnterCoroutine(int profileId, int chapterId)
        {
            _clientApplying = true;
            ProgressText = "Loading host world (offline)…";
            if (_net != null)
                _net.StatusText = ProgressText;

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
                        "Join pipeline phase 2: ENTER WORLD (slot " + profileId
                        + ") — disconnect transfer link, load offline, then phase-3 reconnect");
                    // StopNetwork resets WorldSaveShare; locals already hold load state.
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
            _awaitingSlotPick = false;
            _awaitingEnterWorld = false;
        }

        /// <summary>
        /// Update Core.profiles in RAM from disk merge when possible (never drop other slots).
        /// Prefer <see cref="MergeProfileIntoDiskIndexAndSave"/> on the client receive path.
        /// </summary>
        private static void MergeProfileIntoMemoryOnly(GameProfile slot)
        {
            if (slot == null) return;
            // Start from disk index so we never collapse to a single receive slot in RAM.
            List<GameProfile> profiles = LoadProfilesFromDisk();
            if (profiles == null)
            {
                profiles = Core.profiles != null
                    ? new List<GameProfile>(Core.profiles)
                    : new List<GameProfile>();
            }
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

            // Safety net: keep all 5 PLAY slots in the index.
            // Prior bug wrote profs.dat with only the receive slot (5) → UI showed only slot 5.
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
                if (File.Exists(sav))
                {
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
                else
                {
                    // Empty placeholder so PLAY grid still has 5 slots (NEW GAME on empty).
                    var empty = new GameProfile(id, _Active: false, 0);
                    empty.fullRelease = true;
                    empty.majorVersion = Core.majorVersion;
                    empty.minorVersion = Core.minorVersion;
                    empty.RCVersion = Core.RCVersion;
                    profiles.Add(empty);
                    ModLog.Event(LogCat.Save,
                        "Restored empty profile slot " + id + " in index (was missing after receive-slot merge)");
                }
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

        /// <summary>
        /// sav.dat (dynamic) and savs.dat (static) must be written together. Partial / only-dynamic
        /// saves leave a loadable host RAM world but a client that Load()s the pair hard-fails.
        /// </summary>
        private const double SavPairMaxSkewSeconds = 30.0;

        private static bool OnDiskSavPairNeedsForceSave(string savPath, string savsPath)
        {
            bool hasSav = File.Exists(savPath);
            bool hasSavs = File.Exists(savsPath);
            if (!hasSav && !hasSavs)
                return false;
            if (!hasSav || !hasSavs)
            {
                ModLog.Event(LogCat.Save,
                    "Late-join pair incomplete: sav=" + hasSav + " savs=" + hasSavs);
                return true;
            }

            try
            {
                DateTime savT = File.GetLastWriteTimeUtc(savPath);
                DateTime savsT = File.GetLastWriteTimeUtc(savsPath);
                double skewSec = Math.Abs((savT - savsT).TotalSeconds);
                if (skewSec > SavPairMaxSkewSeconds)
                {
                    ModLog.Event(LogCat.Save,
                        "Late-join pair skew " + skewSec.ToString("F0") + "s (limit "
                        + SavPairMaxSkewSeconds.ToString("F0") + "s) sav=" + savT.ToString("u")
                        + " savs=" + savsT.ToString("u"));
                    return true;
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "Late-join pair mtime check failed: " + ex.Message);
                return true;
            }

            return false;
        }

        private static void LogSavPairTimestamps(string savPath, string savsPath)
        {
            try
            {
                string savInfo = File.Exists(savPath)
                    ? ("sav.dat " + new FileInfo(savPath).Length + "b mtime="
                        + File.GetLastWriteTimeUtc(savPath).ToString("u"))
                    : "sav.dat MISSING";
                string savsInfo = File.Exists(savsPath)
                    ? ("savs.dat " + new FileInfo(savsPath).Length + "b mtime="
                        + File.GetLastWriteTimeUtc(savsPath).ToString("u"))
                    : "savs.dat MISSING";
                ModLog.Event(LogCat.Save, "Share pack pair: " + savInfo + " | " + savsInfo);
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "Share pack pair log failed: " + ex.Message);
            }
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
