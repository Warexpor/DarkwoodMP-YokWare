using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Ensures sleeping entities wake up when the remote proxy triggers
    /// attackCharacter, since the proxy is not a real Player and vanilla
    /// attackCharacter skips wake-up for non-Player targets.
    /// </summary>
    [HarmonyPatch(typeof(Character), "attackCharacter", new[] { typeof(Transform) })]
    public static class HostAttackCharacterPatch
    {
        private static bool Prefix(Character __instance, object[] __args)
        {
            Transform destTransform = (Transform)__args[0];
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (destTransform == null)
                return false;
            if (destTransform.GetComponent<RemotePlayerProxy>() == null)
                return true;

            // Rabbits/ravens/non-predators must never chase a remote proxy.
            if (__instance.aggressiveness == Aggressiveness.flee
                || __instance.aggressiveness == Aggressiveness.fleeAndDespawn
                || !__instance.attacksFaction(Faction.player))
                return false;

            if (__instance.sleeping && !__instance.wakeUpOnlyManually)
            {
                __instance.wakeup();
                __instance.sleeping = false;
            }

            return true;
        }
    }

    /// <summary>
    /// Vanilla checkStuff culls on host distance / host isInside. Skipping the whole
    /// method (old Prefix return false) froze lure/activities/retarget while the client
    /// was still next to the NPC. Stash only the host-only cull flags so the rest runs.
    /// </summary>
    [HarmonyPatch(typeof(Character), "checkStuff")]
    public static class HostCheckStuffPatch
    {
        private struct Stash
        {
            public bool TempSpawned;
            public bool WantDespawn;
            public bool ForestSpirit;
            public bool StashedTemp;
            public bool StashedDespawn;
            public bool StashedSpirit;
        }

        private static readonly Dictionary<Character, Stash> _stash = new Dictionary<Character, Stash>();
        private const float TempKeepRange = 1400f;

        public static void Reset() => _stash.Clear();

        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(Character __instance)
        {
            if (!HostPlayerIdentity.HostWithRemotes())
                return true;
            if (__instance == null)
                return true;

            float distSq = PlayerPositionManager.SqrDistanceToNearestPlayer(__instance.transform.position);
            Player hostPlayer = Player.Instance;
            float distToHost = hostPlayer != null
                ? Core.trueDistance(hostPlayer._transform.position, __instance.transform.position)
                : float.MaxValue;

            Stash s = new Stash
            {
                TempSpawned = __instance.temporarySpawned,
                WantDespawn = __instance.wantToDespawn,
                ForestSpirit = __instance.forestSpirit
            };

            if (__instance.temporarySpawned
                && distToHost > GameplayConstants.EntityActivationRange
                && distSq <= TempKeepRange * TempKeepRange)
            {
                __instance.temporarySpawned = false;
                s.StashedTemp = true;
            }
            if (__instance.wantToDespawn
                && distToHost > 1500f
                && distSq <= 1500f * 1500f)
            {
                __instance.wantToDespawn = false;
                s.StashedDespawn = true;
            }
            if (__instance.forestSpirit
                && hostPlayer != null
                && hostPlayer.isInside
                && !HostPlayerIdentity.AllPlayersInside())
            {
                __instance.forestSpirit = false;
                s.StashedSpirit = true;
            }

            if (s.StashedTemp || s.StashedDespawn || s.StashedSpirit)
                _stash[__instance] = s;

            return true;
        }

        // Finalizer (not Postfix): if checkStuff throws after Prefix mutated
        // temporarySpawned / wantToDespawn / forestSpirit, Postfix never runs and
        // those flags stay permanently wrong (never-despawn / spirit idle). Same class
        // as NightSpawnFlagPatch / HostGridOccupancy Finalizer clears.
        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(Character __instance)
        {
            if (__instance == null)
                return;
            if (!_stash.TryGetValue(__instance, out Stash s))
                return;
            _stash.Remove(__instance);

            if (s.StashedTemp)
                __instance.temporarySpawned = s.TempSpawned;
            if (s.StashedDespawn)
                __instance.wantToDespawn = s.WantDespawn;
            if (s.StashedSpirit)
                __instance.forestSpirit = s.ForestSpirit;
        }
    }

    /// <summary>
    /// Forces Character.inSightOrCloseToPlayer to return true when the
    /// remote proxy is within 1000 units, preventing NPCs from being
    /// culled or going idle while the remote player is near.
    /// </summary>
    [HarmonyPatch(typeof(Character), "inSightOrCloseToPlayer")]
    public static class HostInSightOrCloseToPlayerPatch
    {
        private static void Postfix(Character __instance, ref bool __result)
        {
            if (__result) return;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host) return;
            if (!PlayerPositionManager.HasRemotePlayer) return;

            var net = ModRuntime.Network;
            if (net == null) return;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                float dist = Core.trueDistance(__instance.transform.position, proxy.transform.position);
                if (dist < 1000f)
                {
                    __result = true;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Redirects NPC fleeing/despawning behavior to run away from the
    /// nearest player (host or remote) instead of only the host.
    /// </summary>
    [HarmonyPatch(typeof(Character), "checkIfBeingChased")]
    public static class HostCheckIfBeingChasedPatch
    {
        private static bool Prefix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            if (ProxyDistanceHelper.ProxyIsFar(__instance))
                return true;

            if (__instance.wantToDespawn)
            {
                Vector3 nearest = PlayerPositionManager.GetNearestPlayerPosition(__instance.transform.position);
                __instance.runAway(nearest);
                return false;
            }

            if (__instance.behaviour != Character.Behaviour.escaping)
                return false;

            float sqrDist = PlayerPositionManager.SqrDistanceToNearestPlayer(__instance.transform.position);
            if (sqrDist < 500f * 500f)
            {
                Vector3 nearest = PlayerPositionManager.GetNearestPlayerPosition(__instance.transform.position);
                __instance.runAway(nearest);
            }
            return false;
        }
    }

    /// <summary>
    /// Replicates vanilla Character.onCollideWith behavior for the remote
    /// proxy, since the proxy has a CharBase but no Player component and
    /// would otherwise be ignored by vanilla collision logic.
    /// </summary>
    [HarmonyPatch(typeof(Character), "onCollideWith", new[] { typeof(Collider) })]
    public static class HostOnCollideWithProxyPatch
    {
        private static void Postfix(Character __instance, object[] __args)
        {
            Collider _collider = (Collider)__args[0];
            if (_collider == null) return;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;
            if (__instance.dummy || !__instance.alive)
                return;

            RemotePlayerProxy proxy = _collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null)
                return;

            // Replicate vanilla Player collision behavior from Character.onCollideWith,
            // adapted for the proxy (which has CharBase but no Player component).
            // Vanilla flow:
            //   1. Sleeping → wakeup + return (don't react)
            //   2. Banshee  → initiateBansheeAttack + return
            //   3. Invisible/ignoreMe → skip
            //   4. Aggressiveness.neutral/follower → ignore
            //   5. Aggressiveness.flee/fleeAndDespawn → runAway
            //   6. attackOnSight/defensive/stalker → chase

            // Track contact like a Player collision
            if (!__instance.touchingColliders.Contains(_collider))
                __instance.touchingColliders.Add(_collider);

            if (__instance.sleeping)
            {
                if (!__instance.wakeUpOnlyManually)
                {
                    __instance.wakeup();
                }
                return; // Sleeping entities wake up but don't react further
            }

            CharBase proxyCB = proxy.CachedCharBase;
            if (proxyCB == null || proxyCB.invisible || proxyCB.ignoreMe)
                return;

            if (__instance.banshee)
            {
                __instance.Invoke("initiateBansheeAttack", 0f);
                return;
            }

            switch (__instance.aggressiveness)
            {
                case Aggressiveness.neutral:
                case Aggressiveness.follower:
                    return;

                case Aggressiveness.flee:
                case Aggressiveness.fleeAndDespawn:
                    // Vanilla onCollideWith(Player) calls runAway. Proxy has no Player,
                    // restore flee-on-bump so client can scare rabbits/crows.
                    __instance.runAway(proxy.transform.position);
                    if (__instance.aggressiveness == Aggressiveness.fleeAndDespawn)
                        __instance.wantToDespawn = true;
                    return;

                default:
                    if (!__instance.attacksFaction(Faction.player))
                        return;
                    __instance.attackCharacter(proxy.transform);
                    break;
            }
        }
    }

    /// <summary>
    /// Prevents MeleeSensor from hitting the same CharBase twice within the sensor's
    /// lifetime. This fixes double-damage on the proxy (which has multiple child colliders
    /// from the player clone, each triggering OnTriggerEnter independently).
    ///
    /// Uses nameHash + Time debounce instead of MeleeSensor.GetInstanceID() to avoid
    /// Unity object-pooling reuse issues. Based on ClientCombatPatches pattern.
    /// </summary>
    [HarmonyPatch(typeof(MeleeSensor), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class MeleeSensorDeduplicatePatch
    {
        // Time-based debounce per character to prevent duplicate
        // OnTriggerEnter from multiple colliders on the same target
        // in one swing. Time.time keyed by character nameHash.
        // This avoids pooling issues with GetInstanceID().
        private const float HIT_DEBOUNCE = 0.2f;
        internal static readonly Dictionary<long, float> _lastCharHitTime = new Dictionary<long, float>();
        private static readonly List<long> _staleHitKeys = new List<long>(8); // process-scoped: scratch buffer, cleared before each use

        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(MeleeSensor __instance, object[] __args)
        {
            Collider _collider = (Collider)__args[0];
            if (_collider == null) return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;

            CharBase cb = _collider.GetComponentInParent<CharBase>();
            if (cb == null)
                return true;

            // Only the remote body has several child colliders that each fire
            // this swing. Debouncing every character was eating real second hits.
            RemotePlayerProxy proxy = _collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null)
                return true;

            Transform attacker = __instance.attackerTransform;
            int attackerInst = attacker != null ? attacker.GetInstanceID() : __instance.GetInstanceID();
            long nameHash = ((long)attackerInst << 32) | (uint)proxy.PlayerId;

            // Time-based debounce: prevent duplicate OnTriggerEnter from
            // multiple colliders on the same character in one swing.
            float now = Time.time;
            if (_lastCharHitTime.TryGetValue(nameHash, out float lastHit) &&
                now - lastHit < HIT_DEBOUNCE)
                return false;

            _lastCharHitTime[nameHash] = now;
            return true;
        }

        /// <summary>Cleanup stale entries periodically to prevent unbounded growth.</summary>
        internal static void CleanupStaleEntries()
        {
            float cutoff = Time.time - HIT_DEBOUNCE * 2f;
            _staleHitKeys.Clear();
            foreach (var kvp in _lastCharHitTime)
            {
                if (kvp.Value < cutoff)
                    _staleHitKeys.Add(kvp.Key);
            }
            for (int i = 0; i < _staleHitKeys.Count; i++)
                _lastCharHitTime.Remove(_staleHitKeys[i]);
        }

        public static void Reset()
        {
            _lastCharHitTime.Clear();
        }
    }

    /// <summary>
    /// Enters WorldGrid nodes near every remote so host AI/physics keep running
    /// in that bubble. Must cover <b>all</b> grids: vanilla
    /// <c>transportToLocation</c> switches <c>currentGrid</c> to the bunker and
    /// force-leaves World. currentGrid-only enter left forest clients on a
    /// hidden host World.
    /// </summary>
}
