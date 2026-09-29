using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host combat ThrownItem that leaves the world (stick-into-char, water, destroyOnLand)
    /// must clear peer FX copies. Those copies are MuteThrownCombat visualOnly / client-own
    /// throws — still pickable via getDroppedItem — and DestroyObjectByPos cannot find the
    /// host knife once it is already inside a Character inventory (dual grant).
    /// </summary>
    [HarmonyPatch(typeof(ThrownItem), "onCollide", typeof(Collider), typeof(Vector3))]
    public static class ThrownItemCombatDespawnSyncPatch
    {
        private struct State
        {
            public bool Track;
            public Vector3 Pos;
            public string ObjectName;
            public string ItemType;
            public int Amount;
            public float Durability;
            public int Ammo;
        }

        [HarmonyPrefix]
        private static void Prefix(ThrownItem __instance, ref State __state)
        {
            __state = default;
            if (__instance == null || __instance.gameObject == null)
                return;
            if (WorldPhysicsSyncService.IsMutedThrownFx(__instance.gameObject))
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            // Only in-flight → land transitions. Saved / already-grounded skips.
            if (!__instance.thrown)
                return;

            __state.Track = true;
            __state.Pos = __instance.transform.position;
            DroppedItemSyncHelpers.CaptureWorldPickupItemMeta(
                __instance.GetComponent<Item>(),
                out __state.ItemType, out __state.Amount, out __state.Durability, out __state.Ammo);
            __state.ObjectName = !string.IsNullOrEmpty(__state.ItemType)
                ? __state.ItemType
                : __instance.gameObject.name;
        }

        [HarmonyPostfix]
        private static void Postfix(ThrownItem __instance, State __state)
        {
            if (!__state.Track)
                return;
            // Still in the world (ground / wall stick) — peer FX claim + DestroyObjectByPos OK.
            if (__instance != null)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (string.IsNullOrEmpty(__state.ObjectName))
                return;

            WorldPhysicsSyncService.TryConsumeWorldPickup(
                __state.Pos.x, __state.Pos.y, __state.Pos.z, __state.ObjectName);

            net.SendWorldObjectRemoved(new WorldObjectRemovedMessage
            {
                PosX = __state.Pos.x,
                PosY = __state.Pos.y,
                PosZ = __state.Pos.z,
                ObjectName = __state.ObjectName,
                Mode = WorldObjectRemovedMessage.ModeRemove,
                ClaimedByPlayerId = net.LocalPlayerId,
                ItemType = __state.ItemType ?? "",
                Amount = __state.Amount > 0 ? __state.Amount : 1,
                Durability = __state.Durability,
                Ammo = __state.Ammo
            });
            ModRuntime.LegacyInfo("[ThrownDespawn] host combat left world → WOR "
                + __state.ObjectName + " at " + __state.Pos);
        }
    }
}
