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
    internal sealed partial class NightNetHandlers
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
        }

        internal void ClearShadowLookups()
        {
            _clientShadowLookups?.Clear();
            _clientDeadShadowIds.Clear();
        }

        internal void HandleShadowEvent(ShadowEventMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (Player.Instance == null) return;

            // Set the CharacterSpawner flags so the game knows shadows are active.
            // Do not call Player.tryToSpawnShadow(); it spawns shadows at the wrong local
            // positions.  The host sends individual ShadowSpawnMessages with exact positions.
            var cs = Singleton<CharacterSpawner>.Instance;
            if (cs == null)
                return;
            if (msg.End)
            {
                // Vanilla Player.endShadows. The wave's shadows die on the host (their counter
                // is the host's), so without this a client kept spawnedShadows set for the rest of
                // the session, and with it a natural-light lantern that could never be lit again.
                cs.shadowsRemove = true;
                cs.spawnedShadows = false;
                cs.shadowsPaused = false;
                ModRuntime.LegacyInfo("[ShadowSync] client: shadow wave ended");
                return;
            }
            cs.shadowsRemove = false;
            cs.shadowsPaused = false;
            // spawnedShadows is the cursed player's own state (vanilla: it keeps their natural
            // lights off). Another player's wave only needs its shadows shown here.
            if (msg.OwnerId == _net.LocalPlayerId)
            {
                cs.spawnedShadows = true;
                cs.spawnedShadowsAmount = 8;
            }

            ModRuntime.LegacyInfo("[ShadowSync] client received NightShadows event, awaiting host ShadowSpawn + ShadowStateUpdate messages");
        }

        /// <summary>
        /// Client→host: a Shadows wave for the requester only (its own vanilla tryToSpawnShadow).
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

            // The client's own vanilla tryToSpawnShadow (a Shadows night scene it replayed): vanilla
            // spawns the wave at night only, with no other check (the scene's requirements chose
            // the player). Its spawns stop at day on their own, below.
            if (Core.isDay())
            {
                ModRuntime.LegacyInfo($"[NightShadow] reject P{requesterId}: day");
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

            _net.SendShadowEvent(new ShadowEventMessage { OwnerId = (short)ownerPlayerId });

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

            // A real spawn announces a live shadow, even under an id the host recycled
            // after the previous one with that id died.
            _clientDeadShadowIds.Remove(msg.ShadowId);
            SpawnClientShadow(msg);
        }

        private void SpawnClientShadow(ShadowSpawnMessage msg)
        {
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
                // Spawned already dead (late spawn message): nothing will ever remove it otherwise.
                if (sc.dead)
                    UnityEngine.Object.Destroy(go, 1.5f);
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

        /// <summary>
        /// Shadows the host reported dead. StateUpdate is Unreliable, so a late "alive" update
        /// for one of these would otherwise find no lookup entry and respawn the dead shadow.
        /// </summary>
        private readonly HashSet<short> _clientDeadShadowIds = new HashSet<short>();

        internal void HandleShadowStateUpdate(ShadowStateUpdateMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            if (_clientShadowLookups == null) return;

            if (_clientShadowLookups.TryGetValue(msg.ShadowId, out var sc) && sc != null)
            {
                // Already dying here: a reordered alive update must not drag it around or revive it.
                if (sc.dead && (msg.Flags & 2) == 0)
                    return;

                // Update position
                sc.transform.position = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                sc.transform.rotation = Quaternion.Euler(90f, msg.RotY, 0f);
                sc.distanceToPlayer = msg.DistanceToPlayer;

                bool dead = (msg.Flags & 2) != 0;
                if (dead && !sc.dead)
                {
                    sc.dead = true;
                    float destroyAfter = 1.5f;
                    var anim = sc.GetComponent<tk2dSpriteAnimator>();
                    var clip = anim != null ? anim.GetClipByName("Death1") : null;
                    if (clip != null)
                    {
                        anim.Play(clip);
                        if (clip.fps > 0f && clip.frames != null && clip.frames.Length > 0)
                            destroyAfter = clip.frames.Length / clip.fps + 0.1f;
                    }
                    // Vanilla removes the puppet from its "WantToDie" animation event via die(), but
                    // on a client Start (which wires that listener) and die (host-owned counters) are
                    // skipped: the presentation copy is removed here once the death clip has played.
                    UnityEngine.Object.Destroy(sc.gameObject, destroyAfter);
                }

                if (dead)
                {
                    _clientShadowLookups.Remove(msg.ShadowId);
                    _clientDeadShadowIds.Add(msg.ShadowId);
                }
            }
            else
            {
                // A dead shadow stays dead: never respawn it from a late or reordered update.
                if ((msg.Flags & 2) != 0)
                {
                    _clientDeadShadowIds.Add(msg.ShadowId);
                    return;
                }
                if (_clientDeadShadowIds.Contains(msg.ShadowId))
                    return;

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
                SpawnClientShadow(spawnMsg);
            }
        }
    }
}
