using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YokWare.ManualSaves
{
    public class ManualSaveSlotMeta
    {
        public int day;
        public int chapter;
        public string timeSaved;
        public int majorVersion;
        public int minorVersion;
        public bool hasData;
    }

    /// <summary>
    /// Ten extra save slots per PLAY profile: a copy of the profile's whole save set, taken right
    /// after a real save, and put back (offline only) to load it. The co-op rules come from the
    /// co-op mod: a joined client cannot save the host's world, nothing is saved during a partial
    /// night death, a dream or the prologue, and loading needs the session to be left first.
    /// </summary>
    internal sealed class SaveSlots
    {
        public const int Count = 10;

        public readonly ManualSaveSlotMeta[] Metas = new ManualSaveSlotMeta[Count];
        /// <summary>Per slot: where a save goes (this profile's own folder).</summary>
        private readonly string[] _paths = new string[Count];
        /// <summary>Per slot: where the shown data is read from (own folder, else the old shared one).</summary>
        private readonly string[] _readPaths = new string[Count];
        public readonly bool[] IsLegacy = new bool[Count];

        private static string SaveDir => Application.persistentDataPath + "/1_4Save";
        /// <summary>Pre-profile layout: one set of slots shared by every profile. Read-only fallback.</summary>
        private static string LegacySlotsBase => SaveDir + "/manual_saves";
        /// <summary>Slots belong to one PLAY profile, so another profile's world never shows up here.</summary>
        private static string SlotsBaseFor(int profileId) => SaveDir + "/manual_saves/prof" + profileId;

        public SaveSlots()
        {
            for (int i = 0; i < Count; i++)
                Metas[i] = new ManualSaveSlotMeta();
        }

        /// <summary>Resolve slot folders for the current profile and reload what each slot shows.</summary>
        public void Refresh()
        {
            int profileId = Core.currentProfile != null ? Core.currentProfile.id : 0;
            for (int i = 0; i < Count; i++)
            {
                string own = SlotsBaseFor(profileId) + "/slot" + (i + 1);
                string legacy = LegacySlotsBase + "/slot" + (i + 1);
                _paths[i] = own;
                bool useLegacy = !File.Exists(own + "/sav.dat") && File.Exists(legacy + "/sav.dat");
                _readPaths[i] = useLegacy ? legacy : own;
                IsLegacy[i] = useLegacy;
                Metas[i] = ReadMeta(_readPaths[i]);
            }
        }

        private static ManualSaveSlotMeta ReadMeta(string slotDir)
        {
            string metaPath = slotDir + "/meta.json";
            if (File.Exists(metaPath))
            {
                try
                {
                    var m = JsonConvert.DeserializeObject<ManualSaveSlotMeta>(File.ReadAllText(metaPath));
                    if (m != null)
                    {
                        m.hasData = m.hasData && File.Exists(slotDir + "/sav.dat");
                        return m;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[ManualSave] failed to read slot meta: " + ex);
                }
            }
            return new ManualSaveSlotMeta { hasData = File.Exists(slotDir + "/sav.dat") };
        }

        /// <summary>Why saving into a slot is not possible right now (English), or null.</summary>
        public static string SaveBlockReason()
        {
            if (Singleton<SaveManager>.Instance == null)
                return "Error: SaveManager not available";
            // A connected client's world Save is blocked (the host owns the world), so the profile
            // files on disk are stale: copying them would report a save that never happened.
            if (AddOnApi.IsClient)
                return "Blocked: only the host can save the co-op world";
            if (Core.currentProfile == null)
                return "Error: no active profile";
            string blocked = AddOnApi.WorldSaveBlockReason();
            if (blocked != null)
                return "Blocked: cannot save during " + blocked;
            return null;
        }

        /// <summary>Why loading a slot is not possible right now (English), or null.</summary>
        public static string LoadBlockReason()
        {
            if (Core.currentProfile == null)
                return "Error: no active profile";
            if (Singleton<SaveManager>.Instance == null)
                return "Error: SaveManager not available";
            // Loading swaps the live profile files and reloads the chapter: that would pull the
            // host's world out from under connected peers (or desync a client from its host).
            if (AddOnApi.InSession)
                return "Blocked: leave the co-op session first (Multiplayer > Disconnect), then load";
            if (Core.loadingGame || !Core.coreStarted || Player.Instance == null)
                return "Blocked: wait until the game finishes loading";
            return null;
        }

        /// <summary>Save now and copy the profile into slot <paramref name="idx"/>. Returns the status line (English).</summary>
        public string Save(int idx)
        {
            try
            {
                string blocked = SaveBlockReason();
                if (blocked != null)
                    return blocked;
                string profDir = SaveDir + "/prof" + Core.currentProfile.id;
                string slotDir = _paths[idx];
                string savPath = profDir + "/sav.dat";
                DateTime savBefore = File.Exists(savPath) ? File.GetLastWriteTimeUtc(savPath) : DateTime.MinValue;

                // In co-op the host's Save fans out, so every peer saves with the Saving sign.
                Singleton<SaveManager>.Instance.Save(true, true, true, false, true);

                // Save returns silently when it declines (loading, patched out): only report
                // success when the profile file really changed.
                if (!File.Exists(savPath) || File.GetLastWriteTimeUtc(savPath) <= savBefore)
                {
                    Log.Info("[ManualSave] Save produced no new sav.dat (" + savPath + ")");
                    return "Save did not write — try again in a moment";
                }

                // Whole set in one read, then an atomic swap into the slot: a stale savch.dat
                // from an older save in this slot is removed with the rest.
                if (!CopySaveSet(profDir, slotDir, out string copyError))
                    return "Save error: " + copyError;

                var meta = new ManualSaveSlotMeta
                {
                    day = Core.currentProfile.day,
                    chapter = Core.currentProfile.chapter,
                    timeSaved = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    majorVersion = Core.majorVersion,
                    minorVersion = Core.minorVersion,
                    hasData = true
                };
                AddOnApi.WriteTextAtomic(slotDir + "/meta.json", JsonConvert.SerializeObject(meta, Formatting.Indented));

                Refresh();
                Log.Info("[ManualSave] saved profile " + Core.currentProfile.id + " to slot " + (idx + 1));
                return "Saved to slot " + (idx + 1);
            }
            catch (Exception ex)
            {
                Log.Error("[ManualSave] Save error: " + ex);
                return "Save error: " + ex.Message;
            }
        }

        /// <summary>Put slot <paramref name="idx"/> back into the profile and load it. Returns the status line on failure.</summary>
        public string Load(int idx)
        {
            try
            {
                string blocked = LoadBlockReason();
                if (blocked != null)
                    return blocked;
                if (!File.Exists(_readPaths[idx] + "/sav.dat"))
                    return "Slot " + (idx + 1) + " is empty";

                ManualSaveSlotMeta meta = Metas[idx];
                string profDir = SaveDir + "/prof" + Core.currentProfile.id;
                string slotDir = _readPaths[idx];

                // Atomic: the profile ends up with exactly the slot's set (a savch.dat the slot
                // does not have is removed), or untouched when anything fails.
                if (!CopySaveSet(slotDir, profDir, out string copyError))
                    return "Load error: " + copyError;

                // A slot is a different save instance: a new campaign id, so a co-op backup of
                // another copy cannot apply to it, and the co-op mod's world state is dropped.
                AddOnApi.BeforeOfflineLoad(Core.currentProfile.id);

                Core.currentProfile.day = meta.day;
                Core.currentProfile.chapter = meta.chapter;
                Core.currentProfile.timeSaved = meta.timeSaved;
                Core.currentProfile.majorVersion = meta.majorVersion;
                Core.currentProfile.minorVersion = meta.minorVersion;

                // Offline load: the slot's sav.dat holds the character. No personal backup is
                // snapshotted or overlaid — that put the pre-load inventory back on (item dupe).
                Singleton<SaveManager>.Instance.saveGameProfiles();

                int chapterId = meta.chapter > 0 ? meta.chapter : 1;
                Log.Info("[ManualSave] loading slot " + (idx + 1) + " into profile " + Core.currentProfile.id + " (chapter " + chapterId + ")");

                Core.coreStarted = false;
                Core.mainMenu = false;
                Core.loadingGame = true;
                Core.loadedGame = true;
                Time.timeScale = 1f;

                if (Singleton<MainMenu>.Instance != null)
                    Singleton<MainMenu>.Instance.close();

                SceneManager.LoadScene("chapter" + chapterId);
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("[ManualSave] Load error: " + ex);
                return "Load error: " + ex.Message;
            }
        }

        /// <summary>Copy a whole save set from one folder to another through the crash-safe swap.</summary>
        private static bool CopySaveSet(string srcDir, string dstDir, out string error)
        {
            List<KeyValuePair<string, byte[]>> set;
            try { set = AddOnApi.ReadSaveSet(srcDir); }
            catch (Exception ex)
            {
                error = "could not read " + srcDir + ": " + ex.Message;
                return false;
            }
            bool hasSav = false;
            foreach (var f in set)
                if (f.Key == "sav.dat") hasSav = true;
            if (!hasSav)
            {
                error = "no sav.dat in " + srcDir;
                return false;
            }
            return AddOnApi.TryReplaceSaveSet(dstDir, set, out error);
        }
    }
}
