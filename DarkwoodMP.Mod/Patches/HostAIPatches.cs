using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    internal static class ProxyDistanceHelper
    {
        internal static bool ProxyIsFar(Character c)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !PlayerPositionManager.HasRemotePlayer)
                return true;
            float range = (float)c.farViewDistance * c.aniSightRangeModifier;
            Sniffer sniffer = c.GetComponent<Sniffer>();
            if (sniffer != null && sniffer.radius > range)
                range = sniffer.radius;
            float threshold = range + 50f;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy != null)
                {
                    float dist = (c.transform.position - proxy.transform.position).magnitude;
                    if (dist <= threshold)
                        return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Augments Character.canSeeEnemy on the host so NPCs react to both
    /// the host player and the remote proxy for detection, targeting,
    /// and fear/ward effects.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Character), "canSeeEnemy")]
    public static class HostCanSeeEnemyPatch
    {
        private static void Postfix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (__instance.dummy || __instance.blind || !__instance.alive)
                return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            // --- CASE 3: Entity has no target but host is visible ---
            // Must run BEFORE the ProxyDistanceHelper guard so host-target
            // acquisition happens even when the proxy has fled. Handles the
            // onlyAttackPlayer=true case where canSeeEnemy can't set target.
            if (__instance.aggressiveness != Aggressiveness.neutral &&
                __instance.aggressiveness != Aggressiveness.follower &&
                __instance.attacksFaction(Faction.player))
            {
                CharBase hostCB = Player.Instance?.GetComponent<CharBase>();
                if (hostCB != null && __instance.charactersInSight.Contains(hostCB) && !hostCB.invisible && !hostCB.ignoreMe)
                {
                    bool acquiringHost = __instance.target == null ||
                        (__instance.target != hostCB.transform && __instance.behaviour != Character.Behaviour.chasingTarget);
                    if (acquiringHost)
                    {
                        __instance.attackCharacter(hostCB.transform);
                    }
                }
            }

            // Don't modify entity behavior for proxy-specific cases when no
            // proxy is within detection range.
            if (ProxyDistanceHelper.ProxyIsFar(__instance))
                return;

            // --- CASE 1: Entity is already chasing a proxy ---
            // Check if the host is detectable and add to charactersInSight
            // so checkForNewEnemyCloserThanTarget can switch to the closer player.
            if (__instance.target != null && __instance.target.GetComponent<RemotePlayerProxy>() != null)
            {
                Player hostPlayer = Player.Instance;
                if (hostPlayer == null) return;

                CharBase hostCB = hostPlayer.GetComponent<CharBase>();
                if (hostCB == null || hostCB.invisible || hostCB.ignoreMe) return;
                if (__instance.charactersInSight.Contains(hostCB)) return;

                Vector3 toHost = hostPlayer.transform.position - __instance.transform.position;
                float distToHost = toHost.magnitude;

                // Path A: visual detection with FOV
                if (distToHost <= (float)__instance.farViewDistance * __instance.aniSightRangeModifier &&
                    Vector3.Angle(toHost, __instance.transform.up) <= (float)__instance.fieldOfViewRange)
                {
                    if (Physics.Raycast(__instance.transform.position, toHost, out var hostHit, distToHost, 18909185))
                    {
                        if (hostHit.collider.GetComponentInParent<Player>() != null)
                        {
                            __instance.charactersInSight.Add(hostCB);
                            __instance.canSeeEnemyFar = true;
                            if (distToHost < (float)__instance.nearViewDistance * __instance.aniSightRangeModifier)
                                __instance.canSeeEnemyNear = true;
                        }
                    }
                }
                // Path B: smell detection — bypass FOV and raycast
                else
                {
                    Sniffer sniffer = __instance.GetComponent<Sniffer>();
                    if (sniffer != null && distToHost < sniffer.radius)
                    {
                        __instance.charactersInSight.Add(hostCB);
                    }
                }
                return;
            }

            // --- CASE 2: Entity is NOT yet chasing any proxy ---
            // Find the closest detectable proxy and start chasing it.
            float maxDist = (float)__instance.farViewDistance * __instance.aniSightRangeModifier;
            float sniffRadius = 0f;
            var entitySniffer = __instance.GetComponent<Sniffer>();
            if (entitySniffer != null)
                sniffRadius = entitySniffer.radius;
            if (sniffRadius > maxDist)
                maxDist = sniffRadius;

            RemotePlayerProxy bestProxy = null;
            Transform bestProxyT = null;
            float bestDist = float.MaxValue;

            foreach (var p in net.GetAllProxies())
            {
                if (p == null) continue;
                Transform pt = p.transform;
                Vector3 toRemote = pt.position - __instance.transform.position;
                float dist = toRemote.magnitude;
                if (dist > maxDist) continue;

                bool inFOV = Vector3.Angle(toRemote, __instance.transform.up) <= (float)__instance.fieldOfViewRange;
                bool inSniffRange = entitySniffer != null && dist < sniffRadius;
                if (!inFOV && !inSniffRange) continue;

                // Don't redirect neutral entities
                if (__instance.aggressiveness == Aggressiveness.neutral)
                    continue;

                // Detect by line-of-sight (FOV + raycast) or by smell (direct)
                bool detected = false;
                if (inSniffRange && !inFOV)
                {
                    detected = true; // smell detection — no line-of-sight needed
                }
                else
                {
                    Collider myCollider = __instance.GetComponent<Collider>();
                    if (Physics.Raycast(__instance.transform.position, toRemote, out var hit, dist, 18909185))
                    {
                        if (hit.collider != null && (myCollider == null || hit.collider != myCollider))
                        {
                            RemotePlayerProxy hitProxy = hit.collider.GetComponentInParent<RemotePlayerProxy>();
                            if (hitProxy != null && hitProxy == p)
                                detected = true;
                        }
                    }
                }

                if (!detected) continue;

                // Respect invisible/ignoreMe flags
                CharBase proxyCB = pt.GetComponent<CharBase>();
                if (proxyCB != null && (proxyCB.invisible || proxyCB.ignoreMe))
                    continue;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestProxy = p;
                    bestProxyT = pt;
                }
            }

            if (bestProxy == null)
                return;

            // Wake up sleeping enemies so they react to the proxy
            if (__instance.sleeping && !__instance.wakeUpOnlyManually)
                __instance.wakeup();

            __instance.canSeeEnemyFar = true;
            __instance.stopRoutine("lostEnemy", true);

            // Only set target to proxy when host is not visible
            CharBase hostCharBase = Player.Instance?.GetComponent<CharBase>();
            bool hostVisible = hostCharBase != null && __instance.charactersInSight.Contains(hostCharBase);

            if (!hostVisible)
            {
                // Flee animals: flee from nearest player (host or proxy), don't override target
                if (__instance.aggressiveness == Aggressiveness.flee ||
                    __instance.aggressiveness == Aggressiveness.fleeAndDespawn)
                {
                    if (bestProxy != null)
                    {
                        Vector3 nearest = PlayerPositionManager.GetNearestPlayerPosition(__instance.transform.position);
                        __instance.runAway(nearest);
                        if (__instance.aggressiveness == Aggressiveness.fleeAndDespawn)
                            __instance.wantToDespawn = true;
                    }
                    return;
                }

                if (__instance.target == null || __instance.target != bestProxyT)
                {
                    if (__instance.aggressiveness != Aggressiveness.neutral &&
                        __instance.behaviour != Character.Behaviour.chasingTarget &&
                        __instance.behaviour != Character.Behaviour.defensive &&
                        __instance.behaviour != Character.Behaviour.following &&
                        !__instance.canSeeEnemyNear &&
                        __instance.behaviour != Character.Behaviour.escaping &&
                        __instance.behaviour != Character.Behaviour.running)
                    {
                        __instance.stopAndListenTo(bestProxyT.position);
                    }
                    __instance.target = bestProxyT;
                }

                if (bestDist < (float)__instance.nearViewDistance * __instance.aniSightRangeModifier)
                {
                    __instance.canSeeEnemyNear = true;
                }
            }

            // Remote player effect checks (shadowWard, forestSpiritWard, EnemyOfTheForest)
            if (!bestProxy.RemoteHasEnemyOfTheForest)
            {
                if (__instance.afraidOfHideout && bestProxy.RemoteHasShadowWard)
                {
                    __instance.runAway(bestProxyT.position);
                    __instance.wantToDespawn = true;
                }
                if (__instance.afraidOfForestSpiritWard && bestProxy.RemoteHasForestSpiritWard)
                {
                    __instance.runAway(bestProxyT.position);
                    __instance.blind = true;
                }
            }

            if (bestProxy.RemoteHasEnemyOfTheForest && __instance.faction == Faction.animalAggressive)
            {
                __instance.target = bestProxyT;
                __instance.canSeeEnemyFar = true;
                if (bestDist < (float)__instance.nearViewDistance * __instance.aniSightRangeModifier)
                    __instance.canSeeEnemyNear = true;
                if (__instance.behaviour != Character.Behaviour.chasingTarget)
                    __instance.attackCharacter(bestProxyT);
            }
        }
    }

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

            if (__instance.sleeping && !__instance.wakeUpOnlyManually)
            {
                __instance.wakeup();
                __instance.sleeping = false;
            }

            return true;
        }
    }

    /// <summary>
    /// Prevents despawning NPCs when the host is far away if the remote
    /// player is still close, keeping the entity alive for multiplayer.
    /// Re-checks distances in Postfix to clean up if both players leave range.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Character), "checkStuff")]
    public static class HostCheckStuffPatch
    {
        private static readonly HashSet<Character> _suppressed = new HashSet<Character>();

        public static void Reset() => _suppressed.Clear();

        private static bool Prefix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            float distSq = PlayerPositionManager.SqrDistanceToNearestPlayer(__instance.transform.position);
            Player hostPlayer = Player.Instance;
            float distToHost = hostPlayer != null
                ? Core.trueDistance(hostPlayer._transform.position, __instance.transform.position)
                : float.MaxValue;

            bool vanillaWouldRemove = false;
            if (__instance.temporarySpawned && distToHost > 3500f)
                vanillaWouldRemove = true;
            if (__instance.wantToDespawn && distToHost > 1500f)
                vanillaWouldRemove = true;

            if (!vanillaWouldRemove)
                return true;

            // Vanilla would remove because host is far;
            // keep alive if nearest player (host or remote) is close enough
            bool keepAlive = false;
            if (__instance.temporarySpawned && distSq <= 3500f * 3500f)
                keepAlive = true;
            if (__instance.wantToDespawn && distSq <= 1500f * 1500f)
                keepAlive = true;

            if (keepAlive)
            {
                _suppressed.Add(__instance);
                return false;
            }

            return true;
        }

        private static void Postfix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;

            if (!_suppressed.Remove(__instance))
                return;

            float distSq = PlayerPositionManager.SqrDistanceToNearestPlayer(__instance.transform.position);

            bool removed = false;
            if (__instance.temporarySpawned && distSq > 3500f * 3500f)
            {
                __instance.removeMe();
                removed = true;
            }
            if (!removed && __instance.wantToDespawn && distSq > 1500f * 1500f)
            {
                __instance.removeMe();
            }
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

            var net = LanNetworkManager.Instance;
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

            CharBase proxyCB = proxy.GetComponent<CharBase>();
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
                    __instance.runAway(proxy.transform.position);
                    return;

                default:
                    __instance.attackCharacter(proxy.transform);
                    break;
            }
        }
    }

    /// <summary>
    /// When an NPC starts chasing the remote proxy, registers it in the
    /// host player's charactersAttackingMe list so the host's UI/audio
    /// combat indicators trigger correctly.
    /// </summary>
    [HarmonyPatch(typeof(Character), "setBehaviour")]
    public static class HostSetBehaviourPatch
    {
        private static void Postfix(Character __instance, Character.Behaviour targetBehaviour)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host) return;
            if (!PlayerPositionManager.HasRemotePlayer) return;
            if (targetBehaviour != Character.Behaviour.chasingTarget) return;
            if (__instance.target == null) return;
            if (__instance.target == Player.Instance?.transform) return;
            if (__instance.target.GetComponent<RemotePlayerProxy>() == null) return;

            Player player = Player.Instance;
            if (player == null) return;

            bool alreadyAdded = false;
            for (int i = 0; i < player.charactersAttackingMe.Count; i++)
            {
                if (player.charactersAttackingMe[i] == __instance)
                {
                    alreadyAdded = true;
                    break;
                }
            }
            if (!alreadyAdded)
            {
                player.charactersAttackingMe.Add(__instance);
                player.checkInCombatChars();
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
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(MeleeSensor), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class MeleeSensorDeduplicatePatch
    {
        // Time-based debounce per character to prevent duplicate
        // OnTriggerEnter from multiple colliders on the same target
        // in one swing. Time.time keyed by character nameHash.
        // This avoids pooling issues with GetInstanceID().
        private const float HIT_DEBOUNCE = 0.2f;
        internal static readonly Dictionary<short, float> _lastCharHitTime = new Dictionary<short, float>();

        private static bool Prefix(MeleeSensor __instance, object[] __args)
        {
            Collider _collider = (Collider)__args[0];
            if (_collider == null) return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;

            CharBase cb = _collider.GetComponentInParent<CharBase>();
            if (cb == null)
                return true;

            Character c = cb.GetComponent<Character>();
            if (c == null)
                return true;

            short nameHash = Sync.CharacterTracker.GetStableId(c);

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
            var stale = new List<short>();
            foreach (var kvp in _lastCharHitTime)
            {
                if (kvp.Value < cutoff)
                    stale.Add(kvp.Key);
            }
            foreach (var key in stale)
                _lastCharHitTime.Remove(key);
        }

        public static void Reset()
        {
            _lastCharHitTime.Clear();
        }
    }

    /// <summary>
    /// Enters WorldGrid nodes near the remote proxy so entities in those
    /// chunks become active on the host. This Postfix runs after refreshNodes
    /// finishes, so the leave blocker (WorldGridNodeLeavePatch) must also be
    /// active to prevent deactivation during the coroutine.
    /// </summary>
    [HarmonyPatch(typeof(WorldGrid), "refreshPosition")]
    public static class HostWorldGridProxyCullPatch
    {
        private static void Postfix(WorldGrid __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;

            float activationRange = 3500f;
            if (__instance.currentGrid == null) return;
            var nodes = __instance.currentGrid.nodes;

            foreach (Vector3 proxyPos in PlayerPositionManager.GetAllRemotePositions())
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    Vector2 np = nodes[i].position;
                    bool proxyNear = Mathf.Abs(proxyPos.x - np.x) <= activationRange
                                  && Mathf.Abs(proxyPos.z - np.y) <= activationRange;
                    if (proxyNear)
                        nodes[i].enter(true);
                }
            }
        }
    }

    /// <summary>
    /// Prevents WorldGridNode.leave() from deactivating nodes near the remote
    /// proxy, so entities near the client player remain active on the host for
    /// AI, physics, and damage processing. Without this, the refreshNodes
    /// coroutine (run every 0.4-0.6s) deactivates all nodes outside the host's
    /// screen-size range, including proxy-near nodes, causing them to be
    /// inactive for the entire coroutine duration (~100+ frames).
    /// </summary>
    [HarmonyPatch(typeof(WorldGrid.Node), "leave", new[] { typeof(bool) })]
    public static class WorldGridNodeLeavePatch
    {
        private static bool Prefix(WorldGrid.Node __instance, object[] __args)
        {
            bool force = (bool)__args[0];
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;
            if (force)
                return true;

            Vector2 np = __instance.position;
            float activationRange = 3500f;

            foreach (Vector3 proxyPos in PlayerPositionManager.GetAllRemotePositions())
            {
                bool proxyNear = Mathf.Abs(proxyPos.x - np.x) <= activationRange
                              && Mathf.Abs(proxyPos.z - np.y) <= activationRange;
                if (proxyNear)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// After forceAttackClosestCharacter runs, if the entity fell through to
    /// attackPlayer() because the proxy has no Character component, redirect
    /// to the proxy if it's within detection range.
    /// </summary>
    [HarmonyPatch(typeof(Character), "forceAttackClosestCharacter")]
    public static class HostForceAttackClosestCharacterPatch
    {
        private static void Postfix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;

            Player hostPlayer = Player.Instance;
            if (hostPlayer == null) return;

            // Only redirect if entity fell through to attackPlayer()
            if (__instance.target != hostPlayer.transform)
                return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            float range = (float)__instance.farViewDistance * __instance.aniSightRangeModifier;
            Sniffer sniffer = __instance.GetComponent<Sniffer>();
            if (sniffer != null && sniffer.radius > range)
                range = sniffer.radius;

            // Find the closest detectable proxy
            Transform closestProxy = null;
            float closestDist = float.MaxValue;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                Transform pt = proxy.transform;
                float distToProxy = (__instance.transform.position - pt.position).sqrMagnitude;
                if (distToProxy > range * range)
                    continue;
                CharBase proxyCB = pt.GetComponent<CharBase>();
                if (proxyCB == null || proxyCB.invisible || proxyCB.ignoreMe)
                    continue;
                if (distToProxy < closestDist)
                {
                    closestDist = distToProxy;
                    closestProxy = pt;
                }
            }

            if (closestProxy != null)
                __instance.attackCharacter(closestProxy);
        }
    }
}
