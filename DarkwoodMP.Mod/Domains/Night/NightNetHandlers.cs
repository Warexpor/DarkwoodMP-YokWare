using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Night shadows / scenarios / sleep / time handlers composed for 0.8.</summary>
    internal sealed class NightNetHandlers
    {
        private readonly LanNetworkManager _net;

        private bool _hasPendingScenarioSync;
        private ScenarioSyncMessage _pendingScenarioSync;
        private bool _hasPendingScenarioEvent;
        private ScenarioEventFiredMessage _pendingScenarioEvent;
        private bool _hasPendingScenarioStateBulk;
        private ScenarioStateBulkMessage _pendingScenarioStateBulk;

        internal NightNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingScenario()
        {
            _hasPendingScenarioSync = false;
            _pendingScenarioSync = default;
            _hasPendingScenarioEvent = false;
            _pendingScenarioEvent = default;
            _hasPendingScenarioStateBulk = false;
            _pendingScenarioStateBulk = default;
            Patches.ScenarioPendingEventState.PendingEventIndex = -1;
            Patches.ScenarioPendingEventState.PendingScenario = null;
        }

        internal void ClearShadowLookups()
        {
            _clientShadowLookups?.Clear();
        }

        internal void HandleShadowEvent(ShadowEventMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (Player.Instance == null) return;

            // Set the CharacterSpawner flags so the game knows shadows are active.
            // Do not call Player.tryToSpawnShadow(); it spawns shadows at the wrong local
            // positions.  The host sends individual ShadowSpawnMessages with exact positions.
            var cs = Singleton<CharacterSpawner>.Instance;
            if (cs != null)
            {
                cs.shadowsRemove = false;
                cs.shadowsPaused = false;
                cs.spawnedShadows = true;
                cs.spawnedShadowsAmount = 8;
            }

            ModRuntime.LegacyInfo("[ShadowSync] client received NightShadows event, awaiting host ShadowSpawn + ShadowStateUpdate messages");
        }

        /// <summary>
        /// Client→host: NightShadows perk wave for the requester only (per-owner curse).
        /// </summary>
        internal void HandleNightShadowSpawnRequest(NightShadowSpawnRequestMessage msg)
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected) return;

            int requesterId = _net.CurrentReceivePlayerId;
            if (requesterId <= 0)
            {
                ModRuntime.LegacyInfo("[NightShadow] reject: no sender id");
                return;
            }

            if (Core.isDay()
                || Singleton<Controller>.Instance == null
                || !Singleton<Controller>.Instance.isHardNight
                || (Singleton<Dreams>.Instance != null && Singleton<Dreams>.Instance.dreaming))
            {
                ModRuntime.LegacyInfo($"[NightShadow] reject P{requesterId}: not hard night / day / dream");
                return;
            }

            if (!_net.RemotePlayers.TryGetValue(requesterId, out var st) || !st.HasNightShadows)
            {
                ModRuntime.LegacyInfo($"[NightShadow] reject P{requesterId}: no NightShadows perk");
                return;
            }

            if (st.HasLightProtection)
            {
                ModRuntime.LegacyInfo($"[NightShadow] reject P{requesterId}: in light");
                return;
            }

            if (!NightShadowsRateLimit.TryAllow(requesterId))
            {
                ModRuntime.LegacyInfo($"[NightShadow] reject P{requesterId}: rate limit");
                return;
            }

            RemotePlayerProxy proxy = _net.GetProxy(requesterId);
            if (proxy == null)
            {
                ModRuntime.LegacyInfo($"[NightShadow] reject P{requesterId}: no proxy");
                return;
            }

            ModRuntime.LegacyInfo($"[NightShadow] host spawning perk wave for P{requesterId}");
            SpawnNightShadowWaveFor(requesterId, proxy.transform.position);
        }

        /// <summary>
        /// Host: 8 delayed shadow spawns around <paramref name="origin"/> owned by
        /// <paramref name="ownerPlayerId"/> (proxy AI when not host).
        /// </summary>
        public void SpawnNightShadowWaveFor(int ownerPlayerId, Vector3 origin)
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected) return;

            var cs = Singleton<CharacterSpawner>.Instance;
            if (cs != null)
            {
                cs.shadowsRemove = false;
                cs.shadowsPaused = false;
                cs.spawnedShadows = true;
                cs.spawnedShadowsAmount = 8;
            }

            _net.SendShadowEvent(new ShadowEventMessage());

            int owner = ownerPlayerId;
            Vector3 center = origin;
            for (int i = 0; i < 8; i++)
            {
                int delayIndex = i;
                Singleton<Controller>.Instance.Invoke(delegate
                {
                    if (Core.isDay()) return;
                    if (Singleton<CharacterSpawner>.Instance != null
                        && Singleton<CharacterSpawner>.Instance.shadowsRemove)
                        return;

                    Vector3 spawnOrigin = center;
                    if (owner != _net.LocalPlayerId)
                    {
                        RemotePlayerProxy p = _net.GetProxy(owner);
                        if (p != null)
                            spawnOrigin = p.transform.position;
                    }

                    Vector3 position = Core.randomPosAround(spawnOrigin, 400f, 700f,
                        canBeInside: true, mustBeInsideGraph: false);
                    ShadowSpawnContext.PushOwner(owner);
                    try
                    {
                        Core.AddPrefab("characters/fakechars/shadow", position,
                            Quaternion.Euler(90f, UnityEngine.Random.Range(0, 360), 0f), null);
                    }
                    finally
                    {
                        ShadowSpawnContext.PopOwner();
                    }
                }, 9f * (float)delayIndex, timeScaleDependent: true);
            }
        }

        internal void HandleShadowSpawn(ShadowSpawnMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (Player.Instance == null) return;

            var spawner = Singleton<CharacterSpawner>.Instance;
            if (spawner == null) return;

            if (Core.isDay() || spawner.shadowsRemove)
                return;

            string prefabPath = msg.ShadowType == 1
                ? "characters/fakechars/shadow_immortal"
                : "characters/fakechars/shadow";

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(90f, msg.RotY, 0f);
            GameObject go = Core.AddPrefab(prefabPath, pos, rot, null);
            if (go == null) return;

            // Attach a ShadowSyncInfo so HandleShadowStateUpdate can find it by ID
            var info = go.GetComponent<ShadowSyncInfo>();
            if (info == null)
                info = go.AddComponent<ShadowSyncInfo>();
            info.ShadowId = msg.ShadowId;
            info.ShadowType = msg.ShadowType;

            // Apply initial state
            var sc = go.GetComponent<ShadowCreature>();
            if (sc != null)
            {
                sc.distanceToPlayer = msg.DistanceToPlayer;
                sc.dead = (msg.Flags & 2) != 0;
            }

            // Start the Float animation (blocked Start/appear won't trigger it)
            var anim = go.GetComponent<tk2dSpriteAnimator>();
            if (anim != null && anim.GetClipByName("Float") != null)
                anim.Play("Float");

            if (_clientShadowLookups == null)
                _clientShadowLookups = new Dictionary<short, ShadowCreature>();
            if (sc != null)
                _clientShadowLookups[msg.ShadowId] = sc;
        }

        private Dictionary<short, ShadowCreature> _clientShadowLookups;

        internal void HandleShadowStateUpdate(ShadowStateUpdateMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (_clientShadowLookups == null) return;

            if (_clientShadowLookups.TryGetValue(msg.ShadowId, out var sc) && sc != null)
            {
                // Update position
                sc.transform.position = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                sc.transform.rotation = Quaternion.Euler(90f, msg.RotY, 0f);
                sc.distanceToPlayer = msg.DistanceToPlayer;

                bool dead = (msg.Flags & 2) != 0;
                if (dead && !sc.dead)
                {
                    sc.dead = true;
                    var anim = sc.GetComponent<tk2dSpriteAnimator>();
                    if (anim != null && anim.GetClipByName("Death1") != null)
                        anim.Play("Death1");
                }

                if (dead)
                {
                    _clientShadowLookups.Remove(msg.ShadowId);
                }
            }
            else
            {
                // Shadow not yet created; treat this as a spawn if ShadowSpawnMessage was missed.
                var spawnMsg = new ShadowSpawnMessage
                {
                    ShadowId = msg.ShadowId,
                    ShadowType = 0,
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ,
                    RotY = msg.RotY,
                    DistanceToPlayer = msg.DistanceToPlayer,
                    Flags = msg.Flags
                };
                HandleShadowSpawn(spawnMsg);
            }
        }

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
                ModRuntime.Log?.LogWarning($"[ScenarioSync] unknown scenario '{msg.ScenarioName}'");
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
                ModRuntime.Log?.LogWarning($"[ScenarioStateBulk] unknown scenario '{msg.ScenarioName}'");
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
                ModRuntime.Log?.LogWarning($"[ScenarioEventFired] unknown nightId {msg.NightId}");
                return;
            }

            if (msg.EventIndex >= scenario.customEventAndInts.Count)
            {
                ModRuntime.Log?.LogWarning($"[ScenarioEventFired] index {msg.EventIndex} out of range (count={scenario.customEventAndInts.Count})");
                return;
            }

            if (scenario.customEventAndInts[msg.EventIndex].customEvent == null)
            {
                ModRuntime.Log?.LogWarning($"[ScenarioEventFired] null CustomEvent at index {msg.EventIndex}");
                return;
            }

            // Ensure client currentScenario matches the event's night (join race).
            if (ns.currentScenario != scenario)
                ns.currentScenario = scenario;

            ModRuntime.LegacyInfo($"[ScenarioEventFired] host fired event index {msg.EventIndex} in nightId {msg.NightId}");

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
                // Already clear; still push the clock so a stale requester unfreezes.
                _net.SendTimeSyncTo(-1);
                return;
            }

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
