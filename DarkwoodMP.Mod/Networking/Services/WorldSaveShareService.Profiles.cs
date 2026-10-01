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
                // Host's own campaign (or an old meta that stamped it) is a campaign, not a copy.
                info.IsCoopCopy = coop != null && coop.IsReceivedCoopCopy;
                info.Day = gp != null ? gp.day : (coop != null ? coop.Day : 0);
                info.Chapter = gp != null ? gp.chapter : (coop != null ? coop.Chapter : 0);
                info.TimeSaved = gp != null ? (gp.timeSaved ?? "") : "";
                if (coop != null)
                {
                    if (info.IsCoopCopy)
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
        /// Every file is validated and inflated before any disk write, and the slot is swapped
        /// atomically (<see cref="CommitInflatedPackage"/>), so a failure never leaves a mixed slot.
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
                if (!TryInflatePackage(out List<VerifiedFile> files, out error))
                    return false;
                return CommitInflatedPackage(profileId, files, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ModLog.Error(LogCat.Save, "TryCommitPermanentSlot failed", ex);
                return false;
            }
        }

    }
}
