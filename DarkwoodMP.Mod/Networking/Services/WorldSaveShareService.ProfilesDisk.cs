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
        /// </summary>
        private static List<GameProfile> LoadProfilesFromDisk()
        {
            var sm = Singleton<SaveManager>.Instance;
            if (sm == null) return null;

            try
            {
                // Prefer private GetProfiles(), which matches Yokyy and returns MainMenu.SaveState with .profiles
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
    }
}
