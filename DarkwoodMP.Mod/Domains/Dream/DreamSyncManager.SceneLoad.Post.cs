using DG.Tweening;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Spectator;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Sync
{
    internal static partial class DreamSyncManager
    {

        /// <summary>
        /// Safety clear after dream setGrid (StartLoadDreamScene already clears loading).
        /// </summary>
        internal static void FinishDreamOutsideLoadFlags()
        {
            try
            {
                var outside = Singleton<OutsideLocations>.Instance;
                if (outside != null)
                    outside.loading = false;
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// UniqueObjects keeps the first registrant. Overworld bunker wins before the pad
        /// exists; leave-door / setActive GEs then mutate the wrong twin. Force-map pad
        /// UniqueObjects into the registry after spawn.
        /// </summary>
        internal static void RemapDreamUniqueObjects(Transform dreamRoot)
        {
            if (dreamRoot == null) return;
            try
            {
                var uo = Singleton<UniqueObjects>.Instance;
                if (uo == null || uo.objects == null) return;

                // Pad-scoped only — world FindObjectsOfType hitch + overworld twin risk.
                UniqueObject[] all = dreamRoot.GetComponentsInChildren<UniqueObject>(true);
                int remapped = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    UniqueObject u = all[i];
                    if (u == null || string.IsNullOrEmpty(u.type)) continue;

                    if (!uo.objects.TryGetValue(u.type, out UniqueObject cur) || cur != u)
                    {
                        uo.objects[u.type] = u;
                        remapped++;
                    }
                }
                if (remapped > 0)
                    ModRuntime.LegacyInfo(
                        "[DreamSync] Remapped " + remapped + " UniqueObject(s) onto dream pad");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] RemapDreamUniqueObjects: " + ex.Message);
            }
        }

        /// <summary>
        /// Mirror vanilla Dreams.onLocationSpawned epilogue branch for remote/host co-op entry.
        /// </summary>
        internal static void ApplyEpilogueModeIfNeeded(Location location, string locationName = null)
        {
            try
            {
                bool isEpilogue = (location != null && location.isEpilogueLocation)
                    || (!string.IsNullOrEmpty(locationName)
                        && locationName.IndexOf("epilog", System.StringComparison.OrdinalIgnoreCase) >= 0);

                if (!isEpilogue) return;
                if (Player.Instance == null || Player.Instance.inEpilogue) return;

                Player.Instance.inEpilogue = true;
                if (Singleton<UI>.Instance != null)
                    Singleton<UI>.Instance.hideVisibleUI();

                var cam = Singleton<CamMain>.Instance;
                if (cam != null && cam.FireMaskCam != null)
                    cam.FireMaskCam.gameObject.SetActive(true);

                // First-entry epilogue title (same delay spirit as OutsideLocations.onSpawnedLocation).
                if (Singleton<Controller>.Instance != null && Singleton<UI>.Instance != null)
                {
                    Singleton<Controller>.Instance.Invoke(
                        Singleton<UI>.Instance.showEpilogueText, 1f, timeScaleDependent: true);
                }

                ModRuntime.LegacyInfo($"[DreamSync] Epilogue mode applied (loc={locationName ?? location?.name})");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] ApplyEpilogueMode failed: {ex.Message}");
            }
        }

        private static void CleanupDreamScene(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return;

            try
            {
                Location dreamLoc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
                Transform dreamRoot = dreamLoc != null ? dreamLoc.transform : null;

                // Never remove a grid selected by name: overworld and dream
                // locations intentionally share keys in several save layouts.
                if (dreamRoot != null && Singleton<WorldGrid>.Instance != null)
                {
                    var grid = Patches.HostGridOccupancy.FindGridContaining(
                        Singleton<WorldGrid>.Instance, dreamRoot.position);
                    if (grid != null)
                        Singleton<WorldGrid>.Instance.grids.Remove(grid);
                }

                if (Singleton<OutsideLocations>.Instance != null &&
                    dreamLoc != null
                    && Singleton<OutsideLocations>.Instance.spawnedLocations.TryGetValue(
                        locationName, out Location mappedLoc)
                    && mappedLoc == dreamLoc)
                {
                    Singleton<OutsideLocations>.Instance.spawnedLocations.Remove(locationName);
                }

                // The Dreams component is the only authoritative owner of the
                // active pad. If it is already gone, leave teardown to vanilla
                // rather than risking destruction of an overworld twin.
                if (dreamLoc != null && dreamLoc.gameObject != null)
                {
                    UnityEngine.Object.Destroy(dreamLoc.gameObject, 2f);
                    if (Dreams.Instance != null)
                        Dreams.Instance.dreamLocation = null;
                }
                else
                {
                    ModRuntime.Log?.LogWarning(
                        "[DreamSync] cleanup skipped unscoped dream object '" + locationName + "'");
                }

                ModRuntime.LegacyInfo($"[DreamSync] Dream scene cleaned up: {locationName}");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Error during dream scene cleanup: {ex}");
            }
        }

        private static void ApplyDreamCameraEffects(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            try
            {
                GameObject presetGO = Resources.Load("DreamPresets/" + presetName) as GameObject;
                if (presetGO != null)
                {
                    Core.modifyCamEffects(active: true, presetGO);
                    ModRuntime.LegacyInfo($"[DreamSync] Applied camera effects for dream: {presetName}");
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to apply camera effects: {ex}");
            }
        }

        private static void RemoveDreamCameraEffects(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            try
            {
                GameObject presetGO = Resources.Load("DreamPresets/" + presetName) as GameObject;
                if (presetGO == null) return;
                // modifyCamEffects can NRE if CamMain/effects torn down mid-quit.
                if (Singleton<CamMain>.Instance == null) return;
                Core.modifyCamEffects(active: false, presetGO);
                ModRuntime.LegacyInfo($"[DreamSync] Removed camera effects for dream: {presetName}");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to remove camera effects: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts the dream transition (video/audio overlay) on the remote client.
        /// Returns the number of seconds to wait for the transition to finish,
        /// or 0 if the transition should be skipped (fallback to black screen).
        /// </summary>
    }
}
