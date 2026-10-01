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

    [HarmonyPatch(typeof(Character), "bansheeAgitated")]
    public static class HostBansheeAgitatedPatch
    {
        private static AudioObject _victimScream;

        /// <summary>Registered with NetworkResetRegistry so a recycled audio object is never kept across sessions.</summary>
        public static void Reset()
        {
            _victimScream = null;
        }

        internal static void StopVictimScream()
        {
            // AudioObjects are pooled: once the scream finished the object is recycled for other
            // sounds, so only stop it while it is still ours and still playing this clip.
            if (_victimScream != null
                && _victimScream.IsPlaying()
                && _victimScream.audioID == "banshee_agitated_player")
            {
                _victimScream.Stop(0.2f);
            }
            _victimScream = null;
            PlayerAudioHelper.ForwardWorldObjectSound("banshee_agitated_player", 0f, Vector3.zero);
        }

        internal static bool SuppressHostScreamForward; // process-scoped: call-scoped, unwound by its Finalizer/finally

        private static bool Prefix(Character __instance)
        {
            SuppressHostScreamForward = false;
            if (!HostPlayerIdentity.HostWithRemotes() || __instance == null)
                return true;
            Transform n = HostPlayerIdentity.NearestLiving(__instance.transform.position);
            Player host = Player.Instance;
            if (n == null || host == null)
                return true;
            if (n == host.transform || (host._transform != null && n == host._transform))
                return true;
            SuppressHostScreamForward = true;
            return true;
        }

        private static void Postfix(Character __instance)
        {
            if (!SuppressHostScreamForward)
                return;
            SuppressHostScreamForward = false;
            Transform n = HostPlayerIdentity.NearestLiving(__instance.transform.position);
            Player host = Player.Instance;
            if (n == null || host == null)
                return;
            if (host.bansheeAgitatedSoundAO != null)
            {
                host.bansheeAgitatedSoundAO.Stop(0.05f);
                host.bansheeAgitatedSoundAO = null;
            }
            _victimScream = AudioController.Play("banshee_agitated_player", n.position, null);
            PlayerAudioHelper.ForwardWorldObjectSound("banshee_agitated_player", 1f, n.position);
        }

        // bansheeAgitated can throw after Prefix set Suppress; stuck true kills scream forward.
        private static void Finalizer()
        {
            SuppressHostScreamForward = false;
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
            HostBansheeAgitatedPatch.StopVictimScream();
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
