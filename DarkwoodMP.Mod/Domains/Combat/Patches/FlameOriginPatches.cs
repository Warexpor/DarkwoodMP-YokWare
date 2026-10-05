using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>Who started a fire: a player's flamethrower or thrown fire bomb (their id), or nobody (0).</summary>
    public sealed class FlameOrigin : MonoBehaviour
    {
        public int PlayerId;
        /// <summary>Shot by this machine's own player (its flamethrower), not a copy of someone else's fire.</summary>
        public bool LocalShot;
    }

    /// <summary>
    /// Vanilla fire contact always counts as a player's hit (<c>Flame.onCollideWith</c>: byPlayer
    /// true), so with friendly fire off no fire hurt another player's body at all: not a burning
    /// barrel's flames, not their own fire bomb. Fires now remember who started them. Friendly
    /// fire off spares a player from someone else's fire only; their own and the world's burn.
    /// </summary>
    internal static class FlameOriginContext
    {
        /// <summary>Owner for flames spawned right now; -1 when none is being set.</summary>
        internal static int Spawning = -1; // process-scoped: call-scoped, unwound by the patch Finalizers
        /// <summary>Owner of the flame whose contact is being applied; -1 outside a flame contact.</summary>
        internal static int Hitting = -1; // process-scoped: call-scoped, unwound by the patch Finalizer
        /// <summary>Flames spawned right now come from this machine's own shot.</summary>
        internal static bool SpawningLocalShot; // process-scoped: call-scoped, unwound by the patch Finalizer
        /// <summary>The flame whose contact is being applied is this machine's own shot; its effect.</summary>
        internal static bool HittingLocalShot; // process-scoped: call-scoped, unwound by the patch Finalizer
        internal static InvItemEffect HittingEffect; // process-scoped: call-scoped, unwound by the patch Finalizer

        /// <summary>
        /// For a flame contact: true when friendly fire off spares <paramref name="victimId"/> from it.
        /// </summary>
        internal static bool SparedByFriendlyFire(int victimId)
            => Hitting > 0 && Hitting != victimId && !SessionSettings.FriendlyFireEnabled;
    }

    /// <summary>A player's firearm whose projectile is fire (the flamethrower).</summary>
    [HarmonyPatch(typeof(Player), "spawnBullet")]
    public static class FlameOriginShotPatch
    {
        private static void Prefix(Player __instance, out int __state)
        {
            __state = FlameOriginContext.Spawning;
            var net = ModRuntime.Network;
            if (net != null && net.IsConnected && __instance == Player.Instance)
            {
                FlameOriginContext.Spawning = net.LocalPlayerId;
                FlameOriginContext.SpawningLocalShot = true;
            }
        }

        private static void Finalizer(int __state)
        {
            FlameOriginContext.Spawning = __state;
            FlameOriginContext.SpawningLocalShot = false;
        }
    }

    /// <summary>A blast's fire (molotov secondaries): the thrower's, or the world's for a barrel.</summary>
    [HarmonyPatch(typeof(Explodes), "explode")]
    public static class FlameOriginBlastPatch
    {
        private static void Prefix(Explodes __instance, out int __state)
        {
            __state = FlameOriginContext.Spawning;
            if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected || __instance == null)
                return;
            Transform source = ExplosionFriendlyFirePatch.ResolveSpawnSource(__instance);
            FlameOriginContext.Spawning = ExplosionFriendlyFirePatch.IsPlayerSourced(source, net)
                ? ExplosionFriendlyFirePatch.ResolveSourcePlayerId(source, net)
                : 0;
        }

        private static void Finalizer(int __state) => FlameOriginContext.Spawning = __state;
    }

    [HarmonyPatch(typeof(Core), nameof(Core.AddPrefab), new[] { typeof(Object), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    public static class FlameOriginTagObjectPatch
    {
        private static void Postfix(GameObject __result) => FlameOriginTag.Tag(__result);
    }

    [HarmonyPatch(typeof(Core), nameof(Core.AddPrefab), new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    public static class FlameOriginTagPathPatch
    {
        private static void Postfix(GameObject __result) => FlameOriginTag.Tag(__result);
    }

    internal static class FlameOriginTag
    {
        internal static void Tag(GameObject go)
        {
            if (FlameOriginContext.Spawning < 0 || go == null)
                return;
            Flame[] flames = go.GetComponentsInChildren<Flame>(true);
            for (int i = 0; i < flames.Length; i++)
            {
                FlameOrigin o = flames[i].GetComponent<FlameOrigin>();
                if (o == null)
                    o = flames[i].gameObject.AddComponent<FlameOrigin>();
                o.PlayerId = FlameOriginContext.Spawning;
                o.LocalShot = FlameOriginContext.SpawningLocalShot;
            }
        }
    }

    [HarmonyPatch(typeof(Flame), "onCollideWith")]
    public static class FlameOriginHitPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(Flame __instance, out int __state)
        {
            __state = FlameOriginContext.Hitting;
            FlameOrigin o = __instance != null ? __instance.GetComponent<FlameOrigin>() : null;
            FlameOriginContext.Hitting = o != null ? o.PlayerId : 0;
            FlameOriginContext.HittingLocalShot = o != null && o.LocalShot;
            FlameOriginContext.HittingEffect = __instance != null ? __instance.effect : null;
        }

        private static void Finalizer(int __state)
        {
            FlameOriginContext.Hitting = __state;
            FlameOriginContext.HittingLocalShot = false;
            FlameOriginContext.HittingEffect = null;
        }
    }
}
