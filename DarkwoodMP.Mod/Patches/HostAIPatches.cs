using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
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
    /// Host-side "the player" identity: vanilla Character keys off Player.Instance;
    /// co-op treats host + living proxies as equal bodies.
    /// </summary>
    internal static class HostPlayerIdentity
    {
        internal static bool HostWithRemotes()
        {
            return ModRuntime.Network != null
                && ModRuntime.Network.Role == NetworkRole.Host
                && PlayerPositionManager.HasRemotePlayer;
        }

        internal static Transform NearestLiving(Vector3 from)
        {
            return HostAttackPlayerNearestPatch.FindNearestPlayerTransform(from);
        }

        internal static GameObject NearestLivingGo(Vector3 from)
        {
            Transform t = NearestLiving(from);
            if (t != null)
                return t.gameObject;
            return Player.Instance != null ? Player.Instance.gameObject : null;
        }

        /// <summary>
        /// Vanilla Player.isInSight, plus proxy facing + Core.canSee (same 800u / FOV rule).
        /// </summary>
        internal static bool AnyInSight(Transform dest, bool canBeFarAway)
        {
            if (dest == null)
                return false;
            if (Player.Instance != null && Player.Instance.isInSight(dest, canBeFarAway))
                return true;

            var net = LanNetworkManager.Instance;
            if (net == null)
                return false;

            float halfFov = 55f;
            if (Player.Instance != null && Player.Instance.FOVLogic != null)
                halfFov = Player.Instance.FOVLogic.LightConeAngle / 2f;
            float lightR = 0f;
            if (Player.Instance != null && Player.Instance.FOVDot != null)
                lightR = Player.Instance.FOVDot.LightRadius;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                Transform t = proxy.transform;
                Vector3 destPos = dest.position;
                float num = Core.trueDistance(t.position, destPos);
                if (num >= 800f && !canBeFarAway)
                    continue;
                Vector3 vec = destPos - t.position;
                if (Vector3.Angle(vec, t.up) >= halfFov && num >= lightR)
                    continue;
                if (Core.canSee(t, dest))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Forest-spirit indoor cull: despawn only when every living player is inside.
        /// Proxy CharBase.isInside is refreshed via checkGround (proxy has no CharacterSounds tick).
        /// </summary>
        internal static bool AllPlayersInside()
        {
            if (Player.Instance != null && !Player.Instance.isInside)
                return false;

            var net = LanNetworkManager.Instance;
            if (net == null)
                return Player.Instance != null && Player.Instance.isInside;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.GetComponent<CharBase>();
                if (cb == null)
                    continue;
                cb.checkGround();
                if (!cb.isInside)
                    return false;
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

            // --- CASE 3: no/wrong target — acquire closest player (host OR any proxy) ---
            // onlyAttackPlayer entities never set target on proxies via vanilla canSeeEnemy.
            // Treat host + all proxies as equal player identities: pick closest valid CharBase.
            if (__instance.aggressiveness != Aggressiveness.neutral &&
                __instance.aggressiveness != Aggressiveness.follower &&
                __instance.attacksFaction(Faction.player))
            {
                Transform closestPlayer = null;
                float closestD = float.MaxValue;

                CharBase hostCB = Player.Instance?.GetComponent<CharBase>();
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

                // Proxies may not be in charactersInSight yet — use geometric detection.
                if (!ProxyDistanceHelper.ProxyIsFar(__instance) && net != null)
                {
                    float acqRange = (float)__instance.farViewDistance * __instance.aniSightRangeModifier;
                    Sniffer sn = __instance.GetComponent<Sniffer>();
                    float sniffR = sn != null ? sn.radius : 0f;
                    if (sniffR > acqRange) acqRange = sniffR;

                    foreach (var proxy in net.GetAllProxies())
                    {
                        if (proxy == null) continue;
                        CharBase pcb = proxy.GetComponent<CharBase>();
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

            // Sticky: already chasing the host player — do NOT steal aggro to a
            // closer proxy mid-chase (dream forest spirit / any AI). Vanilla has one
            // body; CASE 2 used to retarget to whoever was nearer and pull threats
            // off the player who actually entered the woods.
            if (__instance.target != null && Player.Instance != null
                && (__instance.target == Player.Instance.transform
                    || __instance.target == Player.Instance._transform))
            {
                float stickRange = (float)__instance.farViewDistance * __instance.aniSightRangeModifier;
                Sniffer stickSniff = __instance.GetComponent<Sniffer>();
                if (stickSniff != null && stickSniff.radius > stickRange)
                    stickRange = stickSniff.radius;
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

            // Equal identity: proxy CharBase must sit in charactersInSight so
            // checkForNewEnemyCloserThanTarget can switch host ↔ client by distance.
            CharBase bestProxyCB = bestProxyT.GetComponent<CharBase>();
            if (bestProxyCB != null && !__instance.charactersInSight.Contains(bestProxyCB))
                __instance.charactersInSight.Add(bestProxyCB);

            // Wake up sleeping enemies so they react to the proxy
            if (__instance.sleeping && !__instance.wakeUpOnlyManually)
                __instance.wakeup();

            __instance.canSeeEnemyFar = true;
            __instance.stopRoutine("lostEnemy", true);

            // Closest-player identity (was: "only target proxy if host not visible" —
            // that made the client second-class whenever host was still in sight list).
            CharBase hostCharBase = Player.Instance?.GetComponent<CharBase>();
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
            // from Player — but never attackCharacter. Skipping flee entirely made
            // client approach a no-op (crows stood on corpses). Do not spam:
            // only (re)issue runAway when not already escaping/running from preferT.
            if (__instance.aggressiveness == Aggressiveness.flee ||
                __instance.aggressiveness == Aggressiveness.fleeAndDespawn)
            {
                bool alreadyFleeingPrefer = __instance.target == preferT
                    && (__instance.behaviour == Character.Behaviour.escaping
                        || __instance.behaviour == Character.Behaviour.running);
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

            // Skills on the detected proxy (ward / EotF) — same as host ward checks on Player.
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
    [HarmonyPriority(Priority.Last)]
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

        private static void Postfix(Character __instance)
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
                    // Vanilla onCollideWith(Player) → runAway. Proxy has no Player —
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
    /// Enters WorldGrid nodes near every remote so host AI/physics keep running
    /// in that bubble. Must cover <b>all</b> grids: vanilla
    /// <c>transportToLocation</c> switches <c>currentGrid</c> to the bunker and
    /// force-leaves World — currentGrid-only enter left forest clients on a
    /// hidden host World.
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
            if (__instance == null)
                return;

            float activationRange = GameplayConstants.EntityActivationRange;
            if (__instance.grids != null)
            {
                for (int g = 0; g < __instance.grids.Count; g++)
                    EnterNodesNearRemotes(__instance.grids[g], activationRange);
            }
            else
                EnterNodesNearRemotes(__instance.currentGrid, activationRange);
        }

        internal static void EnterNodesNearRemotes(WorldGrid.Grid grid, float activationRange)
        {
            if (grid == null || grid.nodes == null) return;
            var nodes = grid.nodes;
            foreach (Vector3 proxyPos in PlayerPositionManager.GetAllRemotePositions())
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    Vector2 np = nodes[i].position;
                    if (CoopWorldPresencePolicy.ShouldKeepNodeForRemote(true,
                        Mathf.Abs(proxyPos.x - np.x) <= activationRange
                        && Mathf.Abs(proxyPos.z - np.y) <= activationRange))
                        nodes[i].enter(true);
                }
            }
        }
    }

    /// <summary>
    /// Prevents WorldGridNode.leave() from deactivating nodes near a remote.
    /// Vanilla location transport calls <c>Grid.leave()</c> → every node
    /// <c>leave(force: true)</c>. The old force bypass wiped the forest while
    /// the host was in an outside location.
    /// </summary>
    [HarmonyPatch(typeof(WorldGrid.Node), "leave", new[] { typeof(bool) })]
    public static class WorldGridNodeLeavePatch
    {
        private static bool Prefix(WorldGrid.Node __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            Vector2 np = __instance.position;
            float activationRange = GameplayConstants.EntityActivationRange;

            foreach (Vector3 proxyPos in PlayerPositionManager.GetAllRemotePositions())
            {
                bool proxyNear = Mathf.Abs(proxyPos.x - np.x) <= activationRange
                              && Mathf.Abs(proxyPos.z - np.y) <= activationRange;
                if (CoopWorldPresencePolicy.ShouldKeepNodeForRemote(true, proxyNear))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Host <c>getNode</c> / register paths key off <c>currentGrid</c> only.
    /// While the host is in a bunker, forest spawns near a client would bind to
    /// the bunker grid. Swap currentGrid to the grid that actually contains the
    /// position for the duration of the call.
    /// </summary>
    internal static class HostGridOccupancy
    {
        [System.ThreadStatic]
        private static int _depth;
        [System.ThreadStatic]
        private static WorldGrid.Grid _saved;

        internal static void PushOccupyingGrid(WorldGrid wg, Vector3 pos, bool search = true)
        {
            if (_depth++ > 0) return;
            if (!search || wg == null) return;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;

            WorldGrid.Grid found = FindGridContaining(wg, pos);
            if (found != null && found != wg.currentGrid)
            {
                _saved = wg.currentGrid;
                wg.currentGrid = found;
            }
        }

        internal static void PopOccupyingGrid(WorldGrid wg)
        {
            if (_depth <= 0) return;
            _depth--;
            if (_depth == 0 && _saved != null && wg != null)
            {
                wg.currentGrid = _saved;
                _saved = null;
            }
        }

        internal static WorldGrid.Grid FindGridContaining(WorldGrid wg, Vector3 pos)
        {
            if (wg == null || wg.grids == null) return null;
            if (GridContains(wg, wg.currentGrid, pos))
                return wg.currentGrid;
            for (int i = 0; i < wg.grids.Count; i++)
            {
                WorldGrid.Grid g = wg.grids[i];
                if (g == null || g == wg.currentGrid) continue;
                if (GridContains(wg, g, pos))
                    return g;
            }
            return null;
        }

        private static bool GridContains(WorldGrid wg, WorldGrid.Grid g, Vector3 pos)
        {
            if (wg == null || g == null || g.nodes == null) return false;
            for (int i = 0; i < g.nodes.Count; i++)
            {
                if (wg.inVicinityOfNode(pos, g.nodes[i]))
                    return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldGrid), "getNode")]
    public static class HostWorldGridGetNodeOccupyingPatch
    {
        private static void Prefix(WorldGrid __instance, Vector3 pos)
            => HostGridOccupancy.PushOccupyingGrid(__instance, pos);

        private static void Finalizer(WorldGrid __instance)
            => HostGridOccupancy.PopOccupyingGrid(__instance);
    }

    [HarmonyPatch(typeof(WorldGrid), "registerToNode")]
    public static class HostWorldGridRegisterToNodeOccupyingPatch
    {
        private static void Prefix(WorldGrid __instance, GameObject GO)
        {
            if (GO != null)
                HostGridOccupancy.PushOccupyingGrid(__instance, GO.transform.position);
            else
                HostGridOccupancy.PushOccupyingGrid(__instance, Vector3.zero, search: false);
        }

        private static void Finalizer(WorldGrid __instance)
            => HostGridOccupancy.PopOccupyingGrid(__instance);
    }

    [HarmonyPatch(typeof(WorldGrid), "registerToClosestNode")]
    public static class HostWorldGridRegisterClosestOccupyingPatch
    {
        private static void Prefix(WorldGrid __instance, GameObject GO)
        {
            if (GO != null)
                HostGridOccupancy.PushOccupyingGrid(__instance, GO.transform.position);
            else
                HostGridOccupancy.PushOccupyingGrid(__instance, Vector3.zero, search: false);
        }

        private static void Finalizer(WorldGrid __instance)
            => HostGridOccupancy.PopOccupyingGrid(__instance);
    }

    [HarmonyPatch(typeof(WorldGrid), "registerToNodes")]
    public static class HostWorldGridRegisterToNodesOccupyingPatch
    {
        private static void Prefix(WorldGrid __instance, GameObject GO)
        {
            if (GO != null)
                HostGridOccupancy.PushOccupyingGrid(__instance, GO.transform.position);
            else
                HostGridOccupancy.PushOccupyingGrid(__instance, Vector3.zero, search: false);
        }

        private static void Finalizer(WorldGrid __instance)
            => HostGridOccupancy.PopOccupyingGrid(__instance);
    }

    /// <summary>
    /// After forceAttackClosestCharacter runs, if the entity fell through to
    /// attackPlayer() because the proxy has no Character component, redirect
    /// to the proxy only when that proxy is closer than the host.
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
            if (__instance.target != hostPlayer.transform
                && __instance.target != hostPlayer._transform)
                return;

            // Dream bunker spirit stays on sticky owner — do not steal to nearer proxy.
            if (DWMPHorde.Sync.DreamForestSpiritAggro.IsBunkerDreamSpirit(__instance))
            {
                Transform sticky = DWMPHorde.Sync.DreamForestSpiritAggro.TryGetStickyTarget();
                if (sticky != null)
                {
                    __instance.attackCharacter(sticky);
                    return;
                }
            }

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            float range = (float)__instance.farViewDistance * __instance.aniSightRangeModifier;
            Sniffer sniffer = __instance.GetComponent<Sniffer>();
            if (sniffer != null && sniffer.radius > range)
                range = sniffer.radius;

            float hostDist = Core.trueDistance(
                __instance.transform.position, hostPlayer._transform.position);

            // Find the closest detectable proxy that is nearer than the host.
            Transform closestProxy = null;
            float closestDist = hostDist;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                Transform pt = proxy.transform;
                float distToProxy = Core.trueDistance(__instance.transform.position, pt.position);
                if (distToProxy > range || distToProxy >= closestDist)
                    continue;
                CharBase proxyCB = pt.GetComponent<CharBase>();
                if (proxyCB == null || proxyCB.invisible || proxyCB.ignoreMe)
                    continue;
                closestDist = distToProxy;
                closestProxy = pt;
            }

            if (closestProxy != null)
                __instance.attackCharacter(closestProxy);
        }
    }

    /// <summary>
    /// Vanilla <c>attackPlayer</c> always targets <see cref="Player.Instance"/> (host).
    /// Redirect to the nearest living player body (host or remote proxy) so
    /// story spawns (dream forest spirit, etc.) chase whoever is actually there.
    /// </summary>
    [HarmonyPatch(typeof(Character), "attackPlayer")]
    public static class HostAttackPlayerNearestPatch
    {
        private static bool Prefix(Character __instance)
        {
            if (__instance == null || __instance.dummy)
                return true;
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;

            // Dream bunker spirit: never retarget off the spawn owner (ThreatTrigger
            // "recent proxy" steal was hitting far clients still on the dream path).
            Transform prefer = null;
            if (DWMPHorde.Sync.DreamForestSpiritAggro.IsBunkerDreamSpirit(__instance))
                prefer = DWMPHorde.Sync.DreamForestSpiritAggro.TryGetStickyTarget();
            if (prefer == null)
                prefer = ThreatTriggerContext.TryGetRecentProxyTransform(8f);
            if (prefer == null)
                prefer = FindNearestPlayerTransform(__instance.transform.position);
            if (prefer == null)
                return true;

            if (__instance.aggressiveness != Aggressiveness.defensive)
                __instance.aggressiveness = Aggressiveness.attackOnSight;
            __instance.attackCharacter(prefer);

            // Event / temp spawns: do not inherit host bigLocation waypoints (dog walks
            // to host macro map). Clear patrol; chase stays on the triggering player.
            if (__instance.temporarySpawned && __instance.waypoints != null)
                __instance.waypoints.Clear();

            return false;
        }

        internal static Transform FindNearestPlayerTransform(Vector3 from)
        {
            Transform best = null;
            float bestD = float.MaxValue;

            Player host = Player.Instance;
            if (host != null)
            {
                CharBase hcb = host.GetComponent<CharBase>();
                if (hcb != null && hcb.alive && !hcb.invisible && !hcb.ignoreMe)
                {
                    best = host._transform != null ? host._transform : host.transform;
                    bestD = Core.trueDistance(from, best.position);
                }
            }

            var net = LanNetworkManager.Instance;
            if (net == null) return best;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                CharBase pcb = proxy.GetComponent<CharBase>();
                if (pcb == null || !pcb.alive || pcb.invisible || pcb.ignoreMe)
                    continue;
                float d = Core.trueDistance(from, proxy.transform.position);
                if (d < bestD)
                {
                    bestD = d;
                    best = proxy.transform;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Forest spirit waitToTeleport only checked host FOV + host distance. Client looking
    /// (or standing next to it) still allowed the blink.
    /// </summary>
    [HarmonyPatch(typeof(Character), "waitToTeleport")]
    public static class HostWaitToTeleportPatch
    {
        private static bool Prefix(Character __instance)
        {
            if (!HostPlayerIdentity.HostWithRemotes())
                return true;
            if (__instance == null || __instance.target == null)
                return true;

            if (HostPlayerIdentity.AnyInSight(__instance.transform, canBeFarAway: false))
                return false;
            float d = Mathf.Sqrt(PlayerPositionManager.SqrDistanceToNearestPlayer(__instance.transform.position));
            if (d <= 200f)
                return false;

            __instance.teleportWithEffect(
                __instance.target.position + __instance.target.up * 500f
                    + new Vector3(UnityEngine.Random.Range(-100, 100), 0f, UnityEngine.Random.Range(-100, 100)),
                "ForestSpirit_fastSpawnEff",
                4f);
            return false;
        }
    }

    /// <summary>
    /// Hit-and-run flee after attacking used host position even when the victim was the proxy.
    /// </summary>
    [HarmonyPatch(typeof(Character), "set_attacking", new[] { typeof(bool) })]
    public static class HostAttackingFleePatch
    {
        private static void Prefix(Character __instance, object[] __args)
        {
            bool value = (bool)__args[0];
            if (value)
                return;
            if (!HostPlayerIdentity.HostWithRemotes())
                return;
            if (__instance == null || __instance.currentAttack == null)
                return;
            if (!__instance.currentAttack.runAwayAfterAttacking)
                return;
            if (UnityEngine.Random.Range(0f, 1f) <= __instance.currentAttack.runAwayChance)
                return;

            Vector3 from = __instance.target != null
                ? __instance.target.position
                : PlayerPositionManager.GetNearestPlayerPosition(__instance.transform.position);
            __instance.runAway(from);
            __instance.currentAttack.runAwayAfterAttacking = false;
        }
    }

    /// <summary>
    /// Scripted Activity.playerIsTarget always bound to the host body.
    /// </summary>
    [HarmonyPatch(typeof(Character.Activity), "assignTarget")]
    public static class HostActivityAssignTargetPatch
    {
        private static void Postfix(Character.Activity __instance)
        {
            if (__instance == null || !__instance.playerIsTarget)
                return;
            if (!HostPlayerIdentity.HostWithRemotes())
                return;
            Vector3 from = __instance.thisCharacter != null
                ? __instance.thisCharacter.transform.position
                : Vector3.zero;
            GameObject go = HostPlayerIdentity.NearestLivingGo(from);
            if (go != null)
                __instance.target = go;
        }
    }

    [HarmonyPatch(typeof(Character.Activity), "run")]
    public static class HostActivityRunAwayPatch
    {
        private static void Prefix(Character.Activity __instance, Character _character)
        {
            if (__instance == null || _character == null)
                return;
            if (__instance.type != Character.Activity.Type.runAway)
                return;
            if (__instance.target != null)
                return;
            if (!HostPlayerIdentity.HostWithRemotes())
                return;
            GameObject go = HostPlayerIdentity.NearestLivingGo(_character.transform.position);
            if (go != null)
                __instance.target = go;
        }
    }

    [HarmonyPatch(typeof(Character), "onBansheeSeePlayer")]
    public static class HostBansheeSeePlayerPatch
    {
        private static void Postfix(Character __instance)
        {
            if (!HostPlayerIdentity.HostWithRemotes() || __instance == null || !__instance.alive)
                return;
            Transform n = HostPlayerIdentity.NearestLiving(__instance.transform.position);
            if (n == null)
                return;
            __instance.target = n;
            if (__instance.behaviour != Character.Behaviour.defensive)
                __instance.goToPos(n);
        }
    }

    [HarmonyPatch(typeof(Character), "onBansheeOutOfSightOfPlayer")]
    public static class HostBansheeOutOfSightPatch
    {
        private static void Postfix(Character __instance)
        {
            if (!HostPlayerIdentity.HostWithRemotes() || __instance == null)
                return;
            Transform n = HostPlayerIdentity.NearestLiving(__instance.transform.position);
            if (n != null)
                __instance.goToPos(n);
        }
    }

    [HarmonyPatch(typeof(Character), "checkIfInSightOfPlayer")]
    public static class HostBansheeCheckSightPatch
    {
        private static bool Prefix(Character __instance)
        {
            if (!HostPlayerIdentity.HostWithRemotes())
                return true;
            if (__instance == null || !__instance.banshee)
                return true;
            if (HostPlayerIdentity.AnyInSight(__instance.transform, canBeFarAway: false))
            {
                __instance.Invoke("onBansheeSeePlayer", 0f);
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Host removeMe (flee-despawn crows/rabbits, temp wildlife) never reached the client —
    /// EntityState just stopped, and _everHostSyncedIds blocked unmatched cleanup → permanent
    /// ghost birds the host no longer has.
    /// </summary>
    [HarmonyPatch(typeof(Character), "removeMe")]
    public static class HostCharacterRemoveMeDespawnPatch
    {
        private static void Prefix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!ModRuntime.Network.IsConnected)
                return;
            if (__instance == null)
                return;

            if (!CharacterTracker.TryGetStableId(__instance, out short id) || id == 0)
                return;

            LanNetworkManager.Instance?.SendEntityDespawn(id);
        }
    }
}
