using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Decision point for random dreams: vanilla prepareDream("") rolls inside getPreset
    /// and removes the pick from presetList. Broadcast the RESOLVED name (not "") and
    /// mirror pool consumption on remotes so future rolls stay aligned.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "getPreset")]
    public static class DreamGetPresetPatch
    {
        private const int StateNone = 0;
        private const int StateHostRolled = 1;
        private const int StateClientAdopted = 2;

        private static void Prefix(ref string presetName, ref int __state)
        {
            __state = StateNone;
            try
            {
                if (!string.IsNullOrEmpty(presetName)) return;
                if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
                if (LanNetworkManager.IsApplyingRemoteState) return;

                var net = ModRuntime.Network as LanNetworkManager;
                if (net == null) return;

                if (net.Role == NetworkRole.Host)
                {
                    // Let vanilla roll; postfix broadcasts resolved name + TryBegin.
                    __state = StateHostRolled;
                    return;
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
                }
                // No pending: local roll stands; startDreaming → DreamStartRequest carries resolved name.
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] getPreset prefix: " + ex.Message);
            }
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
                        DreamSession.TryBegin(resolved);
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
    /// Empty name is handled after getPreset (DreamGetPresetPatch) — prefix only for named.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "prepareDream")]
    public static class DreamPreparePatch
    {
        private static void Prefix(Dreams __instance, string presetName)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (ModRuntime.Network.Role != NetworkRole.Host)
                return;

            string name = presetName;
            if (string.IsNullOrEmpty(name) && __instance.preset != null)
                name = DreamSession.ResolvePresetName(__instance.preset);
            if (string.IsNullOrEmpty(name))
                return; // empty: DreamGetPresetPatch TryBegin after roll

            DreamSession.TryBegin(name);
            DreamSession.MirrorPoolRemove(name); // named prepare never touches presetList
        }
    }

    /// <summary>
    /// Prefix on Dreams.startDreaming: blocks completed dreams, routes client starts to host,
    /// and registers a shared DreamSession so all peers enter together.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "startDreaming")]
    public static class DreamStartPatch
    {
        private static bool Prefix(Dreams __instance)
        {
            if (__instance.preset == null || string.IsNullOrEmpty(__instance.preset.name))
                return true;

            string preset = __instance.preset.name;

            if (ModRuntime.Network != null && ModRuntime.Network.IsConnected)
            {
                if (DreamSession.IsPresetCompleted(preset)
                    && !DreamSession.IsActive)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Blocked re-entry of completed dream: {preset}");
                    return false;
                }

                if (LanNetworkManager.IsApplyingRemoteState)
                    return true; // remote/session-driven enter

                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.Role == NetworkRole.Client)
                {
                    // Include local hadDreamAtLvl* so host union-flags before prepare (client leveled).
                    ModRuntime.LegacyInfo($"[DreamSync] Client-initiated dream — requesting host to start: {preset}");
                    net.Send(NetMessageType.DreamStartRequest, w => new DreamStartRequestMessage
                    {
                        PresetName = preset,
                        RequestId = (int)(Time.realtimeSinceStartup * 1000f),
                        LvlFlags = DreamSession.ReadLocalLvlFlags()
                    }.Serialize(w), DeliveryMethod.ReliableOrdered);
                    // Local empty roll already consumed pool; keep aligned with host named prepare.
                    DreamSession.MirrorPoolRemove(preset);
                    return false;
                }

                if (net != null && net.Role == NetworkRole.Host)
                {
                    // prepareDream already TryBegin; ensure session if host started without prepare patch path.
                    if (!DreamSession.IsActive && !DreamSession.TryBegin(preset))
                        return false;
                }
            }

            return true;
        }

        private static void Postfix(Dreams __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
            {
                DreamSession.MarkActive();
                return;
            }

            if (__instance.preset == null || string.IsNullOrEmpty(__instance.preset.name))
                return;

            Vector3 locPos = Vector3.zero;
            if (__instance.dreamLocation != null)
                locPos = __instance.dreamLocation.transform.position;

            DreamSyncManager.OnLocalDreamStarted(__instance.preset.name, locPos);
            DreamSession.MarkActive();
        }
    }

    /// <summary>Prefix on endDreaming: ends shared session then notifies manager.</summary>
    [HarmonyPatch(typeof(Dreams), "endDreaming")]
    public static class DreamEndPatch
    {
        private static void Prefix(Dreams __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            if (!__instance.dreaming)
                return;

            // transferToDream sets switchingDream mid-endDreaming (after this prefix) OR
            // before via wantToSwitchDream. Detect outcome effects so we don't End→Idle first.
            if (__instance.switchingDream || OutcomeHasTransferToDream(__instance))
            {
                string next = FindTransferDestPreset(__instance);
                if (!string.IsNullOrEmpty(next)
                    && ModRuntime.Network.Role == NetworkRole.Host
                    && DreamSession.IsActive)
                {
                    DreamSession.SetChainedPreset(next);
                    var net = LanNetworkManager.Instance;
                    net?.Broadcast(NetMessageType.DreamChainStart,
                        w => new DreamChainStartMessage
                        {
                            NextPresetName = next,
                            SessionId = DreamSession.SessionId
                        }.Serialize(w),
                        DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo("[DreamSync] endDreaming transfer → DreamChainStart " + next);
                }
                else
                {
                    ModRuntime.LegacyInfo("[DreamSync] endDreaming with chain — session stays active");
                }
                return;
            }

            string outcome = __instance.outcome ?? "";
            if (DreamSession.IsActive)
                DreamSession.End(outcome);
            DreamSyncManager.OnLocalDreamEnded();
        }

        private static bool OutcomeHasTransferToDream(Dreams dreams)
        {
            return !string.IsNullOrEmpty(FindTransferDestPreset(dreams));
        }

        private static string FindTransferDestPreset(Dreams dreams)
        {
            if (dreams?.preset?.outcomes == null) return null;
            DreamPreset.Outcome match = null;
            string want = dreams.outcome ?? "";
            for (int i = 0; i < dreams.preset.outcomes.Count; i++)
            {
                var oc = dreams.preset.outcomes[i];
                if (oc != null && oc.name == want)
                {
                    match = oc;
                    break;
                }
            }
            if (match == null)
            {
                for (int i = 0; i < dreams.preset.outcomes.Count; i++)
                {
                    var oc = dreams.preset.outcomes[i];
                    if (oc != null && oc.name == "default")
                    {
                        match = oc;
                        break;
                    }
                }
            }
            if (match?.effects == null) return null;
            for (int i = 0; i < match.effects.Count; i++)
            {
                var e = match.effects[i];
                if (e == null || e.type != DreamPreset.Outcome.Effect.Type.transferToDream)
                    continue;
                if (e.destPrefab == null) continue;
                var go = e.destPrefab as GameObject;
                if (go != null && !string.IsNullOrEmpty(go.name))
                    return go.name;
            }
            return null;
        }
    }

    /// <summary>
    /// Host: when prepareDream is called with switchingDream / chain, notify peers of next pocket.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "prepareDream")]
    public static class DreamPrepareChainPatch
    {
        private static void Prefix(Dreams __instance, string presetName)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!__instance.switchingDream && !DreamSession.IsActive)
                return;

            string name = presetName;
            if (string.IsNullOrEmpty(name))
                return;

            // Only broadcast chain when already in a dream session and preparing a new pocket.
            if (!DreamSession.IsActive || string.IsNullOrEmpty(DreamSession.PresetName))
                return;
            if (string.Equals(DreamSession.PresetName, name, System.StringComparison.OrdinalIgnoreCase)
                && DreamSession.IsStarting)
                return;

            if (__instance.switchingDream || DreamSession.IsActive)
            {
                DreamSession.SetChainedPreset(name);
                var net = LanNetworkManager.Instance;
                net?.Broadcast(NetMessageType.DreamChainStart,
                    w => new DreamChainStartMessage
                    {
                        NextPresetName = name,
                        SessionId = DreamSession.SessionId
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModRuntime.LegacyInfo("[DreamSync] Host DreamChainStart → " + name);
            }
        }
    }

    /// <summary>
    /// Single Prefix for Dreams.initiateEndDreaming (merged death + client-authority logic).
    /// Branch order: offline → applying remote → not in session → death spectate →
    /// client story defer to host → host/vanilla continues.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "initiateEndDreaming")]
    public static class DreamEndDreamingAuthorityPatch
    {
        private static bool Prefix(Dreams __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            if (LanNetworkManager.IsApplyingRemoteState)
                return true;

            if (!DreamSession.IsActive && !DreamSyncManager.IsDreamActive)
                return true;

            string outcome = __instance.outcome ?? "";

            // Death: never end the shared session alone — spectate until all dead / story end.
            if (outcome == "playerDeath")
            {
                if (Player.Instance != null && Player.Instance.inEpilogue)
                {
                    ModRuntime.LegacyInfo("[DreamDeath] Epilogue playerDeath — allowing vanilla end path");
                    return true;
                }
                ModRuntime.LegacyInfo("[DreamDeath] Player died in dream — redirecting to spectator");
                if (!FinalDreamsceneManager.IsActive)
                    FinalDreamsceneManager.OnDreamStarted();
                FinalDreamsceneManager.OnLocalDeathInDream();
                return false;
            }

            // Client story end: host owns teardown (including outcome transition).
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                ModRuntime.LegacyInfo($"[DreamSession] Client story end '{outcome}' — deferring to host");
                var net = ModRuntime.Network as LanNetworkManager;
                net?.Send(NetMessageType.DreamEnded,
                    w => DreamEndedMessage.Build(
                        __instance.preset != null ? __instance.preset.name : "",
                        outcome).Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                return false;
            }

            // Host story end: allow vanilla initiateEndDreaming → transition → endDreaming
            return true;
        }
    }
}
