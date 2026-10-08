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
        private static bool _swapRecoveryDone; // process-scoped: once per process, before any profile read

        private static void Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result))
                return;

            string resolved = Resolve(__result);
            // First access after patching (before the game reads any profile): finish save swaps a
            // crash interrupted. Runs once, on the effective root, before anything else uses it.
            if (!_swapRecoveryDone)
            {
                _swapRecoveryDone = true;
                Networking.WorldSaveShareService.RecoverInterruptedSlotSwaps(resolved);
                Networking.WorldSaveGuards.EnsureQuitHook();
            }
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

                // Auto: a SecondDarkwood / ThirdDarkwood install path → sibling LocalLow product
                // folder (Darkwood_Second / Darkwood_Third).
                string dataPath = Application.dataPath ?? "";
                string suffix = dataPath.IndexOf("SecondDarkwood", StringComparison.OrdinalIgnoreCase) >= 0 ? "Second"
                    : dataPath.IndexOf("ThirdDarkwood", StringComparison.OrdinalIgnoreCase) >= 0 ? "Third"
                    : null;
                if (suffix != null)
                {
                    // unityDefault = .../Acid Wizard Studio/Darkwood
                    string parent = Path.GetDirectoryName(unityDefault);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        _cached = Path.Combine(parent, "Darkwood_" + suffix);
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
