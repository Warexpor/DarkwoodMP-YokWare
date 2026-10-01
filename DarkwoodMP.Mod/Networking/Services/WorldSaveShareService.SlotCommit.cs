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

                int totalLen = 0;
                for (int c = 0; c < chunks.Length; c++)
                {
                    if (chunks[c] == null)
                    {
                        error = "Missing chunk " + i + ":" + c;
                        return false;
                    }
                    totalLen += chunks[c].Length;
                }

                byte[] compressed = new byte[totalLen];
                int off = 0;
                for (int c = 0; c < chunks.Length; c++)
                {
                    Buffer.BlockCopy(chunks[c], 0, compressed, off, chunks[c].Length);
                    off += chunks[c].Length;
                }

                byte[] raw = Inflate(compressed);
                if (_pendingBegin.UncompressedSizes != null && i < _pendingBegin.UncompressedSizes.Length
                    && _pendingBegin.UncompressedSizes[i] > 0
                    && raw.Length != _pendingBegin.UncompressedSizes[i])
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
        /// atomically (all or none), then the profile index and co-op meta are updated. Any failure
        /// rolls the original files back so a slot is never left with files from two chapters.
        /// </summary>
        private bool CommitInflatedPackage(int profileId, List<VerifiedFile> files, out string error)
        {
            error = null;
            string profDir = GetProfileDir(profileId);
            Directory.CreateDirectory(profDir);

            var swap = new SlotSwap(profDir, files);
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

                GameProfile target = EnsureProfileSlot(profileId, _pendingBegin.DayIndex, _pendingBegin.ChapterId);
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
                // Index / meta failed after the files were swapped: put the old slot back so the
                // profile index and the files on disk still describe the same world.
                error = ex.Message;
                ModLog.Error(LogCat.Save, "Slot " + profileId + " commit failed after file swap — rolling back", ex);
                swap.Rollback();
                prevProfile.Restore();
                Core.currentProfile = prevCurrent;
                InvalidateDiskProfilesCache();
                return false;
            }

            swap.Finish();

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

        /// <summary>
        /// All-or-nothing replacement of a slot's save files: stage every new file as .dwmp_tmp, move
        /// the current set aside as .dwmp_bak, rename the staged files into place. Any failure puts
        /// the original files back; nothing is deleted until <see cref="Finish"/>.
        /// </summary>
        private sealed class SlotSwap
        {
            private const string TmpExt = ".dwmp_tmp";
            private const string BakExt = ".dwmp_bak";

            private readonly string _dir;
            private readonly List<VerifiedFile> _files;
            private readonly List<string> _staged = new List<string>(3);
            private readonly List<string> _installed = new List<string>(3);
            private readonly List<KeyValuePair<string, string>> _aside = new List<KeyValuePair<string, string>>(3);

            public SlotSwap(string dir, List<VerifiedFile> files)
            {
                _dir = dir;
                _files = files;
            }

            public bool TryStageAndSwap(out string error)
            {
                error = null;
                try
                {
                    foreach (VerifiedFile f in _files)
                    {
                        string tmp = Path.Combine(_dir, f.Name) + TmpExt;
                        File.WriteAllBytes(tmp, f.Raw);
                        _staged.Add(tmp);
                        if (new FileInfo(tmp).Length != f.Raw.Length)
                            throw new IOException("short write staging " + f.Name);
                    }

                    // The whole save set moves aside, including names this package does not carry:
                    // a stale savch.dat from another chapter must not survive next to the new files.
                    foreach (string name in FileNames)
                    {
                        string dest = Path.Combine(_dir, name);
                        if (!File.Exists(dest))
                            continue;
                        string bak = dest + BakExt;
                        if (File.Exists(bak))
                            File.Delete(bak);
                        File.Move(dest, bak);
                        _aside.Add(new KeyValuePair<string, string>(dest, bak));
                    }

                    foreach (VerifiedFile f in _files)
                    {
                        string dest = Path.Combine(_dir, f.Name);
                        File.Move(dest + TmpExt, dest);
                        _installed.Add(dest);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = "could not write the world to disk: " + ex.Message;
                    ModLog.Error(LogCat.Save, "Slot swap failed in " + _dir + " — rolling back", ex);
                    Rollback();
                    return false;
                }
            }

            public void Rollback()
            {
                foreach (string dest in _installed)
                    TryDelete(dest);
                _installed.Clear();

                foreach (KeyValuePair<string, string> pair in _aside)
                {
                    try
                    {
                        if (File.Exists(pair.Key))
                            File.Delete(pair.Key);
                        File.Move(pair.Value, pair.Key);
                    }
                    catch (Exception ex)
                    {
                        ModLog.Error(LogCat.Save,
                            "Could not restore " + pair.Key + " from " + pair.Value
                            + " — the original is still saved as the .dwmp_bak file", ex);
                    }
                }
                _aside.Clear();

                foreach (string tmp in _staged)
                    TryDelete(tmp);
                _staged.Clear();
            }

            /// <summary>New set is live: the backups are no longer needed.</summary>
            public void Finish()
            {
                foreach (KeyValuePair<string, string> pair in _aside)
                    TryDelete(pair.Value);
                _aside.Clear();
                _staged.Clear();
                _installed.Clear();
            }

            private static void TryDelete(string path)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Save, "Could not delete " + path + ": " + ex.Message);
                }
            }
        }
    }
}
