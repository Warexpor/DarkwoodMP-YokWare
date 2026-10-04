using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Config;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Night shadows / scenarios / sleep / time handlers composed for 0.8.</summary>
    internal sealed partial class NightNetHandlers
    {

        internal void HandleScenarioSync(ScenarioSyncMessage msg)
        {
            ApplyScenarioSync(msg);
        }

        /// <summary>
        /// Client: set host's current night scenario by name (bypasses setCurrentScenario
        /// which is blocked on clients). Used by live ScenarioSync and join ScenarioStateSync.
        /// </summary>
        internal void ApplyScenarioSync(ScenarioSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (string.IsNullOrEmpty(msg.ScenarioName)) return;

            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null)
            {
                _pendingScenarioSync = msg;
                _hasPendingScenarioSync = true;
                ModRuntime.LegacyInfo("[ScenarioSync] NightScenarios not ready — queued");
                return;
            }

            NightScenario scenario = ns.getScenario(msg.ScenarioName);
            if (scenario == null)
            {
                ModLog.WarnRate(LogCat.World, "scenario-sync-unknown",
                    $"[ScenarioSync] unknown scenario '{msg.ScenarioName}'");
                return;
            }

            ns.currentScenario = scenario;
            ModRuntime.LegacyInfo($"[ScenarioSync] client set currentScenario to '{msg.ScenarioName}'");
        }

        /// <summary>
        /// Late-join: apply scenario name + fired-event latch flags only.
        /// Never calls CustomEvent.fire / RandomEvent.fire / checkFrequencies.
        /// </summary>
        internal void ApplyScenarioStateBulk(ScenarioStateBulkMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (string.IsNullOrEmpty(msg.ScenarioName)) return;

            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null)
            {
                _pendingScenarioStateBulk = msg;
                _hasPendingScenarioStateBulk = true;
                ModRuntime.LegacyInfo("[ScenarioStateBulk] NightScenarios not ready — queued");
                return;
            }

            NightScenario scenario = ns.getScenario(msg.ScenarioName);
            if (scenario == null)
            {
                ModLog.WarnRate(LogCat.World, "scenario-bulk-unknown",
                    $"[ScenarioStateBulk] unknown scenario '{msg.ScenarioName}'");
                return;
            }

            ns.currentScenario = scenario;

            if (msg.EventIndices != null && msg.FiredCount > 0)
            {
                int n = System.Math.Min(msg.FiredCount, msg.EventIndices.Length);
                for (int i = 0; i < n; i++)
                {
                    int idx = msg.EventIndices[i];
                    if (idx < 0 || idx >= scenario.customEventAndInts.Count) continue;
                    var cei = scenario.customEventAndInts[idx];
                    if (cei == null || cei.customEvent == null) continue;

                    CustomEvent ce = cei.customEvent;
                    if (msg.Started != null && i < msg.Started.Length && msg.Started[i])
                        ce.started = true;

                    if (ce.theEvent != null)
                    {
                        if (msg.StartedToday != null && i < msg.StartedToday.Length && msg.StartedToday[i])
                            ce.theEvent.startedToday = true;
                        if (msg.Disabled != null && i < msg.Disabled.Length && msg.Disabled[i])
                            ce.theEvent.disabled = true;
                    }
                }
            }

            if (msg.CurrentEventIndex >= 0
                && msg.CurrentEventIndex < scenario.customEventAndInts.Count)
            {
                var cur = scenario.customEventAndInts[msg.CurrentEventIndex];
                if (cur != null && cur.customEvent != null)
                {
                    CustomEvent ce = cur.customEvent;
                    ce.started = true;
                    if (msg.CurrentEventTimeDay >= 0 || msg.CurrentEventTimeTime >= 0)
                        ce.timeStarted = new TimeAndDay(msg.CurrentEventTimeTime, msg.CurrentEventTimeDay);
                    scenario.currentEvent = ce;
                }
            }
            else
            {
                scenario.currentEvent = null;
            }

            ModRuntime.LegacyInfo(
                $"[ScenarioStateBulk] applied '{msg.ScenarioName}' fired={msg.FiredCount} currentIdx={msg.CurrentEventIndex}");
        }

        internal void TryFlushPendingScenario()
        {
            if (_net.Role != NetworkRole.Client) return;

            if (_hasPendingScenarioSync && Singleton<NightScenarios>.Instance != null)
            {
                _hasPendingScenarioSync = false;
                var pending = _pendingScenarioSync;
                _pendingScenarioSync = default;
                ApplyScenarioSync(pending);
            }

            if (_hasPendingScenarioStateBulk && Singleton<NightScenarios>.Instance != null)
            {
                _hasPendingScenarioStateBulk = false;
                var pending = _pendingScenarioStateBulk;
                _pendingScenarioStateBulk = default;
                ApplyScenarioStateBulk(pending);
            }

            if (_hasPendingScenarioEvent && Singleton<NightScenarios>.Instance != null)
            {
                _hasPendingScenarioEvent = false;
                var ev = _pendingScenarioEvent;
                _pendingScenarioEvent = default;
                ApplyScenarioEventFired(ev);
            }
        }

        internal void HandleScenarioEventFired(ScenarioEventFiredMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (msg.EventIndex < 0) return;

            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null)
            {
                _pendingScenarioEvent = msg;
                _hasPendingScenarioEvent = true;
                return;
            }

            ApplyScenarioEventFired(msg);
        }

        internal void ApplyScenarioEventFired(ScenarioEventFiredMessage msg)
        {
            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null) return;

            NightScenario scenario = null;
            for (int i = 0; i < ns.scenarios.Count; i++)
            {
                if (ns.scenarios[i] != null && ns.scenarios[i].nightId == msg.NightId)
                {
                    scenario = ns.scenarios[i];
                    break;
                }
            }

            if (scenario == null)
            {
                ModLog.WarnRate(LogCat.World, "scenario-event-night",
                    $"[ScenarioEventFired] unknown nightId {msg.NightId}");
                return;
            }

            if (msg.EventIndex >= scenario.customEventAndInts.Count)
            {
                ModLog.WarnRate(LogCat.World, "scenario-event-index",
                    $"[ScenarioEventFired] index {msg.EventIndex} out of range (count={scenario.customEventAndInts.Count})");
                return;
            }

            if (scenario.customEventAndInts[msg.EventIndex].customEvent == null)
            {
                ModLog.WarnRate(LogCat.World, "scenario-event-null",
                    $"[ScenarioEventFired] null CustomEvent at index {msg.EventIndex}");
                return;
            }

            // Ensure client currentScenario matches the event's night (join race).
            if (ns.currentScenario != scenario)
                ns.currentScenario = scenario;

            ModRuntime.LegacyInfo($"[ScenarioEventFired] host fired event index {msg.EventIndex} in nightId {msg.NightId}");

            CustomEvent ce = scenario.customEventAndInts[msg.EventIndex].customEvent;
            // Location events belong to a world location; never replay one into a location pad.
            var olLocal = Singleton<OutsideLocations>.Instance;
            bool outside = Player.Instance == null || Player.Instance.whereAmI == null
                || Player.Instance.whereAmI.bigLocation == null
                || (olLocal != null && olLocal.playerInOutsideLocation);
            if (ce != null && ce.theEvent != null && ce.theEvent.type == RandomEvent.Type.locationEvent)
            {
                if (outside)
                {
                    Patches.ScenarioPendingEventState.PendingEventIndex = -1;
                    Patches.ScenarioPendingEventState.PendingScenario = null;
                    return;
                }
                Patches.ClientRandomEventGate.Arm(20f);
                try
                {
                    using (new NetworkApplyGuard())
                        ce.fire(force: true);
                }
                catch (System.Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ScenarioEventFired] location event replay failed: " + ex.Message);
                }
                Patches.ScenarioPendingEventState.PendingEventIndex = -1;
                Patches.ScenarioPendingEventState.PendingScenario = null;
                return;
            }

            Patches.ScenarioPendingEventState.PendingEventIndex = msg.EventIndex;
            Patches.ScenarioPendingEventState.PendingScenario = scenario;
        }

        /// <summary>
        /// Client slept: the host may adopt the post-sleep clock, then send
        /// TimeSync to all peers. This does not run the full day chain.
        /// </summary>
        internal void HandleSleepEndRequest(SleepEndRequestMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;

            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;

            bool forward = msg.Day > ctrl.day
                || (msg.Day == ctrl.day && msg.CurrentTime > ctrl.CurrentTime)
                || (msg.Day == ctrl.day && msg.CurrentTime == ctrl.CurrentTime
                    && msg.IsAfterNight != ctrl.isAfterNight);

            if (!forward)
            {
                ModRuntime.LegacyInfo(
                    $"[SleepSync] ignore non-forward sleep day={msg.Day} time={msg.CurrentTime} " +
                    $"(host day={ctrl.day} time={ctrl.CurrentTime})");
                // Still rebroadcast host clock so client snaps.
                _net.SendTimeSyncTo(-1);
                return;
            }

            ctrl.day = msg.Day;
            ctrl.CurrentTime = msg.CurrentTime;

            // Mirror the after-night flag only; do not run startAfterNight or
            // endAfterNight world chains.
            if (msg.IsAfterNight && !ctrl.isAfterNight)
            {
                ctrl.isAfterNight = true;
                try
                {
                    if (Player.Instance != null && Player.Instance.effects != null)
                        ctrl.addAfterNightEffect();
                }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[SleepSync] addAfterNightEffect: " + ex.Message);
                }
            }
            else if (!msg.IsAfterNight && ctrl.isAfterNight)
            {
                ctrl.isAfterNight = false;
                try { ctrl.removeAfterNightEffect(); }
                catch (System.Exception ex)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.Log?.LogWarning("[SleepSync] removeAfterNightEffect: " + ex.Message);
                }
            }

            try { ctrl.refreshTimeNoLogic(); }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[SleepSync] refreshTimeNoLogic: " + ex.Message);
            }

            ModRuntime.LegacyInfo(
                $"[SleepSync] host adopted client sleep day={msg.Day} time={msg.CurrentTime} afterNight={msg.IsAfterNight}");
            _net.SendTimeSyncTo(-1);
        }

        /// <summary>
        /// Client left the hideout during the morning freeze; the host runs
        /// endAfterNight once
        /// (trader despawn, time++, clear freeze) then TimeSync fans out.
        /// </summary>
        internal void HandleAfterNightEndRequest(AfterNightEndRequestMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;

            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;

            if (!ctrl.isAfterNight)
            {
                MorningHideoutHold.Reset();
                // Already clear; still push the clock so a stale requester unfreezes.
                _net.SendTimeSyncTo(-1);
                return;
            }

            bool confirmedOutside = true;
            RemotePlayerProxy leaver = _net.GetProxy(_net.CurrentReceivePlayerId);
            if (leaver != null)
                confirmedOutside = !MorningHideoutHold.PositionIsPlayerBase(leaver.transform.position);
            MorningHideoutHold.NoteLeft(_net.CurrentReceivePlayerId, confirmedOutside);
            if (MorningHideoutHold.SomeoneStillInside())
            {
                ModRuntime.LegacyInfo(
                    $"[DayNight] peer p{_net.CurrentReceivePlayerId} left the hideout — someone is still inside, morning stays");
                return;
            }

            MorningHideoutHold.Reset();
            ModRuntime.LegacyInfo(
                $"[DayNight] host endAfterNight from peer p{_net.CurrentReceivePlayerId}");
            try
            {
                // Under NetworkApplyGuard, the endAfterNight postfix skips TimeSync;
                // flush it here.
                ctrl.endAfterNight();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DayNight] host endAfterNight failed: " + ex.Message);
            }
            _net.SendTimeSyncTo(-1);
        }

    
    }
}
