using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using LiteNetLib;
using Pathfinding;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A player's own way out of a party dream. An exit a player sets off with its own body (it
    /// walks into the exit, it uses the bed) wakes that player alone; the dream goes on for the
    /// others until each of them leaves. It used to end for everyone: the host lying down in the
    /// bunker dream's bed woke a client that was still playing it.
    ///
    /// What stays for everyone, as before: an ending no other player can set off again (a creature
    /// killed, a scripted scene, a dialogue), a dream that hands over to its next part (the
    /// doctor's), the epilogue, and "everyone in it is dead".
    ///
    /// The host runs the dream's place for as long as anyone is in it, also after its own exit
    /// (vanilla's teardown of the pad waits, <see cref="KeepPad"/>). The world runs again from the
    /// first wake-up: creatures unfreeze and the clock steps for whoever is awake.
    /// The first exit decides what the dream changed in the world (its outcome's world events run
    /// once, on the host); every later leaver gets only its own part of its outcome.
    /// </summary>
    internal static class DreamExit
    {
        private const byte ByPlayer = 1;
        private const byte ByOther = 2;

        /// <summary>Players who left the running dream by their own exit.</summary>
        private static readonly HashSet<int> _left = new HashSet<int>(); // reset-in: ResetSession
        /// <summary>What sets each event of the loaded pad off: a player's own body, something else, or both.</summary>
        private static readonly Dictionary<int, byte> _setOffBy = new Dictionary<int, byte>(); // process-scoped: rebuilt for each new pad
        private static readonly Dictionary<int, List<EventTrigger>> _triggersOf = new Dictionary<int, List<EventTrigger>>(); // process-scoped: rebuilt for each new pad
        private static int _padId; // process-scoped: instance id of the pad the maps above were built for

        /// <summary>This player's own exit is running (from its end-dream step to its wake-up).</summary>
        internal static bool LocalLeaving { get; private set; } // reset-in: ResetSession
        /// <summary>Host: its own exit leaves the dream running for the others.</summary>
        private static bool _hostKeepsSession; // reset-in: ResetSession
        /// <summary>Host: awake, and the dream still runs here for the players in it.</summary>
        internal static bool HostHostsFromOutside { get; private set; } // reset-in: ResetSession
        /// <summary>Someone has left: what the dream changed in the world is decided.</summary>
        internal static bool WorldOutcomeDecided { get; private set; } // reset-in: ResetSession
        /// <summary>This leaver's outcome events run for its own part only (the world's part was another's).</summary>
        internal static bool PersonalOnlyOutcome { get; private set; } // reset-in: ResetForNewSession
        /// <summary>An outcome event is firing for the leaver's own part only (call-scoped).</summary>
        internal static bool PersonalOnlyFire { get; private set; } // process-scoped: call-scoped, unwound by EndOwnPartFire
        /// <summary>Host: the dream's place stays up although the host is leaving or has left it.</summary>
        internal static bool HostKeepsPlace => _hostKeepsSession || HostHostsFromOutside;
        /// <summary>
        /// Host, still in the dream while someone is awake: the world's clock. The host's own
        /// clock shows the dream's hour until it wakes, so the world's time is counted here
        /// (sent to the players who are awake, and where the host's clock goes when it wakes).
        /// -1: not counting.
        /// </summary>
        internal static int HostWorldTime = -1; // reset-in: ResetForNewSession
        /// <summary>Host: the world's clock stepped while the host was still in the dream.</summary>
        internal static bool ClockRan => HostWorldTime >= 0;

        /// <summary>Host, dreaming: one step of the world's clock for the players who are awake.</summary>
        internal static void StepHostWorldTime()
        {
            if (HostWorldTime >= 0)
                HostWorldTime++;
        }

        /// <summary>Client: the host closed the session while this player's own exit was still playing.</summary>
        private static bool _overWhileLeaving; // reset-in: ResetSession

        private static Location _keptPad; // reset-in: ClearKeptPad
        private static DreamPreset _keptPreset; // reset-in: ClearKeptPad

        internal static bool HasLeft(int playerId) => _left.Contains(playerId);

        /// <summary>
        /// <paramref name="playerId"/> left the running dream by its own exit and its body is still
        /// reported on the pad (lying in the exit's bed through its wake-up). It is awake: it waits
        /// out of sight, every trigger and every creature's reach until it is back in the world.
        /// The dream's creatures went on hitting it there (the bunker dream's forest spirit).
        /// </summary>
        internal static bool HidesLeaverBody(int playerId, Vector3 pos)
            => _left.Count > 0 && _left.Contains(playerId) && DreamSyncManager.IsAtDreamPad(pos);

        /// <summary>A session begins: nothing of the last one is left.</summary>
        internal static void ResetForNewSession()
        {
            ResetSession();
            PersonalOnlyOutcome = false;
            HostWorldTime = -1;
        }

        /// <summary>
        /// The session is over. <see cref="PersonalOnlyOutcome"/> and <see cref="HostWorldTime"/>
        /// stay: the wake-up they belong to may still be playing (the session closes when the
        /// exit begins), and ends them itself (<see cref="OnLocalLeaveDone"/>).
        /// </summary>
        internal static void ResetSession()
        {
            _left.Clear();
            LocalLeaving = false;
            _hostKeepsSession = false;
            HostHostsFromOutside = false;
            WorldOutcomeDecided = false;
            _overWhileLeaving = false;
        }

        /// <summary>The session is over on this machine: the host lets go of the place it kept.</summary>
        internal static void OnSessionOver()
        {
            ClearKeptPad();
            ResetSession();
        }

        // ---- which endings are a player's own --------------------------------------------------

        /// <summary>
        /// The event on <paramref name="eventObject"/> ends the dream for the one player who set it
        /// off: it has an end-dream step, only a player's own body sets it off (an area it walks
        /// into, a thing it uses), and the dream neither hands over to another nor is the ending.
        /// Read from the scene, so every machine answers the same.
        /// </summary>
        internal static bool IsPersonalEnd(GameObject eventObject)
        {
            var net = ModRuntime.Network;
            if (eventObject == null || net == null || !net.IsConnected)
                return false;
            if (!DreamSession.IsActive || DreamSession.IsEnding)
                return false;
            // A solo dream: every ending of this player's own copy is its own way out.
            Transform own = SeparateDream.OwnPad;
            if (own != null)
            {
                GameEvents soloGe = eventObject.GetComponent<GameEvents>();
                return soloGe != null && eventObject.transform.IsChildOf(own) && HasEndDream(soloGe);
            }
            Dreams dreams = Dreams.Instance;
            if (dreams == null || dreams.preset == null || PresetHandsOver(dreams.preset))
                return false;
            Transform pad = DreamSyncManager.GetLoadedDreamPad();
            if (pad == null || !eventObject.transform.IsChildOf(pad))
                return false;
            GameEvents ge = eventObject.GetComponent<GameEvents>();
            if (ge == null || ge.destroyOnFire || !HasEndDream(ge))
                return false;
            if (_padId != pad.GetInstanceID())
                Build(pad);
            return _setOffBy.TryGetValue(ge.GetInstanceID(), out byte by) && by == ByPlayer;
        }

        private static bool PresetHandsOver(DreamPreset preset)
        {
            if (preset.outcomes == null)
                return false;
            for (int i = 0; i < preset.outcomes.Count; i++)
            {
                DreamPreset.Outcome oc = preset.outcomes[i];
                if (oc == null || oc.effects == null)
                    continue;
                for (int j = 0; j < oc.effects.Count; j++)
                {
                    DreamPreset.Outcome.Effect e = oc.effects[j];
                    if (e != null && e.type == DreamPreset.Outcome.Effect.Type.transferToDream && e.destPrefab != null)
                        return true;
                }
            }
            return false;
        }

        private static bool HasEndDream(GameEvents ge)
        {
            if (ge.events == null)
                return false;
            for (int i = 0; i < ge.events.Count; i++)
            {
                GameEvent step = ge.events[i];
                if (step != null && !step.disabled && step.type == GameEvent.Type.endDream)
                    return true;
            }
            return false;
        }

        private static void Build(Transform pad)
        {
            _padId = pad.GetInstanceID();
            _setOffBy.Clear();
            _triggersOf.Clear();
            var queue = new Queue<GameEvents>();
            GameEvents[] all = pad.GetComponentsInChildren<GameEvents>(true);
            EventTriggers[] holders = pad.GetComponentsInChildren<EventTriggers>(true);
            for (int i = 0; i < holders.Length; i++)
            {
                EventTriggers et = holders[i];
                if (et == null || et.eventTriggers == null)
                    continue;
                for (int j = 0; j < et.eventTriggers.Count; j++)
                {
                    EventTrigger t = et.eventTriggers[j];
                    if (t == null || t.gameEvents == null)
                        continue;
                    bool player = t.type == EventTrigger.Type.onActivate
                        || (t.type == EventTrigger.Type.area && et.reactsToPlayer);
                    Mark(t.gameEvents, player ? ByPlayer : ByOther, queue);
                    int id = t.gameEvents.GetInstanceID();
                    if (!_triggersOf.TryGetValue(id, out List<EventTrigger> list))
                        _triggersOf[id] = list = new List<EventTrigger>(1);
                    list.Add(t);
                }
            }
            // An event fired by another is set off by whatever sets that one off.
            while (queue.Count > 0)
            {
                GameEvents ge = queue.Dequeue();
                if (ge == null || ge.events == null || !_setOffBy.TryGetValue(ge.GetInstanceID(), out byte by))
                    continue;
                for (int i = 0; i < ge.events.Count; i++)
                {
                    GameEvent step = ge.events[i];
                    if (step == null || step.disabled)
                        continue;
                    if (step.type == GameEvent.Type.fireEvent)
                    {
                        if (step.activeModifier)
                        {
                            for (int k = 0; k < all.Length; k++)
                            {
                                if (all[k] != null && all[k].isWorldEvent && all[k].worldEventType == step.Value)
                                    Mark(all[k], by, queue);
                            }
                        }
                        else if (step.targetGameObjects != null)
                        {
                            for (int k = 0; k < step.targetGameObjects.Count; k++)
                            {
                                GameObject target = step.targetGameObjects[k];
                                if (target != null)
                                    Mark(target.GetComponent<GameEvents>(), by, queue);
                            }
                        }
                    }
                    else if (step.type == GameEvent.Type.fireTrigger && step.targetGameObjects != null)
                    {
                        for (int k = 0; k < step.targetGameObjects.Count; k++)
                        {
                            EventTriggers et = step.targetGameObjects[k] != null
                                ? step.targetGameObjects[k].GetComponent<EventTriggers>() : null;
                            if (et == null || et.eventTriggers == null)
                                continue;
                            for (int m = 0; m < et.eventTriggers.Count; m++)
                            {
                                if (et.eventTriggers[m] != null)
                                    Mark(et.eventTriggers[m].gameEvents, by, queue);
                            }
                        }
                    }
                }
            }
        }

        private static void Mark(GameEvents ge, byte by, Queue<GameEvents> queue)
        {
            if (ge == null)
                return;
            int id = ge.GetInstanceID();
            _setOffBy.TryGetValue(id, out byte had);
            byte now = (byte)(had | by);
            if (now == had)
                return;
            _setOffBy[id] = now;
            queue.Enqueue(ge);
        }

        /// <summary>
        /// A player's own exit fired: it is there again for the next player (vanilla fires it once,
        /// for the one player there is).
        /// </summary>
        internal static void Rearm(GameObject eventObject)
        {
            GameEvents ge = eventObject != null ? eventObject.GetComponent<GameEvents>() : null;
            Controller ctrl = Singleton<Controller>.Instance;
            if (ge == null || ctrl == null)
                return;
            // After GameEvents.fire, which is still on the stack and has just latched it.
            ctrl.waitFramesAndRun(delegate
            {
                if (ge == null)
                    return;
                ge.fired = false;
                if (_triggersOf.TryGetValue(ge.GetInstanceID(), out List<EventTrigger> triggers))
                {
                    for (int i = 0; i < triggers.Count; i++)
                    {
                        if (triggers[i] != null)
                            triggers[i].fired = false;
                    }
                }
            }, 1);
        }

        // ---- the leaver's own machine ----------------------------------------------------------

        /// <summary>This player can take an exit: it is in the dream, alive in it, and not already on its way out.</summary>
        internal static bool LocalCanLeave()
        {
            Dreams dreams = Dreams.Instance;
            return dreams != null && dreams.dreaming && !LocalLeaving
                && !FinalDreamsceneManager.IsLocalDead && !DreamSyncManager.IsHostOrderedDreamEnd;
        }

        /// <summary>The end-dream step of this player's own exit is about to run.</summary>
        internal static void NoteLocalExitStep() => LocalLeaving = true;

        /// <summary>
        /// A solo dream ends here by anything (an ending event, a scripted scene, a death): it is
        /// this player's own copy, so it is this player's own way out.
        /// </summary>
        internal static bool LocalSoloEnd()
        {
            if (LocalLeaving || !SeparateDream.LocalActive || DreamSyncManager.IsHostOrderedDreamEnd)
                return false;
            LocalLeaving = true;
            return true;
        }

        /// <summary>
        /// initiateEndDreaming of this player's own exit. False: the host leaves and nobody else is
        /// in the dream, so its end is the session's (the usual path).
        /// </summary>
        internal static bool BeginLocalLeave(Dreams dreams)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || !DreamSession.IsActive)
            {
                LocalLeaving = false;
                return false;
            }
            string preset = DreamSyncManager.ResolveActivePresetName();
            if (string.IsNullOrEmpty(preset) && dreams != null && dreams.preset != null)
                preset = dreams.preset.name;
            string outcome = dreams != null ? dreams.outcome ?? "" : "";
            var msg = new DreamLeftMessage
            {
                PlayerId = net.LocalPlayerId,
                SessionId = DreamSession.SessionId,
                PresetName = preset ?? "",
                OutcomeName = outcome
            };
            // Its world part was someone else's exit: only its own part of the outcome is left.
            PersonalOnlyOutcome = WorldOutcomeDecided;
            if (net.Role == NetworkRole.Host)
            {
                if (!AnyRemoteDreamer(net))
                {
                    // The last one in it: its end is the session's, but what the dream changed
                    // in the world was still the first leaver's.
                    LocalLeaving = false;
                    return false;
                }
                _hostKeepsSession = true;
                _left.Add(net.LocalPlayerId);
                WorldOutcomeDecided = true;
                if (DreamOutcomePolicy.ShouldMarkCompletedOnEnd(outcome))
                    DreamSession.MarkCompleted(DreamSession.PresetName);
                net.Broadcast(NetMessageType.DreamLeft, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            else
            {
                WorldOutcomeDecided = true;
                net.Send(NetMessageType.DreamLeft, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            ModLog.Event(LogCat.Dream, $"[DreamExit] this player leaves {preset} by '{outcome}'"
                + (PersonalOnlyOutcome ? " (own part of the outcome only)" : "")
                + (net.Role == NetworkRole.Host ? "; the dream goes on here for the others" : ""));
            return true;
        }

        /// <summary>Host, inside its own exit: vanilla's teardown of the pad waits for the last dreamer.</summary>
        internal static bool KeepPad(Dreams dreams)
        {
            if (!HostKeepsPlace || dreams == null || dreams.dreamLocation == null)
                return false;
            // A solo dream's copy is the host's own: nobody else plays in it.
            if (SeparateDream.OwnPad != null)
                return false;
            _keptPad = dreams.dreamLocation;
            _keptPreset = dreams.preset;
            ModLog.Event(LogCat.Dream, "[DreamExit] the dream's place is kept for the players still in it");
            return true;
        }

        /// <summary>endDreaming of this player's own exit: the session and the roster, in place of the party's end.</summary>
        internal static void OnLocalLeaveEnd(string outcome)
        {
            var net = ModRuntime.Network;
            bool host = net != null && net.Role == NetworkRole.Host;
            bool keep = host && _hostKeepsSession;
            bool overAlready = _overWhileLeaving;
            if (!keep && DreamSession.IsActive)
            {
                // The session's end resets this class; the wake-up still running needs to know
                // whose part of the outcome it applies.
                bool ownPartOnly = PersonalOnlyOutcome;
                DreamSession.End(outcome);
                PersonalOnlyOutcome = ownPartOnly;
            }
            DreamSyncManager.OnLocalDreamLeft(keep);
            LocalLeaving = false;
            _hostKeepsSession = false;
            if (keep)
            {
                HostHostsFromOutside = true;
                // Everyone still in it may be dead: then it ends for them now.
                HostAfterRosterChange(net, outcome);
            }
            else if (!host)
            {
                // The dream still runs on the host (unless this was its last player): this
                // player sits the rest of it out.
                DreamRetry.HostDreamRunning = !overAlready;
            }
        }

        /// <summary>
        /// Client, on the host's DreamEnded: true while this player's own exit is playing. It was
        /// the last one in the dream, so the host closed the session; its own wake-up goes on as
        /// it is and is not started a second time as the party's.
        /// </summary>
        internal static bool AbsorbSessionEnd()
        {
            if (!LocalLeaving)
                return false;
            _overWhileLeaving = true;
            return true;
        }

        /// <summary>The leaver's wake-up is done (endDreaming returned or threw).</summary>
        internal static void OnLocalLeaveDone()
        {
            PersonalOnlyOutcome = false;
            HostWorldTime = -1;
        }

        /// <summary>
        /// An event is about to fire. True (and <see cref="PersonalOnlyFire"/> set until
        /// <see cref="EndOwnPartFire"/>) when it is an outcome event of a leaver whose world part
        /// was another's: <paramref name="ge"/> null for a world event the caller already matched,
        /// else an outcome's own event (vanilla's fireGameEvent effect fires it directly).
        /// </summary>
        internal static bool BeginOwnPartFire(GameEvents ge)
        {
            if (!PersonalOnlyOutcome || PersonalOnlyFire)
                return false;
            if (ge != null && !IsLocalOutcomeEvent(ge))
                return false;
            PersonalOnlyFire = true;
            return true;
        }

        internal static void EndOwnPartFire() => PersonalOnlyFire = false;

        private static bool IsLocalOutcomeEvent(GameEvents ge)
        {
            Dreams dreams = Dreams.Instance;
            if (dreams == null)
                return false;
            DreamPreset.Outcome outcome;
            try { outcome = HarmonyLib.Traverse.Create(dreams).Field("outcomePreset").GetValue<DreamPreset.Outcome>(); }
            catch { return false; }
            if (outcome == null || outcome.effects == null)
                return false;
            for (int i = 0; i < outcome.effects.Count; i++)
            {
                DreamPreset.Outcome.Effect e = outcome.effects[i];
                if (e == null || e.type != DreamPreset.Outcome.Effect.Type.fireGameEvent)
                    continue;
                var go = e.destPrefab as GameObject;
                if (go != null && go.GetComponent<GameEvents>() == ge)
                    return true;
            }
            return false;
        }

        // ---- the others --------------------------------------------------------------------------

        internal static void HandleDreamLeft(LanNetworkManager net, DreamLeftMessage msg)
        {
            if (net == null)
                return;
            if (net.Role == NetworkRole.Host)
            {
                HostOnPeerLeft(net, net.CurrentReceivePlayerId, msg);
                return;
            }
            if (msg.PlayerId <= 0 || msg.PlayerId == net.LocalPlayerId)
                return;
            if (DreamSession.IsActive && DreamSession.SessionId != 0 && msg.SessionId != 0
                && msg.SessionId != DreamSession.SessionId)
                return;
            _left.Add(msg.PlayerId);
            WorldOutcomeDecided = true;
            if (net.TryGetRemoteState(msg.PlayerId, out var st) && st != null)
                st.IsDeadInDream = false;
            FinalDreamsceneManager.OnPlayerLeftDream(msg.PlayerId);
            ModLog.Event(LogCat.Dream, $"[DreamExit] p{msg.PlayerId} left the dream by '{msg.OutcomeName}'");
        }

        private static void HostOnPeerLeft(LanNetworkManager net, int playerId, DreamLeftMessage msg)
        {
            if (playerId <= 0 || !DreamSession.IsActive
                || (msg.SessionId != 0 && msg.SessionId != DreamSession.SessionId)
                || _left.Contains(playerId))
            {
                ModLog.Event(LogCat.Dream, $"[DreamExit] p{playerId}'s leave ignored (no such dream here)");
                return;
            }
            string preset = DreamSession.PresetName;
            string outcome = msg.OutcomeName ?? "";
            bool first = !WorldOutcomeDecided;
            _left.Add(playerId);
            WorldOutcomeDecided = true;
            if (net.TryGetRemoteState(playerId, out var st) && st != null)
                st.IsDeadInDream = false;
            FinalDreamsceneManager.OnPlayerLeftDream(playerId);
            if (DreamOutcomePolicy.ShouldMarkCompletedOnEnd(outcome))
                DreamSession.MarkCompleted(preset);
            ModLog.Event(LogCat.Dream, $"[DreamExit] p{playerId} left {preset} by '{outcome}'"
                + (first ? " (first out: its outcome is the world's)" : ""));

            var relay = new DreamLeftMessage
            {
                PlayerId = playerId,
                SessionId = DreamSession.SessionId,
                PresetName = preset ?? "",
                OutcomeName = outcome
            };
            net.SendToAllExcept(playerId, NetMessageType.DreamLeft, w => relay.Serialize(w), DeliveryMethod.ReliableOrdered);

            // Someone is awake: the world runs again, also while the host still dreams. The
            // host's clock keeps the dream's hour; the world's is counted from where it stopped.
            Dreams dreams = Dreams.Instance;
            if (dreams != null && dreams.dreaming && HostWorldTime < 0)
                HostWorldTime = (int)dreams.timeCopy;
            DreamSyncManager.UnfreezeWorld(restoreTime: false);
            if (first)
                DreamSyncManager.HostFireOutcomeWorldFor(playerId, preset, outcome);
            HostAfterRosterChange(net, outcome);
        }

        /// <summary>Host: someone left. Nobody in it any more ends the session; only dead players in it ends it for them.</summary>
        internal static void HostAfterRosterChange(LanNetworkManager net, string outcome)
        {
            if (net == null || net.Role != NetworkRole.Host || !DreamSession.IsActive)
                return;
            if (!DreamSyncManager.IsLocalDreamActive && !AnyRemoteDreamer(net))
            {
                HostEndFromOutside(net, outcome, runWorld: false, actor: 0);
                return;
            }
            FinalDreamsceneManager.HostRecheckAllDead("a dreamer left");
        }

        /// <summary>
        /// Host, awake: the dream ends for whoever is still in it (the last one left, or an ending
        /// that is everyone's fired here), with nobody on this machine to wake.
        /// </summary>
        internal static void HostEndFromOutside(LanNetworkManager net, string outcome, bool runWorld, int actor)
        {
            if (net == null || net.Role != NetworkRole.Host || !DreamSession.IsActive)
                return;
            string preset = DreamSession.PresetName;
            ModLog.Event(LogCat.Dream, $"[DreamExit] {preset} is over (outcome '{outcome}'), ended from outside it");
            var ended = DreamEndedMessage.Build(preset ?? "", outcome ?? "");
            net.Broadcast(NetMessageType.DreamEnded, w => ended.Serialize(w), DeliveryMethod.ReliableOrdered);
            if (runWorld && !WorldOutcomeDecided)
            {
                WorldOutcomeDecided = true;
                DreamSyncManager.HostFireOutcomeWorldFor(actor, preset, outcome);
            }
            DreamSession.End(outcome);
            DreamSyncManager.OnHostedDreamOver();
        }

        internal static bool AnyRemoteDreamer(LanNetworkManager net)
        {
            foreach (int id in net.GetHandshakedPeerIds())
            {
                if (id > 0 && id != net.LocalPlayerId && DreamSyncManager.IsRemoteInDream(id))
                    return true;
            }
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy != null && proxy.PlayerId > 0 && DreamSyncManager.IsRemoteInDream(proxy.PlayerId))
                    return true;
            }
            return false;
        }

        /// <summary>Vanilla Dreams.destroyDream for the pad the host kept.</summary>
        private static void ClearKeptPad()
        {
            Location pad = _keptPad;
            DreamPreset preset = _keptPreset;
            _keptPad = null;
            _keptPreset = null;
            if (preset == null && pad == null)
                return;
            try
            {
                if (preset != null)
                {
                    WorldGrid wg = Singleton<WorldGrid>.Instance;
                    WorldGrid.Grid grid = wg != null ? wg.getGrid(preset.name) : null;
                    if (grid != null)
                        wg.grids.Remove(grid);
                    if (AstarData.active != null && AstarData.active.astarData != null)
                    {
                        NavGraph graph = AstarData.active.astarData.GetGraph(preset.name);
                        if (graph != null)
                            AstarData.active.astarData.RemoveGraph(graph);
                    }
                    OutsideLocations ol = Singleton<OutsideLocations>.Instance;
                    if (ol != null && ol.spawnedLocations.ContainsKey(preset.name))
                        ol.spawnedLocations.Remove(preset.name);
                }
                if (pad != null && pad.gameObject != null)
                    UnityEngine.Object.Destroy(pad.gameObject, 4f);
                ModLog.Event(LogCat.Dream, "[DreamExit] the kept dream place is gone");
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Dream, "[DreamExit] releasing the kept dream place: " + ex.Message);
            }
        }
    }
}
