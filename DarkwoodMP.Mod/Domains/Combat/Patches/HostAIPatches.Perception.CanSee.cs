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
    /// Per-instance component lookups for <see cref="HostCanSeeEnemyPatch"/> (canSeeEnemy
    /// runs for every awake AI each think tick). Cleared on session reset.
    /// </summary>
    internal static class CanSeeComponentCache
    {
        private struct Entry
        {
            public Sniffer Sniffer;
            public Collider Collider;
        }

        private const int MaxEntries = 4096;
        private static readonly Dictionary<int, Entry> _byCharacter = new Dictionary<int, Entry>();
        private static readonly Dictionary<int, bool> _isProxyTarget = new Dictionary<int, bool>();
        private static int _hostPlayerId;
        private static CharBase _hostCharBase;

        internal static void Get(Character c, out Sniffer sniffer, out Collider collider)
        {
            int id = c.GetInstanceID();
            if (!_byCharacter.TryGetValue(id, out Entry e))
            {
                if (_byCharacter.Count >= MaxEntries)
                    _byCharacter.Clear();
                e = new Entry { Sniffer = c.GetComponent<Sniffer>(), Collider = c.GetComponent<Collider>() };
                _byCharacter[id] = e;
            }
            sniffer = e.Sniffer;
            collider = e.Collider;
        }

        internal static CharBase HostCharBase()
        {
            Player p = Player.Instance;
            if (p == null)
                return null;
            int id = p.GetInstanceID();
            if (id != _hostPlayerId || _hostCharBase == null)
            {
                _hostPlayerId = id;
                _hostCharBase = p.GetComponent<CharBase>();
            }
            return _hostCharBase;
        }

        internal static bool IsProxy(Transform target)
        {
            int id = target.GetInstanceID();
            if (!_isProxyTarget.TryGetValue(id, out bool isProxy))
            {
                if (_isProxyTarget.Count >= MaxEntries)
                    _isProxyTarget.Clear();
                isProxy = target.GetComponent<RemotePlayerProxy>() != null;
                _isProxyTarget[id] = isProxy;
            }
            return isProxy;
        }

        internal static void Reset()
        {
            _byCharacter.Clear();
            _isProxyTarget.Clear();
            _hostPlayerId = 0;
            _hostCharBase = null;
        }
    }

    [HarmonyPatch(typeof(Character), "canSeeEnemy")]
    public static class HostCanSeeEnemyPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Character __instance)
        {
            // Solo host (no remotes yet / all left) keeps vanilla targeting untouched.
            if (!HostPlayerIdentity.HostWithRemotes())
                return;
            if (__instance.dummy || __instance.blind || !__instance.alive)
                return;

            var net = ModRuntime.Network;
            if (net == null) return;
            CanSeeComponentCache.Get(__instance, out Sniffer entitySniffer, out Collider myCollider);

            // --- CASE 3: no or wrong target; acquire the closest player (host or proxy) ---
            // onlyAttackPlayer entities never set target on proxies via vanilla canSeeEnemy.
            // Treat host + all proxies as equal player identities: pick closest valid CharBase.
            if (__instance.aggressiveness != Aggressiveness.neutral &&
                __instance.aggressiveness != Aggressiveness.follower &&
                __instance.attacksFaction(Faction.player))
            {
                Transform closestPlayer = null;
                float closestD = float.MaxValue;

                CharBase hostCB = CanSeeComponentCache.HostCharBase();
                if (hostCB != null && !hostCB.invisible && !hostCB.ignoreMe
                    && __instance.charactersInSight.Contains(hostCB))
                {
                    float dh = Core.trueDistance(__instance.transform.position, hostCB.transform.position);
                    if (dh < closestD)
                    {
                        closestD = dh;
                        closestPlayer = hostCB.transform;
                    }
                }

                // Proxies may not be in charactersInSight yet; use geometric detection.
                if (!ProxyDistanceHelper.ProxyIsFar(__instance) && net != null)
                {
                    float acqRange = (float)__instance.farViewDistance * __instance.aniSightRangeModifier;
                    Sniffer sn = entitySniffer;
                    float sniffR = sn != null ? sn.radius : 0f;
                    if (sniffR > acqRange) acqRange = sniffR;

                    foreach (var proxy in net.GetAllProxies())
                    {
                        if (proxy == null) continue;
                        CharBase pcb = proxy.CachedCharBase;
                        if (pcb == null || !pcb.alive || pcb.invisible || pcb.ignoreMe)
                            continue;
                        float d = Core.trueDistance(__instance.transform.position, proxy.transform.position);
                        if (d > acqRange || d >= closestD) continue;

                        Vector3 to = proxy.transform.position - __instance.transform.position;
                        bool inFov = Vector3.Angle(to, __instance.transform.up) <= (float)__instance.fieldOfViewRange;
                        bool inSniff = sn != null && d < sniffR;
                        if (!inFov && !inSniff) continue;

                        bool detected = inSniff && !inFov;
                        if (!detected && inFov)
                        {
                            if (Physics.Raycast(__instance.transform.position, to, out var hit, d, 18909185))
                            {
                                if (hit.collider != null
                                    && hit.collider.GetComponentInParent<RemotePlayerProxy>() == proxy)
                                    detected = true;
                            }
                        }
                        if (!detected) continue;

                        if (!__instance.charactersInSight.Contains(pcb))
                            __instance.charactersInSight.Add(pcb);
                        closestD = d;
                        closestPlayer = proxy.transform;
                    }
                }

                if (closestPlayer != null)
                {
                    float nearR = (float)__instance.nearViewDistance * __instance.aniSightRangeModifier;
                    bool needAcquire = __instance.target == null
                        || (__instance.target != closestPlayer
                            && __instance.behaviour != Character.Behaviour.chasingTarget);
                    if (needAcquire)
                    {
                        // Vanilla: far = sight/listen; near = chase commit.
                        // Do not attackCharacter at farViewDistance (felt like aggro from too far).
                        if (closestD <= nearR)
                        {
                            __instance.canSeeEnemyNear = true;
                            __instance.canSeeEnemyFar = true;
                            __instance.attackCharacter(closestPlayer);
                        }
                        else
                        {
                            __instance.canSeeEnemyFar = true;
                            __instance.target = closestPlayer;
                            if (__instance.aggressiveness != Aggressiveness.neutral
                                && __instance.behaviour != Character.Behaviour.chasingTarget
                                && __instance.behaviour != Character.Behaviour.escaping
                                && __instance.behaviour != Character.Behaviour.running)
                                __instance.stopAndListenTo(closestPlayer.position);
                        }
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
            if (__instance.target != null && CanSeeComponentCache.IsProxy(__instance.target))
            {
                Player hostPlayer = Player.Instance;
                if (hostPlayer == null) return;

                CharBase hostCB = CanSeeComponentCache.HostCharBase();
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
                // Path B: smell detection bypasses FOV and raycast.
                else
                {
                    if (entitySniffer != null && distToHost < entitySniffer.radius)
                    {
                        __instance.charactersInSight.Add(hostCB);
                    }
                }
                return;
            }

            // Sticky: already chasing the host player; do not steal aggro to a
            // closer proxy mid-chase (dream forest spirit / any AI). Vanilla has one
            // body; CASE 2 used to retarget to whoever was nearer and pull threats
            // off the player who actually entered the woods.
            if (__instance.target != null && Player.Instance != null
                && (__instance.target == Player.Instance.transform
                    || __instance.target == Player.Instance._transform))
            {
                float stickRange = (float)__instance.farViewDistance * __instance.aniSightRangeModifier;
                if (entitySniffer != null && entitySniffer.radius > stickRange)
                    stickRange = entitySniffer.radius;
                stickRange *= 1.5f;
                float hostStickDist = Core.trueDistance(
                    __instance.transform.position, Player.Instance._transform.position);
                if (hostStickDist <= stickRange)
                    return;
            }

            // --- CASE 2: Entity is NOT yet chasing any proxy ---
            // Find the closest detectable proxy and start chasing it.
            float maxDist = (float)__instance.farViewDistance * __instance.aniSightRangeModifier;
            float sniffRadius = 0f;
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
                CharBase proxyCB = p.CachedCharBase;
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

            // Equal identity: proxy CharBase must sit in charactersInSight so
            // checkForNewEnemyCloserThanTarget can switch host ↔ client by distance.
            CharBase bestProxyCB = bestProxy.CachedCharBase;
            if (bestProxyCB != null && !__instance.charactersInSight.Contains(bestProxyCB))
                __instance.charactersInSight.Add(bestProxyCB);

            // Wake up sleeping enemies so they react to the proxy
            if (__instance.sleeping && !__instance.wakeUpOnlyManually)
                __instance.wakeup();

            __instance.canSeeEnemyFar = true;
            __instance.stopRoutine("lostEnemy", true);

            // Closest-player identity replaces the old "only target proxy if host not visible"
            // that made the client second-class whenever host was still in sight list).
            CharBase hostCharBase = CanSeeComponentCache.HostCharBase();
            bool hostVisible = hostCharBase != null && !hostCharBase.invisible && !hostCharBase.ignoreMe
                && __instance.charactersInSight.Contains(hostCharBase);
            float hostDist = hostVisible && Player.Instance != null
                ? Core.trueDistance(__instance.transform.position, Player.Instance.transform.position)
                : float.MaxValue;

            Transform preferT = bestProxyT;
            float preferDist = bestDist;
            bool preferIsProxy = true;
            if (hostVisible && hostDist < preferDist)
            {
                preferT = Player.Instance.transform;
                preferDist = hostDist;
                preferIsProxy = false;
            }

            // Flee fauna (rabbits, ravens): still flee from proxy like vanilla flees
            // from Player, but never attackCharacter. Skipping flee entirely made
            // client approach a no-op (crows stood on corpses). Do not spam:
            // only (re)issue runAway when not already escaping/running from preferT.
            if (__instance.aggressiveness == Aggressiveness.flee ||
                __instance.aggressiveness == Aggressiveness.fleeAndDespawn)
            {
                bool alreadyFleeingPrefer = __instance.target == preferT
                    && __instance.behaviour == Character.Behaviour.escaping;
                if (!alreadyFleeingPrefer)
                {
                    __instance.target = preferT;
                    __instance.canSeeEnemyFar = true;
                    if (preferDist < (float)__instance.nearViewDistance * __instance.aniSightRangeModifier)
                        __instance.canSeeEnemyNear = true;
                    if (__instance.flier != null && __instance.flier.inFlight)
                    {
                        // Still retarget flee while airborne so client scare isn't a no-op mid-flight.
                        __instance.runAway(preferT.position);
                    }
                    else
                        __instance.runAway(preferT.position);
                    if (__instance.aggressiveness == Aggressiveness.fleeAndDespawn)
                        __instance.wantToDespawn = true;
                }
                return;
            }

            if (__instance.target == null || __instance.target != preferT)
            {
                if (__instance.aggressiveness != Aggressiveness.neutral &&
                    __instance.behaviour != Character.Behaviour.chasingTarget &&
                    __instance.behaviour != Character.Behaviour.defensive &&
                    __instance.behaviour != Character.Behaviour.following &&
                    !__instance.canSeeEnemyNear &&
                    __instance.behaviour != Character.Behaviour.escaping &&
                    __instance.behaviour != Character.Behaviour.running)
                {
                    __instance.stopAndListenTo(preferT.position);
                }
                __instance.target = preferT;
            }

            if (preferDist < (float)__instance.nearViewDistance * __instance.aniSightRangeModifier)
                __instance.canSeeEnemyNear = true;

            // Skills on the detected proxy (ward / EotF), matching host ward checks on Player.
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

            if (preferIsProxy && bestProxy.RemoteHasEnemyOfTheForest
                && __instance.faction == Faction.animalAggressive
                && __instance.attacksFaction(Faction.player))
            {
                __instance.target = bestProxyT;
                __instance.canSeeEnemyFar = true;
                if (bestDist < (float)__instance.nearViewDistance * __instance.aniSightRangeModifier)
                {
                    __instance.canSeeEnemyNear = true;
                    if (__instance.behaviour != Character.Behaviour.chasingTarget)
                        __instance.attackCharacter(bestProxyT);
                }
            }
            else if (preferIsProxy
                && __instance.behaviour != Character.Behaviour.chasingTarget
                && __instance.aggressiveness != Aggressiveness.neutral
                && __instance.canSeeEnemyNear
                && __instance.attacksFaction(Faction.player))
            {
                // Commit chase like vanilla near-sight acquisition on a real Player.
                __instance.attackCharacter(preferT);
            }
        }
    }
}
