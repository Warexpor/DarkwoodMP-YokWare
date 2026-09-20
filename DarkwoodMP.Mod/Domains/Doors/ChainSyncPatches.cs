using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// ChainParent absolute health/attached fan-out. Forwardable Broadcast for 3+.
    /// </summary>
    internal static class ChainSyncHelpers
    {
        internal static ChainStateMessage BuildMessage(ChainParent chain)
        {
            Vector3 p = chain.transform.position;
            return new ChainStateMessage
            {
                PosX = Mathf.Round(p.x * 10f) / 10f,
                PosY = Mathf.Round(p.y * 10f) / 10f,
                PosZ = Mathf.Round(p.z * 10f) / 10f,
                Health = chain.health,
                Attached = (byte)(chain.attached ? 1 : 0),
                HasMaxHealth = true,
                MaxHealth = chain.maxHealth
            };
        }

        internal static void SendState(ChainParent chain, string reason)
        {
            if (chain == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            var msg = BuildMessage(chain);
            ModRuntime.Network.Broadcast(NetMessageType.ChainState,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[ChainSync] send {reason} at ({msg.PosX:F1},{msg.PosZ:F1}) " +
                $"health={msg.Health:F1} attached={msg.Attached != 0}");
        }
    }

    /// <summary>Damage → maybe detach at 0. Host broadcasts; client → host (Forwardable).</summary>
    [HarmonyPatch(typeof(ChainParent), "getHit")]
    public static class ChainGetHitSyncPatch
    {
        private static void Postfix(ChainParent __instance)
        {
            ChainSyncHelpers.SendState(__instance, "getHit");
        }
    }

    /// <summary>
    /// Chain / vine setup via <c>ChainParent.attach</c> (attachOnStart, scripted re-attach).
    /// Vine path may leave <c>attached</c> false until <see cref="VineLatchSyncPatch"/>.
    /// </summary>
    [HarmonyPatch(typeof(ChainParent), "attach")]
    public static class ChainAttachSyncPatch
    {
        private static void Postfix(ChainParent __instance)
        {
            ChainSyncHelpers.SendState(__instance, "attach");
        }
    }

    /// <summary>
    /// Covers timer auto-detach, health-zero detach inside getHit, and onDie.
    /// Prefix skips no-op when already detached.
    /// </summary>
    [HarmonyPatch(typeof(ChainParent), "detach")]
    public static class ChainDetachSyncPatch
    {
        private static void Prefix(ChainParent __instance, out bool __state)
        {
            __state = __instance != null && __instance.attached;
        }

        private static void Postfix(ChainParent __instance, bool __state)
        {
            if (!__state) return;
            ChainSyncHelpers.SendState(__instance, "detach");
        }
    }

    /// <summary>
    /// Vine latch sets <c>chainParent.attached = true</c> in Update without calling attach().
    /// Fan-out on false→true only (host and client; Forwardable Broadcast).
    /// </summary>
    [HarmonyPatch(typeof(Vine), "Update")]
    public static class VineLatchSyncPatch
    {
        private static void Prefix(Vine __instance, out bool __state)
        {
            __state = __instance != null
                && __instance.chainParent != null
                && __instance.chainParent.attached;
        }

        private static void Postfix(Vine __instance, bool __state)
        {
            if (__state) return;
            if (__instance == null || __instance.chainParent == null) return;
            if (!__instance.chainParent.attached) return;
            ChainSyncHelpers.SendState(__instance.chainParent, "vineLatch");
        }
    }
}
