using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
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

            // Dream bunker spirit stays on its sticky owner; do not steal it to a nearer proxy.
            if (DWMPHorde.Sync.DreamForestSpiritAggro.IsBunkerDreamSpirit(__instance))
            {
                Transform sticky = DWMPHorde.Sync.DreamForestSpiritAggro.TryGetStickyTarget();
                if (sticky != null)
                {
                    __instance.attackCharacter(sticky);
                    return;
                }
            }

            var net = ModRuntime.Network;
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
                CharBase proxyCB = proxy.CachedCharBase;
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

            var net = ModRuntime.Network;
            if (net == null) return best;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                CharBase pcb = proxy.CachedCharBase;
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
    /// Vanilla <c>attacking = false</c> rolls runAwayChance and runs away from Player.Instance.
    /// With remotes the flee is done here from the real victim instead, so vanilla's own roll is
    /// switched off for exactly this call (<c>currentAttack</c> is the character's shared
    /// attacks[] entry, so the flag must be handed back afterwards or the attack would never
    /// flee again). Set before runAway: runAway itself sets <c>attacking = false</c> again and
    /// would otherwise re-enter this prefix.
    /// </summary>
    [HarmonyPatch(typeof(Character), "set_attacking", new[] { typeof(bool) })]
    public static class HostAttackingFleePatch
    {
        private struct State
        {
            public Character.Attack Attack;
        }

        private static void Prefix(Character __instance, bool value, ref State __state)
        {
            __state = default;
            if (value)
                return;
            if (!HostPlayerIdentity.HostWithRemotes())
                return;
            if (__instance == null || __instance.currentAttack == null)
                return;
            Character.Attack attack = __instance.currentAttack;
            if (!attack.runAwayAfterAttacking)
                return;

            __state.Attack = attack;
            attack.runAwayAfterAttacking = false;

            // Same roll vanilla would make (flee when Random > runAwayChance), made once.
            if (UnityEngine.Random.Range(0f, 1f) <= attack.runAwayChance)
                return;

            Vector3 from = __instance.target != null
                ? __instance.target.position
                : PlayerPositionManager.GetNearestPlayerPosition(__instance.transform.position);
            __instance.runAway(from);
        }

        // Finalizer (not Postfix): the shared Attack must get its vanilla flag back even if the
        // setter or runAway throws.
        private static void Finalizer(State __state)
        {
            if (__state.Attack != null)
                __state.Attack.runAwayAfterAttacking = true;
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

    /// <summary>
    /// Banshee scream at a remote player. Vanilla bansheeAgitated / onBansheeSeePlayer give the
    /// player it sees a personal scream loop on their own body, a camera shake and the banshee
    /// overlay. When that player is a client the host must not get them (it was shaking and
    /// screaming at the host for a banshee staring at someone else); the client gets them through
    /// <see cref="BansheeAgitationMessage"/>, and the banshee's sight light goes to every peer.
    /// </summary>
    internal static class BansheeVictims
    {
        /// <summary>Banshee (instance id) → the remote player it is screaming at.</summary>
        private static readonly Dictionary<int, int> _victimByBanshee = new Dictionary<int, int>();

        /// <summary>Registered with NetworkResetRegistry.</summary>
        public static void Reset() => _victimByBanshee.Clear();

        /// <summary>
        /// The remote player the banshee screams at, or -1 when that is the host (or nobody): vanilla
        /// screams at the player who sees it, so the nearest body that has it in sight; the nearest
        /// living body only when nobody does. The nearest alone gave a host facing away the scream
        /// meant for a client staring at it.
        /// </summary>
        internal static int NearestRemoteVictim(Character banshee, out Transform victim)
        {
            victim = null;
            if (!HostPlayerIdentity.HostWithRemotes() || banshee == null)
                return -1;
            Transform n = HostPlayerIdentity.NearestViewer(banshee.transform, canBeFarAway: true)
                ?? HostPlayerIdentity.NearestLiving(banshee.transform.position);
            Player host = Player.Instance;
            if (n == null || host == null || n == host.transform || n == host._transform)
                return -1;
            var net = ModRuntime.Network;
            if (net == null)
                return -1;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy != null && proxy.transform == n && proxy.PlayerId > 0)
                {
                    victim = n;
                    return proxy.PlayerId;
                }
            }
            return -1;
        }

        internal static void SetSightLight(Character banshee, bool on)
        {
            Transform light = banshee != null ? banshee.transform.Find("SightLight") : null;
            if (light != null)
                light.gameObject.SetActive(on);
        }

        /// <summary>Host: the banshee screams at <paramref name="victimId"/>; a previous different victim is released.</summary>
        internal static void Agitate(Character banshee, int victimId, bool overlay)
        {
            int key = banshee.GetInstanceID();
            if (_victimByBanshee.TryGetValue(key, out int prev) && prev != victimId)
                Send(banshee, prev, agitated: false, overlay: false);
            _victimByBanshee[key] = victimId;
            Send(banshee, victimId, agitated: true, overlay: overlay);
        }

        /// <summary>Host: the banshee lost sight (or turned to the host); release its remote victim.</summary>
        internal static void Release(Character banshee)
        {
            if (banshee == null)
                return;
            int key = banshee.GetInstanceID();
            if (!_victimByBanshee.TryGetValue(key, out int prev))
                return;
            _victimByBanshee.Remove(key);
            Send(banshee, prev, agitated: false, overlay: false);
        }

        private static void Send(Character banshee, int victimId, bool agitated, bool overlay)
        {
            if (!NetGuard.ConnectedHost(out var net))
                return;
            if (!CharacterTracker.TryGetStableId(banshee, out short id) || id == 0)
                return;
            var msg = new BansheeAgitationMessage { HostId = id, VictimId = victimId, Agitated = agitated, Overlay = overlay };
            net.Broadcast(NetMessageType.BansheeAgitation, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// Host, banshee agitated at a remote player: the banshee's own part (sight light, far-sight
    /// flag from the victim's distance) runs here; the victim's scream, shake and overlay go to them.
    /// </summary>
    [HarmonyPatch(typeof(Character), "bansheeAgitated")]
    public static class HostBansheeAgitatedPatch
    {
        private static bool Prefix(Character __instance)
        {
            int victimId = BansheeVictims.NearestRemoteVictim(__instance, out Transform victim);
            if (victimId < 0)
            {
                BansheeVictims.Release(__instance);
                return true;
            }
            if (!__instance.alive)
                return false;
            BansheeVictims.SetSightLight(__instance, true);
            __instance.canSeeEnemyFar = Core.trueDistance(victim, __instance.transform) < __instance.farViewDistance;
            BansheeVictims.Agitate(__instance, victimId, overlay: HostBansheeSeePlayerPatch.InsideSighting);
            return false;
        }
    }

    /// <summary>
    /// Host, banshee sees a player: vanilla targets Player.Instance (the host) and fades the
    /// host's overlay. For a remote victim it targets that player and the overlay goes to them
    /// (through bansheeAgitated above).
    /// </summary>
    [HarmonyPatch(typeof(Character), "onBansheeSeePlayer")]
    public static class HostBansheeSeePlayerPatch
    {
        /// <summary>True while a sighting runs, so the agitation carries the overlay fade.</summary>
        internal static bool InsideSighting; // process-scoped: call-scoped, unwound by its Finalizer

        private static System.Reflection.MethodInfo _checkSight; // process-scoped: reflection cache

        private static bool Prefix(Character __instance)
        {
            int victimId = BansheeVictims.NearestRemoteVictim(__instance, out Transform victim);
            if (victimId < 0)
                return true;
            if (!__instance.alive)
                return false;

            // Vanilla onBansheeSeePlayer with the victim in place of Player.Instance.
            __instance.target = victim;
            // Routines match by method name; the delegate must be a plain bound one so its
            // Method is the vanilla method (isRoutineActive / stopRoutine by name still work).
            if (_checkSight == null) _checkSight = AccessTools.Method(typeof(Character), "checkIfInSightOfPlayer");
            __instance.stopRoutine("lostEnemy", all: true);
            __instance.stopRoutine("checkIfInSightOfPlayer", all: true);
            __instance.startRoutine((System.Action)System.Delegate.CreateDelegate(typeof(System.Action), __instance, _checkSight), 0.5f);
            InsideSighting = true;
            try { Traverse.Create(__instance).Method("bansheeAgitated").GetValue(); }
            finally { InsideSighting = false; }
            if (__instance.behaviour != Character.Behaviour.defensive)
                __instance.goToPos(victim);
            return false;
        }

        private static void Finalizer() => InsideSighting = false;
    }

    /// <summary>Host, banshee lost sight: chase the nearest player and release the remote victim.</summary>
    [HarmonyPatch(typeof(Character), "onBansheeOutOfSightOfPlayer")]
    public static class HostBansheeOutOfSightPatch
    {
        private static void Postfix(Character __instance)
        {
            if (__instance == null)
                return;
            BansheeVictims.Release(__instance);
            if (!HostPlayerIdentity.HostWithRemotes())
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
    /// Host removeMe (flee-despawn crows/rabbits and temporary wildlife) never reached the client;
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

            ModRuntime.Network?.SendEntityDespawn(id);
        }
    }
}
