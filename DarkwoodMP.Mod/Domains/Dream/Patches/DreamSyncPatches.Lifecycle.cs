using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Prefix on Dreams.startDreaming: blocks completed dreams, routes client starts to host,
    /// and registers a shared DreamSession so all peers enter together.
    /// Harmony still runs Postfix when Prefix returns false; __state skips the local start.
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
                // Party-once: host must not start a preset the session already finished.
                if (DreamSession.IsPresetCompleted(preset)
                    && !LanNetworkManager.IsApplyingRemoteState)
                {
                    ModRuntime.LegacyInfo(
                        "[DreamSync] Block startDreaming — party already completed: " + preset);
                    __state = true;
                    return false;
                }

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
                    // path), skip re-sending here. The dialogue-direct path still sends normally.
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
                        LvlFlags = DreamSession.ReadUnionLvlFlags()
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
            if (__instance.dreamLocation != null)
                DreamSyncManager.RemapDreamUniqueObjects(__instance.dreamLocation.transform);
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

            // Must run before OnLocalDreamEnded (clears IsLocalDead). Exit video already played
            // from the story outcome; only effect grants are downgraded. Inventory restore stays.
            DowngradeSuccessRewardsIfDeadInDream(__instance);

            // DreamPrepareChainPatch owns chain broadcasts. Both
            // transferToDream and wantToSwitchDream reach prepareDream.
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

        /// <summary>
        /// Safety: if positionCopy was corrupted to pad coords, vanilla teleport leaves
        /// the peer in the abyss. Snap to pre-dream overworld after endDreaming body runs.
        /// </summary>
        private static void Postfix(Dreams __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (__instance != null && __instance.dreaming)
                return; // chained transfer still dreaming

            Player player = Player.Instance;
            if (player == null) return;
            Vector3 live = player._transform.position;
            if (!ClientStateBackup.IsDreamPadCoordinate(live))
                return;

            Vector3 dest = Vector3.zero;
            if (__instance != null
                && __instance.positionCopy.sqrMagnitude > 0.01f
                && !ClientStateBackup.IsDreamPadCoordinate(__instance.positionCopy))
                dest = __instance.positionCopy;
            else if (!DreamSyncManager.TryGetPreDreamOverworldPosition(out dest))
                return;

            try
            {
                player.teleportTo(dest, Quaternion.Euler(90f, 0f, 0f));
                if (Singleton<WorldGrid>.Instance != null)
                    Singleton<WorldGrid>.Instance.refreshPosition(dest, instant: true, force: true);
                ModRuntime.LegacyInfo(
                    "[DreamSync] post-endDreaming snap off pad → " + dest);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning(
                    "[DreamSync] post-endDreaming pad snap failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Dead spectating peer still sees the shared exit video, but must not receive
        /// success createInvItem / journal grants. Swap to playerDeath effects (or none).
        /// </summary>
        private static void DowngradeSuccessRewardsIfDeadInDream(Dreams dreams)
        {
            if (!FinalDreamsceneManager.IsLocalDead) return;
            string outcome = dreams.outcome ?? "";
            if (string.IsNullOrEmpty(outcome) || outcome == "playerDeath")
                return;

            DreamPreset.Outcome deathOc = null;
            if (dreams.preset?.outcomes != null)
            {
                for (int i = 0; i < dreams.preset.outcomes.Count; i++)
                {
                    var oc = dreams.preset.outcomes[i];
                    if (oc != null && oc.name == "playerDeath")
                    {
                        deathOc = oc;
                        break;
                    }
                }
            }

            dreams.outcome = "playerDeath";
            Traverse.Create(dreams).Field("outcomePreset").SetValue(deathOc);
            ModRuntime.LegacyInfo(
                "[DreamDeath] Local dead at story end — inventory restore only (no success rewards)");
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
}
