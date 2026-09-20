using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    [HarmonyPatch(typeof(Dreams), "getPreset")]
    public static class DreamGetPresetPatch
    {
        private const int StateNone = 0;
        private const int StateHostRolled = 1;
        private const int StateClientAdopted = 2;

        private static bool Prefix(ref string presetName, ref DreamPreset __result, ref int __state)
        {
            __state = StateNone;
            try
            {
                if (!string.IsNullOrEmpty(presetName)) return true;
                if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return true;
                if (LanNetworkManager.IsApplyingRemoteState) return true;

                var net = ModRuntime.Network as LanNetworkManager;
                if (net == null) return true;

                if (net.Role == NetworkRole.Host)
                {
                    // Depleted save pool → empty Random.Range throw → stuck black void.
                    EnsureHostRandomPoolHasEligible(Dreams.Instance);
                    // Let vanilla roll; postfix broadcasts resolved name + TryBegin.
                    __state = StateHostRolled;
                    return true;
                }

                // Client: prefer host pick (pending or active session) over local RNG.
                string hostPick = null;
                if (DreamSession.TryGetPendingHostPreset(out var pending))
                    hostPick = pending;
                else if (DreamSession.IsActive && !string.IsNullOrEmpty(DreamSession.PresetName))
                    hostPick = DreamSession.PresetName;

                if (!string.IsNullOrEmpty(hostPick))
                {
                    presetName = hostPick;
                    __state = StateClientAdopted;
                    ModRuntime.LegacyInfo(
                        $"[DreamSync] Client getPreset adopts host pick '{hostPick}' (no local roll)");
                    return true;
                }

                // Do not return null into a live prepareDream(""). Leave the
                // name empty so the prepare prefix can wait for the host.
                ModRuntime.LegacyInfo(
                    "[DreamSync] Client getPreset — no PendingHostPreset; skip roll (wait DreamStarted)");
                return false;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] getPreset prefix: " + ex.Message);
                return true;
            }
        }

        /// <summary>
        /// When saved presetList has no eligible random dreams left, refill from
        /// allPresets so prepareDream("") cannot IndexOutOfRange and hang the entry.
        /// </summary>
        private static void EnsureHostRandomPoolHasEligible(Dreams dreams)
        {
            if (dreams?.presetList == null || dreams.allPresets == null) return;
            if (CountEligibleRandom(dreams) > 0) return;

            int added = 0;
            for (int i = 0; i < dreams.allPresets.Count; i++)
            {
                DreamPreset p = dreams.allPresets[i];
                if (p == null || !p.isRandomDream) continue;
                if (dreams.presetList.Contains(p)) continue;
                string n = DreamSession.ResolvePresetName(p);
                // Party-once: never refill a preset the party already finished.
                if (DreamSession.IsPresetCompleted(n)) continue;
                dreams.presetList.Add(p);
                added++;
            }
            if (added > 0)
            {
                ModRuntime.LegacyInfo(
                    "[DreamSync] Refilled random dream pool (+" + added
                    + ") — save had depleted presetList");
            }
        }

        private static int CountEligibleRandom(Dreams dreams)
        {
            int n = 0;
            int chapter = Singleton<WorldGenerator>.Instance != null
                ? Singleton<WorldGenerator>.Instance.chapterID
                : 1;
            for (int i = 0; i < dreams.presetList.Count; i++)
            {
                DreamPreset p = dreams.presetList[i];
                if (p == null || !p.isRandomDream) continue;
                if (DreamSession.IsPresetCompleted(DreamSession.ResolvePresetName(p)))
                    continue;
                if (chapter == 1 && p.chapter1) n++;
                else if (chapter == 2 && p.chapter2) n++;
            }
            return n;
        }

        private static void Postfix(Dreams __instance, DreamPreset __result, int __state)
        {
            try
            {
                if (__state == StateNone || __result == null) return;
                string resolved = DreamSession.ResolvePresetName(__result);
                if (string.IsNullOrEmpty(resolved)) return;

                if (__state == StateHostRolled)
                {
                    DreamSession.SetPendingHostPreset(resolved);
                    if (!DreamSession.IsActive)
                    {
                        if (!DreamSession.TryBegin(resolved))
                        {
                            // Party-once / session race; do not continue into a completed roll.
                            ModRuntime.LegacyInfo(
                                "[DreamSync] Host random roll rejected TryBegin: " + resolved);
                            try
                            {
                                if (__instance != null)
                                    __instance.dreamPrepared = false;
                            }
                            catch { /* ignore */ }
                            return;
                        }
                    }
                    else
                        // prepareDream("") may have left session on a stale previous preset.
                        DreamSession.UpdateActivePreset(resolved);
                    // Vanilla empty path already removed from presetList.

                    var net = LanNetworkManager.Instance;
                    if (net != null && net.IsConnected && net.Role == NetworkRole.Host)
                    {
                        // Early resolve so clients that enter getPreset mid-prepare adopt same pick.
                        var bulk = DreamSessionBulkMessage.FromLocal();
                        net.Broadcast(NetMessageType.DreamSessionBulk,
                            w => bulk.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                        ModRuntime.LegacyInfo(
                            $"[DreamSync] Host rolled random dream '{resolved}' — early bulk");
                    }
                }
                else if (__state == StateClientAdopted)
                {
                    // Dict path does not remove; mirror one-shot pool.
                    DreamSession.MirrorPoolRemove(resolved);
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] getPreset postfix: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Host: TryBegin session as soon as prepareDream starts (closes double-prepare race).
    /// Empty name is handled after getPreset (DreamGetPresetPatch); this prefix handles named presets.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "prepareDream")]
    public static class DreamPreparePatch
    {
        private static bool Prefix(Dreams __instance, string presetName)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState)
                return true;

            // A client must not run prepareDream("") without a host-selected preset.
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                if (string.IsNullOrEmpty(presetName)
                    && !DreamSession.TryGetPendingHostPreset(out _)
                    && !(DreamSession.IsActive && !string.IsNullOrEmpty(DreamSession.PresetName)))
                {
                    ModRuntime.LegacyInfo(
                        "[DreamSync] Client prepareDream('') aborted — waiting host DreamStarted/bulk");
                    return false;
                }
                return true;
            }

            if (ModRuntime.Network.Role != NetworkRole.Host)
                return true;

            // Empty prepareDream("") must NOT TryBegin from stale Dreams.preset (previous dream).
            // DreamGetPresetPatch postfix begins after the host roll resolves.
            if (string.IsNullOrEmpty(presetName))
                return true;

            string name = presetName;
            if (!DreamSession.TryBegin(name))
            {
                // Duplicate prepare while already Starting the same preset is harmless; continue vanilla.
                if (DreamSession.IsStarting
                    && string.Equals(DreamSession.PresetName, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    DreamSession.MirrorPoolRemove(name);
                    return true;
                }

                // Party-once / session busy; clear sticky prepare flags.
                try
                {
                    __instance.wantToDream = false;
                    __instance.dreamPrepared = false;
                }
                catch { /* ignore */ }

                ModRuntime.LegacyInfo(
                    $"[DreamSync] Host prepareDream aborted — TryBegin rejected '{name}'"
                    + $" (session {DreamSession.Current})");
                return false;
            }

            DreamSession.MirrorPoolRemove(name); // named prepare never touches presetList
            return true;
        }
    }
}
