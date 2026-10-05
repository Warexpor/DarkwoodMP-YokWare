using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla AI steps that use "the player's position" (<c>Player.Instance._transform</c>) while
    /// acting on the character's own target. On the host with peers, the body is the one this
    /// character is after. Same technique as <see cref="HostPlayerIdentity.AnyInSight"/>: the
    /// host Player's <c>_transform</c> points at that body for the vanilla call only.
    /// </summary>
    internal static class HostBodySwap
    {
        /// <summary>The peer this character is after, or null when it is the host (or nobody).</summary>
        internal static Transform PeerBodyOf(Character c, bool nearestWhenNoTarget)
        {
            if (c == null || !HostPlayerIdentity.HostWithRemotes())
                return null;
            Player host = Player.Instance;
            if (host == null)
                return null;
            Transform t = c.target;
            if (t != null && t != host.transform && t != host._transform && CanSeeComponentCache.IsProxy(t))
                return t;
            if (t == null && nearestWhenNoTarget)
            {
                Transform n = HostPlayerIdentity.NearestLiving(c.transform.position);
                if (n != null && n != host.transform && CanSeeComponentCache.IsProxy(n))
                    return n;
            }
            return null;
        }

        internal static Transform Swap(Transform body)
        {
            Player host = Player.Instance;
            Transform saved = host._transform;
            host._transform = body;
            return saved;
        }

        internal static void Restore(Transform saved)
        {
            if (saved != null && Player.Instance != null)
                Player.Instance._transform = saved;
        }
    }

    /// <summary>The <c>teleportNearPlayer</c> animation frame lands next to the character's target.</summary>
    [HarmonyPatch(typeof(AnimationTriggerListener), nameof(AnimationTriggerListener.checkFrameTrigger))]
    public static class HostTeleportNearBodyPatch
    {
        private static readonly AccessTools.FieldRef<AnimationTriggerListener, Character> CharacterRef =
            AccessTools.FieldRefAccess<AnimationTriggerListener, Character>("character");

        private static void Prefix(AnimationTriggerListener __instance, tk2dSpriteAnimationFrame.Trigger.Type type, ref Transform __state)
        {
            __state = null;
            if (type != tk2dSpriteAnimationFrame.Trigger.Type.teleportNearPlayer || __instance == null)
                return;
            Transform body = HostBodySwap.PeerBodyOf(CharacterRef(__instance), nearestWhenNoTarget: true);
            if (body != null)
                __state = HostBodySwap.Swap(body);
        }

        private static void Finalizer(Transform __state) => HostBodySwap.Restore(__state);
    }

    /// <summary>A stalker's hit-and-flee runs away from the body it was chasing.</summary>
    [HarmonyPatch(typeof(Character), "setToFlee")]
    public static class HostStalkerFleePatch
    {
        private static void Prefix(Character __instance, ref Transform __state)
        {
            __state = null;
            Transform body = HostBodySwap.PeerBodyOf(__instance, nearestWhenNoTarget: false);
            if (body != null)
                __state = HostBodySwap.Swap(body);
        }

        private static void Finalizer(Transform __state) => HostBodySwap.Restore(__state);
    }

    /// <summary>
    /// A banshee leaving its defensive pose counts as "out of sight" only when no player sees it
    /// (vanilla: the local player only, so a client watching it released the victim).
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.endDefensive))]
    public static class HostBansheeEndDefensivePatch
    {
        private static readonly System.Action<Character> OutOfSight =
            AccessTools.MethodDelegate<System.Action<Character>>(AccessTools.Method(typeof(Character), "onBansheeOutOfSightOfPlayer"));

        private static bool Prefix(Character __instance)
        {
            if (__instance == null || !__instance.banshee || !HostPlayerIdentity.HostWithRemotes())
                return true;
            __instance.endingDefensive = false;
            __instance.startingDefensive = false;
            __instance.defensive = false;
            __instance.setBehaviour(Character.Behaviour.idle);
            if (!HostPlayerIdentity.AnyInSight(__instance.transform, canBeFarAway: false))
                OutOfSight(__instance);
            return false;
        }
    }
}

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A creature hit by a player turns on that player (vanilla <c>reactToCharacter</c>: a
    /// <c>Player</c> attacker gets chased, skipping the cowardice roll). On the host it went wrong
    /// both ways: a client's hit came from a stand-in without a <c>Player</c>, so a hit creature
    /// rolled to flee instead; and the host's own hit called attackPlayer, which the nearest-body
    /// rule turned on whichever player stood closer. The player who hit it is chased.
    /// </summary>
    [HarmonyPatch(typeof(Character), "reactToCharacter")]
    public static class HostRetaliateOnAttackerPatch
    {
        private static bool Prefix(Character __instance, Character attackerChar, Transform attackerTransform, bool aggressively)
        {
            if (__instance == null || attackerTransform == null || !HostPlayerIdentity.HostWithRemotes())
                return true;
            bool playerBody = attackerTransform.GetComponent<Player>() != null || CanSeeComponentCache.IsProxy(attackerTransform);
            if (!playerBody)
                return true;
            // Vanilla's own early outs, food and flee handling stay vanilla's.
            if ((attackerChar != null && attackerChar.faction == __instance.faction)
                || __instance.target == attackerTransform
                || (!aggressively && (__instance.aggressiveness == Aggressiveness.neutral
                    || __instance.aggressiveness == Aggressiveness.follower || __instance.npc != null)))
                return true;
            if (__instance.eating
                || __instance.aggressiveness == Aggressiveness.flee
                || __instance.aggressiveness == Aggressiveness.fleeAndDespawn
                || __instance.behaviour == Character.Behaviour.escaping
                || __instance.dummy)
                return true;
            __instance.setBehaviour(Character.Behaviour.chasingTarget);
            if (__instance.aggressiveness != Aggressiveness.defensive)
                __instance.aggressiveness = Aggressiveness.attackOnSight;
            __instance.attackCharacter(attackerTransform);
            return false;
        }
    }
}

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A spawning human-spider settles into "the player's" location (vanilla onSpiderSpawn reads
    /// <c>Player.Instance.whereAmI</c>): with a client the one beside it, it was reparented into the
    /// host's location and walked the host's waypoints. It settles into the location of the
    /// player nearest to it.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.onSpiderSpawn))]
    public static class HostSpiderSpawnLocationPatch
    {
        internal sealed class Saved
        {
            public Location Location;
        }

        private static void Prefix(Character __instance, out Saved __state)
        {
            __state = null;
            Player host = Player.Instance;
            if (__instance == null || host == null || host.whereAmI == null || !HostPlayerIdentity.HostWithRemotes())
                return;
            Transform body = HostPlayerIdentity.NearestLiving(__instance.transform.position);
            if (body == null || body == host.transform || body == host._transform)
                return;
            Location at = Location.getAtPos(body.position);
            __state = new Saved { Location = host.whereAmI.location };
            host.whereAmI.location = at;
        }

        private static void Finalizer(Saved __state)
        {
            Player host = Player.Instance;
            if (__state != null && host != null && host.whereAmI != null)
                host.whereAmI.location = __state.Location;
        }
    }
}
