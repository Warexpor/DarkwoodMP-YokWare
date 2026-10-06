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
        /// <summary>
        /// Cached while slot-pick UI polls every OnGUI — avoid re-invoking SaveManager
        /// hundreds of times (empty Darkwood_Second had no profs.dat → WARN spam).
        /// </summary>
        private static List<GameProfile> _cachedDiskProfiles; // process-scoped: disk profile cache, not session state
        private static float _cachedDiskProfilesAt = -999f; // process-scoped: disk profile cache, not session state
        private static bool _diskProfilesCacheValid; // process-scoped: disk profile cache, not session state
        private static bool _loggedMissingProfs; // process-scoped: disk profile cache, not session state
        private const float DiskProfilesCacheSeconds = 2f;

        private static List<GameProfile> LoadProfilesFromDisk()
        {
            float now = Time.unscaledTime;
            if (_diskProfilesCacheValid
                && (now - _cachedDiskProfilesAt) < DiskProfilesCacheSeconds)
                return _cachedDiskProfiles;

            var sm = Singleton<SaveManager>.Instance;
            if (sm == null) return null;

            List<GameProfile> loaded = TryLoadProfilesViaPublicApi(sm);
            if (loaded == null)
                loaded = TryLoadProfilesViaPrivateGet(sm);

            _cachedDiskProfiles = loaded;
            _cachedDiskProfilesAt = now;
            _diskProfilesCacheValid = true;
            return loaded;
        }

        /// <summary>Invalidate after we rewrite profs.dat / merge a receive slot.</summary>
        internal static void InvalidateDiskProfilesCache()
        {
            _cachedDiskProfiles = null;
            _cachedDiskProfilesAt = -999f;
            _diskProfilesCacheValid = false;
        }

        private static List<GameProfile> ExtractProfilesList(object state)
        {
            if (state == null) return null;
            var field = state.GetType().GetField("profiles",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.GetValue(state) is List<GameProfile> list)
                return new List<GameProfile>(list);
            return null;
        }

        /// <summary>
        /// Public loadGameProfiles Exists-guards profs.dat — safe on empty dual-box roots.
        /// </summary>
        private static List<GameProfile> TryLoadProfilesViaPublicApi(SaveManager sm)
        {
            try
            {
                var load = typeof(SaveManager).GetMethod("loadGameProfiles",
                    BindingFlags.Public | BindingFlags.Instance);
                if (load == null) return null;
                return ExtractProfilesList(load.Invoke(sm, null));
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                ModLog.Warn(LogCat.Save, "loadGameProfiles failed: " + inner.Message);
                return null;
            }
        }

        /// <summary>
        /// Private GetProfiles does File.ReadAllText(profs.dat) with no Exists check.
        /// Only call when the file is present (vanilla's intended path).
        /// </summary>
        private static List<GameProfile> TryLoadProfilesViaPrivateGet(SaveManager sm)
        {
            string profilesPath = null;
            try
            {
                profilesPath = Path.Combine(Application.persistentDataPath, "1_4Save", "profs.dat");
                if (!File.Exists(profilesPath))
                {
                    if (!_loggedMissingProfs)
                    {
                        _loggedMissingProfs = true;
                        ModLog.Event(LogCat.Save,
                            "No profs.dat yet under save root (empty dual-box client is normal) — "
                            + "slot picker will use file presence / Core.profiles");
                    }
                    return null;
                }

                var getProfiles = typeof(SaveManager).GetMethod("GetProfiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getProfiles == null) return null;
                return ExtractProfilesList(getProfiles.Invoke(sm, null));
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                // Once per session — OnGUI used to spam TargetInvocationException 1000+.
                if (!_loggedMissingProfs)
                {
                    _loggedMissingProfs = true;
                    ModLog.Warn(LogCat.Save,
                        "GetProfiles failed: " + inner.GetType().Name + ": " + inner.Message
                        + (profilesPath != null ? " path=" + profilesPath : ""));
                }
                return null;
            }
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
