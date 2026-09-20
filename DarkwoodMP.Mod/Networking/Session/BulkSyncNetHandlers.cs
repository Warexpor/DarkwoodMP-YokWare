using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Late-join / world bulk sync (reputation, scenario, hideout, workbench, map, skills).
    /// Flag bulk lives on <see cref="FlagNetHandlers"/> (Flag-owned).
    /// </summary>
    internal sealed class BulkSyncNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal BulkSyncNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>Send all NPC reputations to all clients.</summary>
        internal void SendReputationBulkSync() => SendReputationBulkSyncTo(-1);

        internal void SendReputationBulkSyncTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            int count = flags.npcStates.Count;
            var msg = new ReputationBulkSyncMessage
            {
                NpcCount = count,
                NpcNames = new string[count],
                Reputations = new int[count],
                Dead = new bool[count]
            };
            for (int i = 0; i < count; i++)
            {
                msg.NpcNames[i] = flags.npcStates[i].name;
                msg.Reputations[i] = flags.npcStates[i].reputation;
                msg.Dead[i] = flags.npcStates[i].dead;
            }
            _net.SendBulkOrAll(NetMessageType.ReputationBulkSync, w => msg.Serialize(w), targetPlayerId);
        }

        internal void HandleReputationBulkSync(ReputationBulkSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            if (msg.NpcNames == null) return;

            for (int i = 0; i < msg.NpcCount && i < msg.NpcNames.Length; i++)
            {
                string name = msg.NpcNames[i];
                if (string.IsNullOrEmpty(name)) continue;

                var state = flags.getNPCState(name);
                if (state == null)
                {
                    // Create state for shared NPCs not yet in local list (dead flag + rep).
                    state = new Flags.NPCState
                    {
                        name = name,
                        wantsToTalk = true
                    };
                    flags.npcStates.Add(state);
                }

                // Dead is world and story state; apply it for all NPCs.
                if (msg.Dead != null && i < msg.Dead.Length)
                    state.dead = msg.Dead[i];

                // Never overwrite morning-trader standing with host bulk.
                if (Patches.ReputationSyncUtil.IsPerPlayerReputationNpcName(name))
                    continue;

                if (msg.Reputations != null && i < msg.Reputations.Length)
                    state.reputation = msg.Reputations[i];
            }
            ModLog.Event(LogCat.Session, $"[BulkSync] Reputation bulk applied ({msg.NpcCount} entries, night-traders skipped for rep)");
        }

        /// <summary>
        /// Send current night scenario + non-firing event flags to clients.
        /// Late-join uses ScenarioStateBulk (138); does not replay fire.
        /// </summary>
        internal void SendScenarioBulkSync() => SendScenarioBulkSyncTo(-1);

        internal void SendScenarioBulkSyncTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null || ns.currentScenario == null) return;

            NightScenario scenario = ns.currentScenario;
            var indices = new List<int>(8);
            var started = new List<bool>(8);
            var startedToday = new List<bool>(8);
            var disabled = new List<bool>(8);

            int currentIdx = -1;
            int currentDay = -1;
            int currentTime = -1;

            for (int i = 0; i < scenario.customEventAndInts.Count; i++)
            {
                var cei = scenario.customEventAndInts[i];
                if (cei == null || cei.customEvent == null) continue;

                CustomEvent ce = cei.customEvent;
                if (scenario.currentEvent == ce)
                {
                    currentIdx = i;
                    if (ce.timeStarted != null)
                    {
                        currentDay = ce.timeStarted.day;
                        currentTime = ce.timeStarted.time;
                    }
                }

                bool reStarted = ce.started;
                bool reToday = ce.theEvent != null && ce.theEvent.startedToday;
                bool reDisabled = ce.theEvent != null && ce.theEvent.disabled;
                if (!reStarted && !reToday && !reDisabled && scenario.currentEvent != ce)
                    continue;

                indices.Add(i);
                started.Add(reStarted || scenario.currentEvent == ce);
                startedToday.Add(reToday);
                disabled.Add(reDisabled);
            }

            // Always include currentEvent index even if flags were somehow clear.
            if (currentIdx >= 0 && !indices.Contains(currentIdx))
            {
                indices.Add(currentIdx);
                started.Add(true);
                startedToday.Add(
                    scenario.currentEvent.theEvent != null
                    && scenario.currentEvent.theEvent.startedToday);
                disabled.Add(
                    scenario.currentEvent.theEvent != null
                    && scenario.currentEvent.theEvent.disabled);
            }

            int count = indices.Count;
            var msg = new ScenarioStateBulkMessage
            {
                ScenarioName = scenario.name,
                CurrentEventIndex = currentIdx,
                CurrentEventTimeDay = currentDay,
                CurrentEventTimeTime = currentTime,
                FiredCount = count,
                EventIndices = indices.ToArray(),
                Started = started.ToArray(),
                StartedToday = startedToday.ToArray(),
                Disabled = disabled.ToArray()
            };

            _net.SendBulkOrAll(NetMessageType.ScenarioStateBulk, w => msg.Serialize(w), targetPlayerId);
        }

        internal void HandleScenarioStateSync(ScenarioSyncMessage msg)
        {
            // Name-only join path (legacy ScenarioStateSync 98). Prefer ScenarioStateBulk.
            _net.NightHandlers.ApplyScenarioSync(msg);
            ModLog.Event(LogCat.Session, $"[BulkSync] Scenario applied: {msg.ScenarioName}");
        }

        internal void HandleScenarioStateBulk(ScenarioStateBulkMessage msg)
        {
            _net.NightHandlers.ApplyScenarioStateBulk(msg);
            ModLog.Event(LogCat.Session,
                $"[BulkSync] ScenarioStateBulk applied: {msg.ScenarioName} fired={msg.FiredCount} currentIdx={msg.CurrentEventIndex}");
        }

        /// <summary>Send hideout oven enable states to all clients.</summary>
        internal void SendHideoutStateSync() => SendHideoutStateSyncTo(-1);

        internal void SendHideoutStateSyncTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            var ovens = WorldQueryHelper.GetCachedSceneComponents<ExperienceMachine>();
            int count = Mathf.Min(ovens.Length, 128);
            var msg = new HideoutStateSyncMessage
            {
                OvenCount = count,
                PosX = new float[count],
                PosY = new float[count],
                PosZ = new float[count],
                IsOn = new bool[count]
            };
            for (int i = 0; i < count; i++)
            {
                msg.PosX[i] = ovens[i].transform.position.x;
                msg.PosY[i] = ovens[i].transform.position.y;
                msg.PosZ[i] = ovens[i].transform.position.z;
                msg.IsOn[i] = ovens[i].isOn;
            }
            _net.SendBulkOrAll(NetMessageType.HideoutStateSync, w => msg.Serialize(w), targetPlayerId);
        }

        internal void HandleHideoutStateSync(HideoutStateSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (msg.OvenCount <= 0 || msg.PosX == null) return;

            var machines = WorldQueryHelper.GetCachedSceneComponents<ExperienceMachine>();
            for (int i = 0; i < msg.OvenCount; i++)
            {
                Vector3 pos = new Vector3(msg.PosX[i], msg.PosY[i], msg.PosZ[i]);
                bool wantOn = msg.IsOn != null && i < msg.IsOn.Length && msg.IsOn[i];
                for (int j = 0; j < machines.Length; j++)
                {
                    var em = machines[j];
                    if (em == null) continue;
                    if (Vector3.Distance(em.transform.position, pos) >= 1f) continue;
                    if (wantOn && !em.isOn)
                        em.enable();
                    else if (!wantOn && em.isOn)
                        em.disable();
                    break;
                }
            }
            ModLog.Event(LogCat.Session, $"[BulkSync] Hideout ovens applied count={msg.OvenCount}");
        }

        /// <summary>Send current workbench level to all clients.</summary>
        internal void SendWorkbenchLevelSync() => SendWorkbenchLevelSyncTo(-1);

        internal void SendWorkbenchLevelSyncTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;
            int level = Singleton<Controller>.Instance != null
                ? Singleton<Controller>.Instance.workbenchLevel : 1;
            _net.SendBulkOrAll(NetMessageType.WorkbenchLevelSync,
                w => new WorkbenchLevelMessage { Level = level }.Serialize(w),
                targetPlayerId);
        }

        internal void HandleWorkbenchLevelSync(WorkbenchLevelMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            _net.JournalHandlers.ApplyWorkbenchLevel(msg.Level);
        }

        /// <summary>Send map markers and discoveries to all clients.</summary>
        internal void SendMapStateSync() => SendMapStateSyncTo(-1);

        internal void SendMapStateSyncTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            // Host local markers as player 1 + all known remote markers keyed by owner.
            var positions = new List<Vector3>(64);
            var owners = new List<int>(64);

            foreach (var p in Sync.MultiplayerMapManager.LocalMarkers)
            {
                positions.Add(p);
                owners.Add(1); // host LocalPlayerId
            }
            foreach (var kvp in Sync.MultiplayerMapManager.RemoteMarkers)
            {
                int pid = kvp.Key;
                if (pid <= 0) continue;
                foreach (var p in kvp.Value)
                {
                    positions.Add(p);
                    owners.Add(pid);
                }
            }

            // Vanilla MapElement.isOnMap (Map.showElement) — late-join mirror of live msg 69.
            var discoveries = new List<string>(256);
            MapElement[] elements = WorldQueryHelper.GetCachedSceneComponents<MapElement>();
            if (elements != null)
            {
                for (int i = 0; i < elements.Length && discoveries.Count < 4096; i++)
                {
                    MapElement el = elements[i];
                    if (el == null || !el.isOnMap) continue;
                    if (string.IsNullOrEmpty(el.elementName)) continue;
                    discoveries.Add(el.elementName);
                }
            }

            int mc = Mathf.Min(positions.Count, 4096);
            int dc = discoveries.Count;
            var msg = new MapStateSyncMessage
            {
                MarkerCount = mc,
                MarkerPosX = new float[mc],
                MarkerPosY = new float[mc],
                MarkerPosZ = new float[mc],
                MarkerPlayerIds = new int[mc],
                MarkerTexts = new string[mc],
                DiscoveryCount = dc,
                DiscoveryElementNames = discoveries.ToArray()
            };
            for (int i = 0; i < mc; i++)
            {
                msg.MarkerPosX[i] = positions[i].x;
                msg.MarkerPosY[i] = positions[i].y;
                msg.MarkerPosZ[i] = positions[i].z;
                msg.MarkerPlayerIds[i] = owners[i];
                msg.MarkerTexts[i] = "";
            }
            _net.SendBulkOrAll(NetMessageType.MapStateSync, w => msg.Serialize(w), targetPlayerId);
        }

        internal void HandleMapStateSync(MapStateSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            for (int i = 0; i < msg.MarkerCount; i++)
            {
                Vector3 pos = new Vector3(msg.MarkerPosX[i], msg.MarkerPosY[i], msg.MarkerPosZ[i]);
                int pid = msg.MarkerPlayerIds != null && i < msg.MarkerPlayerIds.Length
                    ? msg.MarkerPlayerIds[i] : 1;
                if (pid <= 0 || pid == _net.LocalPlayerId) continue;
                Sync.MultiplayerMapManager.AddRemoteMarker(pid, pos);
            }
            for (int i = 0; i < msg.DiscoveryCount; i++)
            {
                string name = msg.DiscoveryElementNames?[i];
                if (!string.IsNullOrEmpty(name))
                    Sync.MultiplayerMapManager.OnRemoteElementDiscovered(name);
            }
        }

        internal void HandlePlayerSkillsSync(PlayerSkillsSyncMessage msg)
        {
            // Domain 2.10: skills/XP are intentionally per-player (not shared).
            // Nothing in the mod sends this type; handler is a permanent no-op so a
            // stale or third-party packet cannot overwrite local progression.
            // Persistence: ClientStateBackup (XP, level, chosen skills, skill points).
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo(
                    $"[SkillsSync] ignored peer XP={msg.Experience} lvl={msg.CurrentLevel} (per-player; use ClientStateBackup)");
        }
    }
}
