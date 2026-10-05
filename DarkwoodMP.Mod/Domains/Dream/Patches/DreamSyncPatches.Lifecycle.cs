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
                        $"[DreamSync] Block startDreaming — party already completed: {preset}");
                    __state = true;
                    return false;
                }

                if (LanNetworkManager.IsApplyingRemoteState)
                {
                    // Remote load path: vanilla startDreaming runs; Postfix only MarkActive.
                    __state = true;
                    return true;
                }

                var net = ModRuntime.Network;
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
        private static void Prefix(Dreams __instance, ref bool __state)
        {
            // true = this call ran the local end path; Postfix drops the pre-dream pose.
            __state = false;

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            if (!__instance.dreaming)
                return;

            // Reads WasLocalDeadThisDream (DreamEnded receipt already cleared IsLocalDead). Exit video already played
            // from the story outcome; only effect grants are downgraded. Inventory restore stays.
            DowngradeSuccessRewardsIfDeadInDream(__instance);

            // DreamPrepareChainPatch owns chain broadcasts. Both
            // transferToDream and wantToSwitchDream reach prepareDream.
            if (__instance.switchingDream || OutcomeHasTransferToDream(__instance))
            {
                string next = FindTransferDestPreset(__instance);
                if (!string.IsNullOrEmpty(next) && DreamSession.IsActive)
                    DreamSession.SetChainedPreset(next);
                // The exit transition for this pocket is over: a host-ordered flag left set here
                // made the client end pocket 2 by itself (initiateEndDreaming authority bypass).
                DreamSyncManager.ClearHostOrderedDreamEnd();
                ModRuntime.LegacyInfo(
                    "[DreamSync] endDreaming with chain — session stays active; ChainStart via prepare");
                return;
            }

            string outcome = __instance.outcome ?? "";
            if (DreamSession.IsActive)
                DreamSession.End(outcome);
            DreamSyncManager.OnLocalDreamEnded();
            __state = true;
        }

        /// <summary>
        /// Safety: if positionCopy was corrupted to pad coords, vanilla teleport leaves
        /// the peer in the abyss. Snap to pre-dream overworld after endDreaming body runs.
        /// The pre-dream pose is dropped afterwards: left behind it teleported the client to the
        /// last dream start on any later disconnect and fed ClientStateBackup.
        /// </summary>
        private static void Postfix(Dreams __instance, bool __state)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (__instance != null && __instance.dreaming)
                return; // chained transfer still dreaming
            // Back in the overworld whichever path ran endDreaming.
            if (__instance != null && !__instance.switchingDream)
                FinalDreamsceneManager.OnLocalWokeUp();
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            try
            {
                SnapOffPadIfStranded(__instance);
            }
            finally
            {
                // Chain keeps the pose for the final exit (switchingDream / session still live).
                if (__state && (__instance == null || !__instance.switchingDream)
                    && !DreamSession.IsActive)
                    DreamSyncManager.ClearPreDreamState();
            }
        }

        private static void SnapOffPadIfStranded(Dreams __instance)
        {
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
                    $"[DreamSync] post-endDreaming snap off pad → {dest}");
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
            if (!FinalDreamsceneManager.WasLocalDeadThisDream) return;
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

            if (deathOc == null)
            {
                // A preset with no death outcome: vanilla endDreaming dereferences outcomePreset
                // (dontLieDown) with no null check, and a null threw after the pad was gone,
                // skipping the wake-up (no heal, inputs left locked). Wake like the party does,
                // without its rewards.
                var success = Traverse.Create(dreams).Field("outcomePreset").GetValue<DreamPreset.Outcome>();
                deathOc = new DreamPreset.Outcome
                {
                    name = "playerDeath",
                    transition = success?.transition,
                    customEndTime = success != null && success.customEndTime,
                    endTime = success != null ? success.endTime : 0,
                    dontLieDown = success != null && success.dontLieDown
                };
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

    /// <summary>
    /// Client, chained dream: the host's DreamChainStart loads the next pocket
    /// (DreamSyncManager.OnDreamChain). Vanilla wantToSwitchDream at the end of the exit video
    /// would also destroy the current pocket and prepare the next one itself: a second pad,
    /// a local Save, a bogus start request, and, when the host's pocket loaded first, the new
    /// pocket destroyed under the player. The client keeps only vanilla's player reset between
    /// pockets, and only while the host's pocket has not started loading.
    /// </summary>
    [HarmonyPatch(typeof(Dreams), "wantToSwitchDream")]
    public static class ClientDreamSwitchPatch
    {
        private static bool Prefix(Dreams __instance, ref bool __result)
        {
            if (!NetGuard.Connected(out var net) || net.Role != NetworkRole.Client)
                return true;
            if (!DreamSession.IsActive)
                return true;
            var outcome = Traverse.Create(__instance).Field("outcomePreset").GetValue<DreamPreset.Outcome>();
            string dest = null;
            if (outcome?.effects != null)
            {
                for (int i = 0; i < outcome.effects.Count; i++)
                {
                    var e = outcome.effects[i];
                    if (e != null && e.type == DreamPreset.Outcome.Effect.Type.transferToDream && e.destPrefab != null)
                    {
                        dest = e.destPrefab.name;
                        break;
                    }
                }
            }
            if (dest == null)
                return true; // no transfer: vanilla returns false and endDreaming follows

            __result = true;
            __instance.switchingDream = true;
            __instance.wantToDream = true;
            if (string.Equals(DreamSyncManager.ChainPocketLoading, dest, System.StringComparison.OrdinalIgnoreCase))
            {
                ModRuntime.LegacyInfo("[DreamSync] Client chain switch — host pocket already loading: " + dest);
                return false;
            }
            Player.Instance?.endDreaming(outcome.dontLieDown);
            Player.Instance?.Hotbar.clear();
            Player.Instance?.Hotbar.refresh();
            ModRuntime.LegacyInfo("[DreamSync] Client chain switch — waiting for host pocket: " + dest);
            return false;
        }
    }
}
