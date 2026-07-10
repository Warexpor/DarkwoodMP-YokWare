using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using DWMPHorde.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// One-shot host→client transfer of Darkwood save files when the host finishes
    /// generating a new world (or host presses Resend). Clients write into the
    /// <b>same profile slot number</b> as the host (e.g. host prof5 → client prof5),
    /// not whatever profile the client currently has selected.
    /// </summary>
    public sealed class WorldSaveShareService
    {
        public const int ChunkSize = 16 * 1024;
        private const int MaxChunksPerFrame = 6;
        private const float HostWaitForSaveSeconds = 2.5f;
        private const int MinProfileId = 1;
        private const int MaxProfileId = 5;

        private static readonly string[] FileNames = { "savs.dat", "sav.dat", "savch.dat" };

        private readonly LanNetworkManager _net;
        private bool _hostShareRunning;
        private bool _clientReceiving;
        private bool _clientApplying;
        private Action _afterHostShare;

        private WorldSaveBeginMessage _pendingBegin;
        private Dictionary<int, byte[][]> _chunkBuffers;
        private int _chunksReceived;
        private int _chunksExpected;

        public bool IsBusy => _hostShareRunning || _clientReceiving || _clientApplying;
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
            ScheduleHostShare(waitForGameSave: true, afterShare: null);
        }

        /// <summary>
        /// Host only: manual fallback — save now and push current profile files to clients.
        /// </summary>
        public void ScheduleHostResend()
        {
            ScheduleHostShare(waitForGameSave: false, afterShare: null);
        }

        /// <summary>
        /// Host only: push current save files, then run <paramref name="afterShare"/> (e.g. chapter LoadScene).
        /// </summary>
        public void ScheduleHostShareThen(Action afterShare, bool waitForGameSave = false)
        {
            ScheduleHostShare(waitForGameSave, afterShare);
        }

        private void ScheduleHostShare(bool waitForGameSave, Action afterShare)
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
                // Chain: run after current share finishes
                if (afterShare != null)
                {
                    var prev = _afterHostShare;
                    _afterHostShare = () =>
                    {
                        try { prev?.Invoke(); } catch { /* ignore */ }
                        afterShare();
                    };
                }
                return;
            }

            _afterHostShare = afterShare;
            _net.StartCoroutine(HostShareCoroutine(waitForGameSave));
        }

        private IEnumerator HostShareCoroutine(bool waitForGameSave)
        {
            _hostShareRunning = true;
            ProgressText = waitForGameSave ? "Preparing world share…" : "Resending world…";
            ModLog.Event(LogCat.Save,
                waitForGameSave
                    ? "New world finished — preparing save share for clients"
                    : "Manual world resend requested");

            if (waitForGameSave)
            {
                float waited = 0f;
                while (waited < HostWaitForSaveSeconds)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
            }

            try
            {
                if (Singleton<SaveManager>.Instance != null)
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
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "Host save before world share failed", ex);
                ProgressText = "World share failed (save error)";
                _net.StatusText = ProgressText;
                FinishHostShare(runAfter: true);
                yield break;
            }

            yield return null;

            int profileId = GetHostProfileId();
            if (profileId < MinProfileId || profileId > MaxProfileId)
            {
                ModLog.Error(LogCat.Save, "Host profile id invalid: " + profileId);
                ProgressText = "World share failed (bad profile id)";
                _net.StatusText = ProgressText;
                FinishHostShare(runAfter: true);
                yield break;
            }

            string profDir = GetProfileDir(profileId);
            if (string.IsNullOrEmpty(profDir) || !Directory.Exists(profDir))
            {
                ModLog.Error(LogCat.Save, "Host profile directory missing: " + profDir);
                ProgressText = "World share failed (no profile dir)";
                _net.StatusText = ProgressText;
                FinishHostShare(runAfter: true);
                yield break;
            }

            var files = new List<PackedFile>();
            foreach (string name in FileNames)
            {
                string path = Path.Combine(profDir, name);
                if (!File.Exists(path))
                    continue;

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

                byte[] compressed = Deflate(raw);
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
                }

                files.Add(new PackedFile
                {
                    Name = name,
                    UncompressedSize = raw.Length,
                    CompressedSize = compressed.Length,
                    Chunks = chunks
                });
            }

            if (files.Count == 0)
            {
                ModLog.Error(LogCat.Save, "No save files found to share for prof" + profileId);
                ProgressText = "World share failed (no files)";
                _net.StatusText = ProgressText;
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
                "Sharing world → clients profile slot " + profileId
                + ": " + files.Count + " files, " + totalChunks + " chunks, ch" + chapter + " day" + day);

            _net.SendToAll(NetMessageType.WorldSaveBegin, w => begin.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);

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
                    _net.SendToAll(NetMessageType.WorldSaveChunk, w =>
                    {
                        new WorldSaveChunkMessage
                        {
                            FileIndex = fileIndex,
                            ChunkIndex = chunkIndex,
                            Data = data
                        }.Serialize(w);
                    }, LiteNetLib.DeliveryMethod.ReliableOrdered);

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

            _net.SendToAll(NetMessageType.WorldSaveEnd, w =>
            {
                new WorldSaveEndMessage { Success = true }.Serialize(w);
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);

            ProgressText = "World shared → client profile " + profileId;
            _net.StatusText = ProgressText;
            ModLog.Event(LogCat.Save, "World save share complete (" + sent + " chunks → slot " + profileId + ")");
            FinishHostShare(runAfter: true);
        }

        private void FinishHostShare(bool runAfter)
        {
            _hostShareRunning = false;
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

            _clientReceiving = true;
            _clientApplying = false;
            _pendingBegin = msg;
            _chunksReceived = 0;
            _chunksExpected = 0;
            _chunkBuffers = new Dictionary<int, byte[][]>();

            for (int i = 0; i < msg.FileCount; i++)
            {
                int n = msg.ChunkCounts[i];
                if (n < 0 || n > 100000) n = 0;
                _chunkBuffers[i] = new byte[n][];
                _chunksExpected += n;
            }

            ProgressText = "Receiving world → slot " + msg.ProfileId + "…";
            _net.StatusText = ProgressText;
            ModLog.Event(LogCat.Save,
                "Receiving host world for profile slot " + msg.ProfileId
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
                ProgressText = "World share failed (host)";
                _net.StatusText = ProgressText;
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

            try
            {
                if (profileId < MinProfileId || profileId > MaxProfileId)
                    throw new InvalidOperationException("Invalid host profile id: " + profileId);

                // Verify all chunks present
                for (int i = 0; i < _pendingBegin.FileCount; i++)
                {
                    if (!_chunkBuffers.TryGetValue(i, out byte[][] chunks))
                        throw new InvalidOperationException("Missing file buffer " + i);
                    for (int c = 0; c < chunks.Length; c++)
                    {
                        if (chunks[c] == null)
                            throw new InvalidOperationException("Missing chunk " + i + ":" + c);
                    }
                }

                // Same slot number as host — never overwrite a different client-selected profile.
                GameProfile target = EnsureProfileSlot(profileId, _pendingBegin.DayIndex, _pendingBegin.ChapterId);
                Core.currentProfile = target;

                string profDir = GetProfileDir(profileId);
                Directory.CreateDirectory(profDir);

                for (int i = 0; i < _pendingBegin.FileCount; i++)
                {
                    string name = _pendingBegin.FileNames[i];
                    if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw new InvalidOperationException("Bad file name: " + name);
                    if (name != "sav.dat" && name != "savs.dat" && name != "savch.dat")
                        throw new InvalidOperationException("Unexpected file: " + name);

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

                    if (compressed.Length != _pendingBegin.CompressedSizes[i]
                        && _pendingBegin.CompressedSizes[i] > 0)
                    {
                        ModLog.Warn(LogCat.Save,
                            "Compressed size mismatch for " + name + ": got " + compressed.Length
                            + " expected " + _pendingBegin.CompressedSizes[i]);
                    }

                    byte[] raw = Inflate(compressed);
                    if (_pendingBegin.UncompressedSizes[i] > 0 && raw.Length != _pendingBegin.UncompressedSizes[i])
                    {
                        throw new InvalidOperationException(
                            "Decompressed size mismatch for " + name + ": " + raw.Length
                            + " vs " + _pendingBegin.UncompressedSizes[i]);
                    }

                    string dest = Path.Combine(profDir, name);
                    string tmp = dest + ".dwmp_tmp";
                    File.WriteAllBytes(tmp, raw);
                    if (File.Exists(dest))
                        File.Delete(dest);
                    File.Move(tmp, dest);
                    ModLog.Event(LogCat.Save,
                        "Wrote host world file " + name + " → prof" + profileId + " (" + raw.Length + " bytes)");
                }

                target.day = _pendingBegin.DayIndex;
                target.chapter = _pendingBegin.ChapterId;
                target.timeSaved = DateTime.Now.ToString();
                target.majorVersion = Core.majorVersion;
                target.minorVersion = Core.minorVersion;
                target.RCVersion = Core.RCVersion;
                target.fullRelease = true;
                target.Active = true;

                // Keep Core.profiles list in sync so load uses this slot.
                if (Core.profiles != null)
                {
                    bool listed = false;
                    for (int i = 0; i < Core.profiles.Count; i++)
                    {
                        if (Core.profiles[i] != null && Core.profiles[i].id == profileId)
                        {
                            Core.profiles[i] = target;
                            listed = true;
                            break;
                        }
                    }
                    if (!listed)
                        Core.profiles.Add(target);
                }

                Core.currentProfile = target;

                if (Singleton<SaveManager>.Instance != null)
                    Singleton<SaveManager>.Instance.saveGameProfiles();

                Sync.WorldPhysicsSyncService.Reset();
                Sync.DreamSyncManager.OnDisconnected();
                Sync.MultiplayerMapManager.Reset();
                Sync.DreamSession.ResetIncludingCompletions();
                DeathStateTracker.Reset();

                int chapterId = _pendingBegin.ChapterId > 0 ? _pendingBegin.ChapterId : 1;

                Core.coreStarted = false;
                Core.mainMenu = false;
                Core.loadingGame = true;
                Core.loadedGame = true;
                Time.timeScale = 1f;

                if (Singleton<MainMenu>.Instance != null)
                    Singleton<MainMenu>.Instance.close();

                ProgressText = "Loading host world (profile " + profileId + ")…";
                _net.StatusText = ProgressText;
                ModLog.Event(LogCat.Save,
                    "Loading chapter" + chapterId + " with host world on profile slot " + profileId);

                SceneManager.LoadScene("chapter" + chapterId);
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "Failed to apply host world", ex);
                ProgressText = "World apply failed";
                _net.StatusText = ProgressText;
            }
            finally
            {
                _chunkBuffers = null;
                _clientApplying = false;
            }
        }

        /// <summary>
        /// Find or create the GameProfile with the host's slot id so client files go to profN.
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

        private static int GetHostProfileId()
        {
            if (Core.currentProfile != null)
                return Core.currentProfile.id;
            return 0;
        }

        private static string GetProfileDir(int profileId)
        {
            return Application.persistentDataPath + "/1_4Save/prof" + profileId;
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
