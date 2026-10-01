using System;
using System.Collections.Generic;
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
            public int InstanceId;
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
            __state.InstanceId = __instance.gameObject.GetInstanceID();
            Tracked.Add(__state.InstanceId);
            __state.Pos = __instance.transform.position;
            DroppedItemSyncHelpers.CaptureWorldPickupItemMeta(
                __instance.GetComponent<Item>(),
                out __state.ItemType, out __state.Amount, out __state.Durability, out __state.Ammo);
            __state.ObjectName = !string.IsNullOrEmpty(__state.ItemType)
                ? __state.ItemType
                : __instance.gameObject.name;
        }

        // Instance ids of ThrownItems currently inside a tracked onCollide, and the subset whose
        // GameObject vanilla queued for destruction via Helpers.DestroyMe (water, stuck into a
        // character, destroyOnLand). Object.Destroy is deferred, so the instance is never fake-null
        // right after onCollide returns; the destroy request itself is the vanilla decision.
        private static readonly HashSet<int> Tracked = new HashSet<int>();
        private static readonly HashSet<int> DestroyQueued = new HashSet<int>();

        /// <summary>Session end: drop anything a torn-down onCollide never released.</summary>
        internal static void Reset()
        {
            Tracked.Clear();
            DestroyQueued.Clear();
        }

        internal static void NoteDestroyMe(GameObject go)
        {
            if (Tracked.Count == 0 || go == null)
                return;
            int id = go.GetInstanceID();
            if (Tracked.Contains(id))
                DestroyQueued.Add(id);
        }

        // Finalizer (not Postfix): tracking must be released even if onCollide throws.
        [HarmonyFinalizer]
        private static void Finalizer(ThrownItem __instance, State __state, Exception __exception)
        {
            if (!__state.Track)
                return;
            int id = __state.InstanceId;
            Tracked.Remove(id);
            bool destroyQueued = DestroyQueued.Remove(id);
            // No destroy request = still in the world (ground / wall stick): peer FX claim +
            // DestroyObjectByPos handle it, nothing to announce.
            if (__exception != null || !destroyQueued)
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

    /// <summary>Records vanilla's destroy request for a ThrownItem inside a tracked onCollide.</summary>
    [HarmonyPatch(typeof(Helpers), "DestroyMe", typeof(GameObject))]
    internal static class ThrownItemDestroyMeTrackPatch
    {
        private static void Prefix(GameObject go) => ThrownItemCombatDespawnSyncPatch.NoteDestroyMe(go);
    }
}
