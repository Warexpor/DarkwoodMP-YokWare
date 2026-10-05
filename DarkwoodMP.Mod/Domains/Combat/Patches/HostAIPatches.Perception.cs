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
    /// Every <c>attackCharacter</c> on the host with remote players is recorded by
    /// <see cref="PlayerTargetArbiter"/> (switch timing and the <c>[TargetSwitch]</c> trace, tagged
    /// with the mod path that ran it, or "attackCharacter" for vanilla's own callers). Vanilla's
    /// body runs unchanged for a stand-in as for the host: it used to refuse a stand-in to any
    /// creature not hostile to players (a deer hit by a client never turned on him, by the host it
    /// did) and woke a sleeping creature and chased the stand-in at once where vanilla only wakes it.
    /// </summary>
    [HarmonyPatch(typeof(Character), "attackCharacter", new[] { typeof(Transform) })]
    public static class HostAttackCharacterPatch
    {
        internal struct State
        {
            public bool On;
            public Transform Before;
        }

        private static void Prefix(Character __instance, ref State __state)
        {
            __state = default;
            if (__instance == null || !HostPlayerIdentity.HostWithRemotes())
                return;
            __state.On = true;
            __state.Before = __instance.target;
        }

        private static void Postfix(Character __instance, State __state)
        {
            if (__state.On && __instance != null)
            {
                PlayerTargetArbiter.ObserveAttack(__instance, __state.Before);
                NamedNpcScalePatch.OnAttack(__instance);
            }
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

            // Vanilla: a creature that summons after escaping never re-checks the chase.
            if (__instance.summonsAfterEscaping)
                return false;

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
            if (__instance.dummy || !__instance.alive || !__instance.isActive)
                return;

            RemotePlayerProxy proxy = _collider.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null)
                return;

            // Vanilla Character.onCollideWith's Player branch, in its order, for the proxy
            // (CharBase, no Player component): track the contact; a banshee attacks; a sleeper
            // that wakes on its own wakes and does nothing else; otherwise, unless the player is
            // invisible or the creature dies on contact, reactToCharacter(null, player, false).
            // reactToCharacter owns every aggressiveness: neutral/follower/NPC ignore the bump, a
            // fleeing creature tryToRunAway()s (from its target, or straight back with none; no
            // despawn), the rest turn on that player (HostRetaliateOnAttackerPatch).
            if (!__instance.touchingColliders.Contains(_collider))
                __instance.touchingColliders.Add(_collider);

            if (__instance.banshee)
            {
                __instance.Invoke("initiateBansheeAttack", 0f);
                return;
            }

            if (__instance.sleeping && !__instance.wakeUpOnlyManually)
            {
                __instance.wakeup();
                return;
            }

            CharBase proxyCB = proxy.CachedCharBase;
            if (proxyCB == null || proxyCB.invisible || proxyCB.ignoreMe)
                return;
            if (__instance.dieOnContactWithTarget)
                return;
            ReactToCharacter(__instance, null, proxy.transform, false);
        }

        private static readonly System.Action<Character, Character, Transform, bool> ReactToCharacter =
            AccessTools.MethodDelegate<System.Action<Character, Character, Transform, bool>>(
                AccessTools.Method(typeof(Character), "reactToCharacter"));
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
