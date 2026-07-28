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

                // H6: No host pick yet — do not return null into a live prepareDream("").
                // Leave name empty and let Prefix on prepare abort; never hand vanilla a null preset.
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
    /// Empty name is handled after getPreset (DreamGetPresetPatch) — prefix only for named.
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

            // H6: Client must not run prepareDream("") without a host pick (null getPreset → NRE).
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
                // Duplicate prepare while already Starting same preset — harmless, continue vanilla.
                if (DreamSession.IsStarting
                    && string.Equals(DreamSession.PresetName, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    DreamSession.MirrorPoolRemove(name);
                    return true;
                }

                ModRuntime.LegacyInfo(
                    $"[DreamSync] Host prepareDream aborted — TryBegin rejected '{name}'"
                    + $" (session {DreamSession.Current})");
                return false;
            }

            DreamSession.MirrorPoolRemove(name); // named prepare never touches presetList
            return true;
        }
    }

    /// <summary>
    /// Prefix on Dreams.startDreaming: blocks completed dreams, routes client starts to host,
    /// and registers a shared DreamSession so all peers enter together.
    /// Harmony still runs Postfix when Prefix returns false — __state skips false local start.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "startDreaming")]
    public static class DreamStartPatch
    {
        private static bool Prefix(Dreams __instance, ref bool __state)
        {
            // true = Postfix must not call OnLocalDreamStarted (blocked or remote-applied).
            __state = false;

            if (__instance.preset == null || string.IsNullOrEmpty(__instance.preset.name))
                return true;

            string preset = __instance.preset.name;

            if (ModRuntime.Network != null && ModRuntime.Network.IsConnected)
            {
                // H4: Completions only drive MirrorPoolRemove — named dreams may re-enter like SP.

                if (LanNetworkManager.IsApplyingRemoteState)
                {
                    // Remote load path: vanilla startDreaming runs; Postfix only MarkActive.
                    __state = true;
                    return true;
                }

                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.Role == NetworkRole.Client)
                {
                    // Fix 2: If onFinishedVideo prefix already sent the request (entry transition
                    // path), skip re-sending here — the dialogue-direct path still sends normally.
                    if (DreamSyncManager.EntryTransitionPlayedLocally)
                    {
                        ModRuntime.LegacyInfo(
                            "[DreamSync] Client entry transition already handled — skip re-request");
                        __state = true;
                        return false;
                    }

                    // Host owns begin: request only. Freeze world until DreamStarted remote path.
                    ModRuntime.LegacyInfo($"[DreamSync] Client-initiated dream — requesting host to start: {preset}");
                    net.Send(NetMessageType.DreamStartRequest, w => new DreamStartRequestMessage
                    {
                        PresetName = preset,
                        RequestId = (int)(Time.realtimeSinceStartup * 1000f),
                        LvlFlags = DreamSession.ReadLocalLvlFlags()
                    }.Serialize(w), DeliveryMethod.ReliableOrdered);
                    // Local empty roll already consumed pool; keep aligned with host named prepare.
                    DreamSession.MirrorPoolRemove(preset);
                    DreamSession.SetPendingHostPreset(preset);
                    DreamSyncManager.FreezeWorld();
                    ModRuntime.LegacyInfo("[DreamSync] Client waiting for host DreamStarted");
                    __state = true;
                    return false;
                }

                if (net != null && net.Role == NetworkRole.Host)
                {
                    // prepareDream already TryBegin; ensure session if host started without prepare patch path.
                    if (!DreamSession.IsActive && !DreamSession.TryBegin(preset))
                    {
                        __state = true;
                        return false;
                    }
                }
            }

            return true;
        }

        private static void Postfix(Dreams __instance, bool __state)
        {
            if (__state)
            {
                // Remote-applied start: mark session active only (no host broadcast from client).
                if (LanNetworkManager.IsApplyingRemoteState)
                    DreamSession.MarkActive();
                return;
            }

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

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

            // H1/H2: Chain broadcast lives only in DreamPrepareChainPatch (prepareDream).
            // transferToDream / wantToSwitchDream both hit prepareDream — do not dual-fire here.
            if (__instance.switchingDream || OutcomeHasTransferToDream(__instance))
            {
                string next = FindTransferDestPreset(__instance);
                if (!string.IsNullOrEmpty(next) && DreamSession.IsActive)
                    DreamSession.SetChainedPreset(next);
                ModRuntime.LegacyInfo(
                    "[DreamSync] endDreaming with chain — session stays active; ChainStart via prepare");
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
    /// H1: wantToSwitchDream skips endDreaming — ensure host session tracks next pocket
    /// before prepareDream (DreamPrepareChainPatch still owns the DreamChainStart wire).
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "wantToSwitchDream")]
    public static class DreamWantToSwitchPatch
    {
        private static void Postfix(Dreams __instance, bool __result)
        {
            if (!__result) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!DreamSession.IsActive)
                return;

            string next = null;
            try
            {
                var traverse = Traverse.Create(__instance);
                var outcomePreset = traverse.Field("outcomePreset").GetValue<DreamPreset.Outcome>();
                if (outcomePreset?.effects == null) return;
                for (int i = 0; i < outcomePreset.effects.Count; i++)
                {
                    var e = outcomePreset.effects[i];
                    if (e == null || e.type != DreamPreset.Outcome.Effect.Type.transferToDream)
                        continue;
                    if (e.destPrefab == null) continue;
                    var go = e.destPrefab as GameObject;
                    if (go != null && !string.IsNullOrEmpty(go.name))
                    {
                        next = go.name;
                        break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] wantToSwitchDream postfix: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(next)) return;
            DreamSession.SetChainedPreset(next);
            ModRuntime.LegacyInfo("[DreamSync] wantToSwitchDream → chained " + next);
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

            // H3: host all-dead teardown — one-shot allow vanilla initiateEndDreaming.
            if (FinalDreamsceneManager.AllowDeathEndPass)
            {
                FinalDreamsceneManager.AllowDeathEndPass = false;
                ModRuntime.LegacyInfo("[DreamDeath] AllowDeathEndPass — vanilla initiateEndDreaming");
                return true;
            }

            // Death: never end the shared session alone — spectate until all dead / story end.
            if (outcome == "playerDeath")
            {
                if (Player.Instance != null && Player.Instance.inEpilogue)
                {
                    ModRuntime.LegacyInfo("[DreamDeath] Epilogue playerDeath — allowing vanilla end path");
                    return true;
                }

                if (!FinalDreamsceneManager.IsActive)
                    FinalDreamsceneManager.OnDreamStarted();

                // C2: solo / no remotes → allow vanilla (never block then no-op).
                if (!FinalDreamsceneManager.HasRemoteParticipants())
                {
                    ModRuntime.LegacyInfo(
                        "[DreamDeath] Solo/empty-peer dream death — allowing vanilla initiateEndDreaming");
                    return true;
                }

                ModRuntime.LegacyInfo("[DreamDeath] Player died in dream — redirecting to spectator");
                FinalDreamsceneManager.OnLocalDeathInDream();
                return false;
            }

            // Client story end: host owns teardown (including outcome transition).
            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                if (DreamSyncManager.IsStoryEndDeferPending)
                {
                    ModRuntime.LegacyInfo(
                        $"[DreamSession] Client story end '{outcome}' — defer already pending");
                    return false;
                }
                ModRuntime.LegacyInfo($"[DreamSession] Client story end '{outcome}' — deferring to host");
                var net = ModRuntime.Network as LanNetworkManager;
                net?.Send(NetMessageType.DreamEnded,
                    w => DreamEndedMessage.Build(
                        __instance.preset != null ? __instance.preset.name : "",
                        outcome).Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                DreamSyncManager.BeginStoryEndDefer();
                return false;
            }

            // Host story end: allow vanilla initiateEndDreaming → transition → endDreaming
            return true;
        }
    }
}
