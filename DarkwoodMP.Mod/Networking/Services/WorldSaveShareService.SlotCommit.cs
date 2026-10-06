using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Verify + inflate the received package, then swap it into a profile slot atomically.
    /// Nothing here writes to disk until every file has been validated and inflated.
    /// </summary>
    public sealed partial class WorldSaveShareService
    {
        private sealed class VerifiedFile
        {
            public string Name;
            public byte[] Raw;
        }

        /// <summary>
        /// Validate names and sizes and inflate every file of the buffered package into memory.
        /// Pure: touches no disk, no profile, no session state. Throws on a corrupt deflate stream.
        /// </summary>
        private bool TryInflatePackage(out List<VerifiedFile> files, out string error)
        {
            files = null;
            error = null;
            if (_chunkBuffers == null)
            {
                error = "No host world package buffered";
                return false;
            }

            var list = new List<VerifiedFile>(_pendingBegin.FileCount);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _pendingBegin.FileCount; i++)
            {
                string name = _pendingBegin.FileNames != null && i < _pendingBegin.FileNames.Length
                    ? _pendingBegin.FileNames[i] : null;
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || (name != "sav.dat" && name != "savs.dat" && name != "savch.dat"))
                {
                    error = "Bad file name: " + name;
                    return false;
                }
                if (!seen.Add(name))
                {
                    error = "Duplicate file in package: " + name;
                    return false;
                }
                if (!_chunkBuffers.TryGetValue(i, out byte[][] chunks))
                {
                    error = "Missing file buffer " + i;
                    return false;
                }

                long totalLen = 0;
                for (int c = 0; c < chunks.Length; c++)
                {
                    if (chunks[c] == null)
                    {
                        error = "Missing chunk " + i + ":" + c;
                        return false;
                    }
                    totalLen += chunks[c].Length;
                }
                if (totalLen > MaxInflatedFileBytes)
                {
                    error = "Compressed " + name + " too large (" + totalLen + " bytes)";
                    return false;
                }

                byte[] compressed = new byte[totalLen];
                int off = 0;
                for (int c = 0; c < chunks.Length; c++)
                {
                    Buffer.BlockCopy(chunks[c], 0, compressed, off, chunks[c].Length);
                    off += chunks[c].Length;
                }

                int declared = _pendingBegin.UncompressedSizes != null && i < _pendingBegin.UncompressedSizes.Length
                    ? _pendingBegin.UncompressedSizes[i] : 0;
                if (declared < 0 || declared > MaxInflatedFileBytes)
                {
                    error = "Declared size out of range for " + name + " (" + declared + ")";
                    return false;
                }
                byte[] raw;
                try { raw = Inflate(compressed, declared); }
                catch (InvalidDataException ex)
                {
                    error = "Corrupt or oversized " + name + ": " + ex.Message;
                    return false;
                }
                if (declared > 0 && raw.Length != declared)
                {
                    error = "Decompressed size mismatch for " + name;
                    return false;
                }
                list.Add(new VerifiedFile { Name = name, Raw = raw });
            }

            // The game loads sav.dat (dynamic) and savs.dat (static) as a pair; one without the
            // other is a load failure waiting to happen, and committing it would orphan the old half.
            if (!seen.Contains("sav.dat") || !seen.Contains("savs.dat"))
            {
                error = "Package incomplete (needs sav.dat and savs.dat)";
                return false;
            }

            files = list;
            return true;
        }

        /// <summary>
        /// Install an inflated package into <paramref name="profileId"/>: files are swapped
        /// atomically (all or none), then the profile index is saved. The swap commits only once
        /// the index is on disk; a failure before that rolls the original files back (and a crash
        /// is rolled back at the next start), so a slot never mixes files from two worlds.
        /// </summary>
        private bool CommitInflatedPackage(int profileId, List<VerifiedFile> files, out string error)
        {
            error = null;
            string profDir = GetProfileDir(profileId);
            Directory.CreateDirectory(profDir);

            var set = new List<KeyValuePair<string, byte[]>>(files.Count);
            foreach (VerifiedFile f in files)
                set.Add(new KeyValuePair<string, byte[]>(f.Name, f.Raw));
            var swap = new SlotSwap(profDir, set);
            if (!swap.TryStageAndSwap(out error))
                return false;

            GameProfile prevCurrent = Core.currentProfile;
            ProfileSnapshot prevProfile = ProfileSnapshot.Capture(profileId);
            try
            {
                foreach (VerifiedFile f in files)
                    ModLog.Event(LogCat.Save,
                        "Permanent co-op copy: wrote " + f.Name + " → prof" + profileId
                        + " (" + f.Raw.Length + " bytes)");

                GameProfile target = EnsureProfileSlot(profileId, _pendingBegin.DayIndex, _pendingBegin.ChapterId, _pendingBegin.Difficulty);
                Core.currentProfile = target;
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

                // Index failure propagates to the rollback below (it used to be swallowed inside).
                if (!MergeProfileIntoDiskIndexAndSave(target))
                    throw new IOException("profile index could not be saved");
                Core.currentProfile = target;
            }
            catch (Exception ex)
            {
                // Files swapped but the index does not describe them: put the old slot back so the
                // profile index and the files on disk still describe the same world.
                error = ex.Message;
                ModLog.Error(LogCat.Save, "Slot " + profileId + " commit failed after file swap — rolling back", ex);
                swap.Rollback();
                prevProfile.Restore();
                Core.currentProfile = prevCurrent;
                InvalidateDiskProfilesCache();
                return false;
            }

            // Commit point: index and files agree. Everything after is metadata.
            swap.Commit();

            try
            {
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
                    OwnCampaign = false,
                    HostProfileId = _hostSourceProfileId,
                    Chapter = _pendingBegin.ChapterId,
                    Day = _pendingBegin.DayIndex,
                    WorldSeed = _pendingBegin.ChapterId * 100000 + _pendingBegin.DayIndex,
                    HostAddress = hostAddr,
                    JoinedAt = joinedAt,
                    LastRefreshedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    ContentFingerprint = diskFp,
                    CampaignId = !string.IsNullOrEmpty(_pendingBegin.CampaignId)
                        ? _pendingBegin.CampaignId
                        : (existing != null ? existing.CampaignId : null),
                    SavBytes = File.Exists(savPath) ? new FileInfo(savPath).Length : 0,
                    SavsBytes = File.Exists(savsPath) ? new FileInfo(savsPath).Length : 0,
                    Note = "Permanent local copy of co-op world. Updated on every session Save. Delete PLAY profile to remove."
                });
                if (string.IsNullOrEmpty(_pendingBegin.CampaignId)
                    && (existing == null || string.IsNullOrEmpty(existing.CampaignId)))
                    CoopWorldCopyMeta.GetOrCreateCampaignId(profileId);
            }
            catch (Exception ex)
            {
                ModLog.Error(LogCat.Save, "Co-op meta update after slot " + profileId + " commit failed", ex);
            }

            // The world in memory is about to be replaced by the one just written.
            Sync.WorldPhysicsSyncService.Reset();
            Sync.DreamSyncManager.OnDisconnected();
            Sync.MultiplayerMapManager.Reset();
            Sync.DreamSession.ResetIncludingCompletions();
            DeathStateTracker.ResetSession();

            int chapterId = _pendingBegin.ChapterId > 0 ? _pendingBegin.ChapterId : 1;
            _chunkBuffers = null;
            _verifiedPackage = null;
            _awaitingChapterGo = false;
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

        /// <summary>
        /// Chapter share, at the host's go: write the package held since verification into the
        /// current profile and start the offline enter. Until this runs the slot is untouched.
        /// </summary>
        internal bool TryCommitBufferedChapterAndEnter(out string error)
        {
            error = null;
            if (_verifiedPackage == null || Core.currentProfile == null)
            {
                error = "no verified chapter world is held";
                return false;
            }
            int profileId = Core.currentProfile.id;
            if (profileId < MinProfileId || profileId > MaxProfileId)
            {
                error = "invalid current profile " + profileId;
                return false;
            }
            try
            {
                if (!CommitInflatedPackage(profileId, _verifiedPackage, out error))
                    return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ModLog.Error(LogCat.Save, "Chapter commit at go failed", ex);
                return false;
            }
            return TryBeginEnterWorld(allowInGame: true);
        }

        /// <summary>Host aborted (or the session ended): drop the held package; the slot was never touched.</summary>
        internal void DiscardPendingChapterPackage()
        {
            if (_verifiedPackage == null && !_awaitingChapterGo)
                return;
            _verifiedPackage = null;
            _awaitingChapterGo = false;
            _chunkBuffers = null;
            ModLog.Event(LogCat.Save, "Chapter world package discarded — save slot left untouched");
        }

        /// <summary>In-memory profile fields restored when a post-swap step fails.</summary>
        private struct ProfileSnapshot
        {
            private GameProfile _profile;
            private int _id;
            private bool _existed;
            private bool _active;
            private int _day;
            private int _chapter;
            private string _timeSaved;
            private bool _fullRelease;
            private bool _bool1;

            public static ProfileSnapshot Capture(int profileId)
            {
                var snap = new ProfileSnapshot { _id = profileId };
                if (Core.profiles == null)
                    return snap;
                for (int i = 0; i < Core.profiles.Count; i++)
                {
                    GameProfile p = Core.profiles[i];
                    if (p == null || p.id != profileId)
                        continue;
                    snap._profile = p;
                    snap._existed = true;
                    snap._active = p.Active;
                    snap._day = p.day;
                    snap._chapter = p.chapter;
                    snap._timeSaved = p.timeSaved;
                    snap._fullRelease = p.fullRelease;
                    snap._bool1 = p.bool1;
                    break;
                }
                return snap;
            }

            public void Restore()
            {
                if (Core.profiles == null)
                    return;
                if (!_existed)
                {
                    // EnsureProfileSlot created it for this commit.
                    for (int i = Core.profiles.Count - 1; i >= 0; i--)
                    {
                        if (Core.profiles[i] != null && Core.profiles[i].id == _id)
                            Core.profiles.RemoveAt(i);
                    }
                    return;
                }
                _profile.Active = _active;
                _profile.day = _day;
                _profile.chapter = _chapter;
                _profile.timeSaved = _timeSaved;
                _profile.fullRelease = _fullRelease;
                _profile.bool1 = _bool1;
            }
        }
    }
}
