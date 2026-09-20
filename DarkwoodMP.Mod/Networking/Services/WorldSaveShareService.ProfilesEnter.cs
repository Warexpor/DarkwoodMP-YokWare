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
        public bool TryBeginEnterWorld()
        {
            if (!_awaitingEnterWorld)
                return false;
            if (_net == null)
                return false;
            if (HasTerminalShareFailure)
            {
                ModLog.Warn(LogCat.Save,
                    "TryBeginEnterWorld blocked — " + ProgressText);
                return false;
            }
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
        /// Never call bare saveGameProfiles() with a partial Core.profiles; that wipes PLAY slots.
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
    }
}
