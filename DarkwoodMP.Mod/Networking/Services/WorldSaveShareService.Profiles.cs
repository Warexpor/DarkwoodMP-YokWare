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
    /// Profile slot pick, permanent commit, enter-world, and disk merge helpers.
    /// </summary>
    public sealed partial class WorldSaveShareService
    {
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
                    if (_awaitingSlotPick && _pendingBegin.CampaignId != null
                        && !string.IsNullOrEmpty(_pendingBegin.CampaignId)
                        && !string.IsNullOrEmpty(coop.CampaignId)
                        && !string.Equals(coop.CampaignId, _pendingBegin.CampaignId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        info.CampaignMismatchWithHost = true;
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
    }
}
