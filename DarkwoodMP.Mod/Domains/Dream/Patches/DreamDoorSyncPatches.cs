using System.Collections;
using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Single Door.open fan-out for hinged + dialogue/GE doors.
    /// Sends DoorOpen + DoorState once (with real OpenForce for hit sounds).
    /// Replaces the former dual Harmony pair (DoorOpenPatch + DoorOpenSyncPatch)
    /// that double-emitted DoorState on every open.
    /// </summary>
    [HarmonyPatch(typeof(Door), "open", new[] { typeof(Vector3), typeof(Transform), typeof(float) })]
    public static class DoorOpenSyncPatch
    {
        private static void Prefix(Door __instance, out bool __state)
        {
            __state = __instance != null && TraverseHack.ReadDoorOpened(__instance);
        }

        private static void Postfix(Door __instance, object[] __args, bool __state)
        {
            // Already open before this call; skip rebroadcast to avoid client spam.
            if (__state) return;
            float openForce = __args != null && __args.Length > 2 ? (float)__args[2] : 0f;
            BroadcastDoorOpened(__instance, openForce);
        }

        internal static void BroadcastDoorOpened(Door door, float openForce = 0f)
        {
            if (door == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (TraverseHack.ApplyingFromNetwork) return;
            // Leave-door GE already syncs via GameEventsFired; DoorOpen would play openSound twice.
            if (DialogueDoorAftermath.SuppressDialogueDoorOpenBroadcast)
                return;
            // ProcessInboundMessage holds IsApplyingRemoteState for all inbound applies.
            // DialogOutcome world-only Door.open is host-authoritative and MUST fan out
            // (was silently dropped when NetworkApplyGuard became a real class).
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            Vector3 pos = door.transform.position;
            string name = door.name ?? "";

            // During dreams only fan-out doors that belong to the dream pad.
            // Entry transition: IsDreamActive but dreamLocation not ready. Suppress all
            // door fan-out so overworld twins do not leak mid-video.
            if (DreamSyncManager.IsDreamActive)
            {
                Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
                if (dreamRoot == null)
                    return;
                if (!door.transform.IsChildOf(dreamRoot)
                    && Vector3.Distance(pos, dreamRoot.position) > 200f)
                    return;
            }

            net.Broadcast(NetMessageType.DoorOpen,
                w => new DoorOpenMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    DoorName = name
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // DoorState carries body rot / force for peers that miss DoorOpen /
            // need PhysicsState-style apply (door_hit_run needs real OpenForce).
            float bodyRotY = 0f;
            Vector3 angVel = Vector3.zero;
            if (door.body != null)
            {
                bodyRotY = door.body.eulerAngles.y;
                Rigidbody rb = door.body.GetComponent<Rigidbody>();
                if (rb != null) angVel = rb.angularVelocity;
            }

            Vector3 opener = Player.Instance != null
                ? Player.Instance.transform.position
                : pos;

            net.SendDoorState(new DoorState
            {
                PosX = Mathf.Round(pos.x * 10f) / 10f,
                PosY = Mathf.Round(pos.y * 10f) / 10f,
                PosZ = Mathf.Round(pos.z * 10f) / 10f,
                Opened = true,
                OpenerPosX = opener.x,
                OpenerPosY = opener.y,
                OpenerPosZ = opener.z,
                OpenForce = openForce,
                BodyRotY = bodyRotY,
                AngVelX = angVel.x,
                AngVelY = angVel.y,
                AngVelZ = angVel.z
            });

            ModRuntime.LegacyInfo(
                $"[DoorSync] sent door open: {name} at ({pos.x:F1}, {pos.y:F1}, {pos.z:F1}) " +
                $"force={openForce} role={net.Role} dream={DreamSyncManager.IsDreamActive}");
        }
    }

    /// <summary>GameEvent.modifyDoor unlock path — peers must clear Locked too.</summary>
    [HarmonyPatch(typeof(Door), "unlock")]
    public static class DoorUnlockSyncPatch
    {
        private static void Postfix(Door __instance)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active)
                return;

            Vector3 pos = __instance.transform.position;
            ModRuntime.Network.SendLockedUnlock(new LockedUnlockMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z
            });
            ModRuntime.LegacyInfo($"[DoorSync] sent unlock: {__instance.name}");
        }
    }

    /// <summary>GameEvent.modifyDoor unblock — peers must clear blocked flag.</summary>
    [HarmonyPatch(typeof(Door), "unblock")]
    public static class DoorUnblockSyncPatch
    {
        private static void Postfix(Door __instance)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active)
                return;

            // Re-use DoorOpen with name prefix so client applies unblock+open attempt.
            // Dedicated message would need protocol bump; open handler also unblocks.
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            Vector3 pos = __instance.transform.position;
            net.Broadcast(NetMessageType.DoorOpen,
                w => new DoorOpenMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    DoorName = "unblock:" + (__instance.name ?? "")
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo($"[DoorSync] sent unblock: {__instance.name}");
        }
    }
}
