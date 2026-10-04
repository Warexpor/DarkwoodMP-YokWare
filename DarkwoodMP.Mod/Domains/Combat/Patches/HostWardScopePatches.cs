using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla <c>Character.canSeeEnemy</c> reads "the player" for three things: a
    /// <c>constantlyAttackPlayer</c> character always paths to it, and an <c>afraidOfHideout</c> /
    /// <c>afraidOfForestSpiritWard</c> character flees and despawns whenever it carries the shadow /
    /// forest-spirit ward. On the host with peers, "the player" is the body this character is
    /// after (its target, else the nearest living body): the host sitting in a lit hideout no
    /// longer scares off the monsters hunting a client in the forest, and a constant attacker
    /// goes for whoever is closest. Wards on a peer are handled in <see cref="HostCanSeeEnemyPatch"/>.
    /// </summary>
    [HarmonyPatch(typeof(Character), "canSeeEnemy")]
    public static class HostWardScopePatch
    {
        private struct State
        {
            public bool MaskedWard;
            public bool RestoreConstant;
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(Character __instance, ref State __state)
        {
            __state = default;
            if (!HostPlayerIdentity.HostWithRemotes() || __instance == null)
                return;
            Player host = Player.Instance;
            if (host == null)
                return;
            bool hostWard = host.effects != null
                && (__instance.afraidOfHideout && host.effects.hasEffectType(CharacterEffectType.shadowWard)
                    || __instance.afraidOfForestSpiritWard && host.effects.hasEffectType(CharacterEffectType.forestSpiritWard));
            if (!hostWard && !__instance.constantlyAttackPlayer)
                return;

            Transform body = BodyOf(__instance, host);
            if (body == null || body == host.transform)
                return;

            if (hostWard)
            {
                HostWardMask.Active = true;
                __state.MaskedWard = true;
            }
            if (__instance.constantlyAttackPlayer)
            {
                // Same as vanilla's first block, aimed at the peer; vanilla's is skipped this call.
                if (__instance.AIpath != null)
                    __instance.AIpath.setTarget(body);
                __instance.lastKnownTargetPosition = body.position;
                __instance.constantlyAttackPlayer = false;
                __state.RestoreConstant = true;
            }
        }

        private static void Finalizer(Character __instance, State __state)
        {
            if (__state.MaskedWard)
                HostWardMask.Active = false;
            if (__state.RestoreConstant && __instance != null)
                __instance.constantlyAttackPlayer = true;
        }

        private static Transform BodyOf(Character c, Player host)
        {
            Transform t = c.target;
            if (t != null)
            {
                if (t == host.transform)
                    return t;
                RemotePlayerProxy proxy = t.GetComponent<RemotePlayerProxy>();
                if (proxy != null)
                    return t;
            }
            return HostPlayerIdentity.NearestLiving(c.transform.position);
        }
    }

    /// <summary>
    /// While <see cref="HostWardScopePatch"/> runs vanilla's ward test for a character that is
    /// after a peer, the host's own wards read as absent.
    /// </summary>
    [HarmonyPatch(typeof(CharacterEffects), nameof(CharacterEffects.hasEffectType), typeof(CharacterEffectType))]
    public static class HostWardMask
    {
        internal static bool Active; // process-scoped: call-scoped, cleared by HostWardScopePatch's Finalizer

        private static bool Prefix(CharacterEffects __instance, CharacterEffectType type, ref bool __result)
        {
            if (!Active)
                return true;
            if (type != CharacterEffectType.shadowWard && type != CharacterEffectType.forestSpiritWard)
                return true;
            Player host = Player.Instance;
            if (host == null || __instance != host.effects)
                return true;
            __result = false;
            return false;
        }
    }
}
