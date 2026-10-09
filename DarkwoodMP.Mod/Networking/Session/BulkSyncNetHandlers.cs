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
        private bool _hasPendingHideoutState;
        private HideoutStateSyncMessage _pendingHideoutState;

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
                Dead = new bool[count],
                WantsToTalk = new bool[count],
                AttackedIds = new int[count],
                DeadIds = new int[count]
            };
            for (int i = 0; i < count; i++)
            {
                msg.NpcNames[i] = flags.npcStates[i].name;
                msg.Reputations[i] = flags.npcStates[i].reputation;
                msg.Dead[i] = flags.npcStates[i].dead;
                msg.WantsToTalk[i] = flags.npcStates[i].wantsToTalk;
                msg.AttackedIds[i] = flags.npcStates[i].attackedID;
                msg.DeadIds[i] = flags.npcStates[i].deadID;
            }
            // same portrait / anim trailers live ReputationSync already fans.
            Patches.NpcAttackedIdSync.FillBulkVisualTrailers(ref msg);
            _net.SendBulkOrAll(NetMessageType.ReputationBulkSync, w => msg.Serialize(w), targetPlayerId);
        }

        internal void HandleReputationBulkSync(ReputationBulkSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            if (msg.NpcNames == null) return;

            int visuals = 0;
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

                // Dead / deadID are world and story state; apply for all NPCs (remap local uid).
                bool dead = msg.Dead != null && i < msg.Dead.Length && msg.Dead[i];
                int deadId = msg.DeadIds != null && i < msg.DeadIds.Length ? msg.DeadIds[i] : 0;
                Patches.NpcAttackedIdSync.ApplyDead(state, name, dead, deadId);

                // wantsToTalk gates NPC.talkTo() — host truth on late-join / soft reconnect
                // (peer SP save can keep false while host re-enabled Doctor/Wolf story talk).
                if (msg.WantsToTalk != null && i < msg.WantsToTalk.Length)
                    state.wantsToTalk = msg.WantsToTalk[i];

                // attackedID: wolfman despawn-on-death + onlyOneInstance dedup (remap local uid).
                if (msg.AttackedIds != null && i < msg.AttackedIds.Length)
                    Patches.NpcAttackedIdSync.ApplyAttackedId(state, name, msg.AttackedIds[i]);

                visuals += Patches.NpcAttackedIdSync.ApplyBulkVisualTrailers(msg, i, name);

                // Never overwrite morning-trader standing with host bulk.
                if (Patches.ReputationSyncUtil.IsPerPlayerReputationNpcName(name))
                    continue;

                if (msg.Reputations != null && i < msg.Reputations.Length)
                    state.reputation = msg.Reputations[i];
            }
            ModLog.Event(LogCat.Session,
                $"[BulkSync] Reputation bulk applied ({msg.NpcCount} entries, " +
                $"wantsToTalk+dead+deadID+attackedID+visuals={visuals}, night-traders skipped for rep)");
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

            if (!LanNetworkManager.ClientCanApplyWorldBulk())
            {
                _hasPendingHideoutState = true;
                _pendingHideoutState = msg;
                ModLog.Event(LogCat.Session, "[BulkSync] Hideout ovens queued (not in-world yet)");
                return;
            }

            ApplyHideoutStateSync(msg);
        }

        internal void ApplyHideoutStateSync(HideoutStateSyncMessage msg)
        {
            if (msg.OvenCount <= 0 || msg.PosX == null) return;

            var machines = WorldQueryHelper.GetCachedSceneComponents<ExperienceMachine>();
            if (machines == null || machines.Length == 0)
            {
                _hasPendingHideoutState = true;
                _pendingHideoutState = msg;
                return;
            }

            // Vanilla enable() also makes the oven the local player's home: lighting every lit oven
            // of the world left a joiner living in whichever came last.
            Player local = Player.Instance;
            ExperienceMachine ownHome = local != null ? local.experienceMachine : null;
            int applied = 0;
            var unmatched = new System.Collections.Generic.List<int>();
            for (int i = 0; i < msg.OvenCount; i++)
            {
                Vector3 pos = new Vector3(msg.PosX[i], msg.PosY[i], msg.PosZ[i]);
                bool wantOn = msg.IsOn != null && i < msg.IsOn.Length && msg.IsOn[i];
                bool matched = false;
                for (int j = 0; j < machines.Length; j++)
                {
                    var em = machines[j];
                    if (em == null) continue;
                    if (Vector3.Distance(em.transform.position, pos) >= 1.5f) continue;
                    if (wantOn && !em.isOn)
                        em.enable();
                    else if (!wantOn && em.isOn && em != ownHome)
                        em.disable();
                    applied++;
                    matched = true;
                    break;
                }
                if (!matched)
                    unmatched.Add(i);
            }
            // Own home is never put out above. Unlit here means not examined yet (vanilla's new
            // game oven only glows until its first examine lights it and offers Cook); relighting
            // it gave a fresh world Cook from the start, on every peer.
            if (local != null)
                local.experienceMachine = ownHome;
            // Keep only the ovens not matched yet (a pad not spawned here). Keeping the whole
            // snapshot replayed it every frame over ovens that had changed since, undoing a
            // relit oven and broadcasting that.
            if (applied < msg.OvenCount)
            {
                _hasPendingHideoutState = true;
                _pendingHideoutState = new HideoutStateSyncMessage
                {
                    OvenCount = unmatched.Count,
                    PosX = new float[unmatched.Count],
                    PosY = new float[unmatched.Count],
                    PosZ = new float[unmatched.Count],
                    IsOn = new bool[unmatched.Count]
                };
                for (int k = 0; k < unmatched.Count; k++)
                {
                    int i = unmatched[k];
                    _pendingHideoutState.PosX[k] = msg.PosX[i];
                    _pendingHideoutState.PosY[k] = msg.PosY[i];
                    _pendingHideoutState.PosZ[k] = msg.PosZ[i];
                    _pendingHideoutState.IsOn[k] = msg.IsOn != null && i < msg.IsOn.Length && msg.IsOn[i];
                }
                if (applied > 0)
                    ModLog.Event(LogCat.Session,
                        $"[BulkSync] Hideout ovens partial matched={applied}/{msg.OvenCount} — keep pending");
                return;
            }
            _hasPendingHideoutState = false;
            _pendingHideoutState = default;
            ModLog.Event(LogCat.Session,
                $"[BulkSync] Hideout ovens applied count={msg.OvenCount} matched={applied}");
        }

        private float _nextHideoutFlush;

        internal void TryFlushPendingHideoutState()
        {
            if (!_hasPendingHideoutState) return;
            if (_net.Role != NetworkRole.Client) return;
            if (Time.unscaledTime < _nextHideoutFlush) return;
            _nextHideoutFlush = Time.unscaledTime + 1f;
            if (!LanNetworkManager.ClientCanApplyWorldBulk()) return;
            var pending = _pendingHideoutState;
            _hasPendingHideoutState = false;
            _pendingHideoutState = default;
            // Out of the receive scope here: an oven this lights or puts out is the host's state,
            // not this peer's own change to broadcast.
            using (new NetworkApplyGuard())
                ApplyHideoutStateSync(pending);
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

        /// <summary>Send the party map board and map discoveries to all clients.</summary>
        internal void SendMapStateSync() => SendMapStateSyncTo(-1);

        internal void SendMapStateSyncTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            MapPinWire[] pins = Sync.MapPinBoard.Snapshot();

            // Vanilla MapElement.isOnMap (Map.showElement) — late-join mirror of live msg 69.
            var discoveries = new List<string>(256);
            // Vanilla's own map element lists, not a MapElement scene scan (~40 ms in the bulk).
            List<MapElement> elements = Sync.MultiplayerMapManager.CollectMapElements();
            for (int i = 0; i < elements.Count && discoveries.Count < 4096; i++)
            {
                MapElement el = elements[i];
                if (el == null || !el.isOnMap) continue;
                if (string.IsNullOrEmpty(el.elementName)) continue;
                discoveries.Add(el.elementName);
            }

            int dc = discoveries.Count;
            var msg = new MapStateSyncMessage
            {
                PinCount = Mathf.Min(pins.Length, 4096),
                Pins = pins,
                DiscoveryCount = dc,
                DiscoveryElementNames = discoveries.ToArray()
            };
            _net.SendBulkOrAll(NetMessageType.MapStateSync, w => msg.Serialize(w), targetPlayerId);
        }

        internal void HandleMapStateSync(MapStateSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            // Full snapshot: the host's board replaces this one (a soft-reconnect resend cannot stack pins).
            Sync.MapPinBoard.ApplySnapshot(msg.Pins, msg.PinCount);
            // Discoveries: apply when in-world; otherwise queue (MapElements may not exist yet).
            // OnRemoteElementDiscovered also queues when the named MapElement is missing /
            // still OutsideLocation-bound via the old string path.
            bool canApply = LanNetworkManager.ClientCanApplyWorldBulk();
            for (int i = 0; i < msg.DiscoveryCount; i++)
            {
                string name = msg.DiscoveryElementNames?[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (!canApply)
                    Sync.MultiplayerMapManager.QueuePendingDiscovery(name);
                else
                    Sync.MultiplayerMapManager.OnRemoteElementDiscovered(name);
            }
            if (!canApply && msg.DiscoveryCount > 0)
                ModLog.Event(LogCat.Session,
                    $"[BulkSync] Map discoveries queued count={msg.DiscoveryCount} (not in-world yet)");
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
