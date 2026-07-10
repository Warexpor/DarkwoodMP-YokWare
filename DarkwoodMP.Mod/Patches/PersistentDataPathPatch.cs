using System;
using System.IO;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Dual-box fix (M8): Steam + SecondDarkwood both resolve Unity
    /// <see cref="Application.persistentDataPath"/> to the same LocalLow folder.
    /// Redirect second install (or explicit config) so saves/profiles do not share one tree.
    /// </summary>
    [HarmonyPatch(typeof(Application), "get_persistentDataPath")]
    public static class PersistentDataPathPatch
    {
        private static string _cached;
        private static bool _logged;

        private static void Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result))
                return;

            string resolved = Resolve(__result);
            if (resolved == __result)
                return;

            __result = resolved;
            if (!_logged)
            {
                _logged = true;
                try
                {
                    Directory.CreateDirectory(__result);
                    ModLog.Event(LogCat.Save,
                        "Save root override active: " + __result
                        + " (dual-box isolation / SaveRootOverride)");
                }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Save, "Save root create failed: " + ex.Message);
                }
            }
        }

        /// <summary>Resolve effective persistent root from Unity default.</summary>
        public static string Resolve(string unityDefault)
        {
            if (!string.IsNullOrEmpty(_cached))
                return _cached;

            try
            {
                string cfg = ModConfig.SaveRootOverride != null
                    ? (ModConfig.SaveRootOverride.Value ?? "").Trim()
                    : "";
                if (!string.IsNullOrEmpty(cfg))
                {
                    _cached = Path.GetFullPath(cfg);
                    return _cached;
                }

                // Auto: SecondDarkwood install path → sibling LocalLow product folder.
                string dataPath = Application.dataPath ?? "";
                if (dataPath.IndexOf("SecondDarkwood", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // unityDefault = .../Acid Wizard Studio/Darkwood
                    string parent = Path.GetDirectoryName(unityDefault);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        _cached = Path.Combine(parent, "Darkwood_Second");
                        return _cached;
                    }
                }
            }
            catch
            {
                // fall through to unity default
            }

            _cached = unityDefault;
            return _cached;
        }

        /// <summary>Tests / config change after bind — clear cache once at startup only.</summary>
        public static void ResetCache()
        {
            _cached = null;
            _logged = false;
        }
    }
}
