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
    /// Dialogue / GameEvent door opens (bunker armored door etc.):
    /// host must fan-out DoorOpen + DoorState even when open() is delayed inside a
    /// GameEvent coroutine, and clients force-open if targets failed to resolve.
    /// </summary>
    [HarmonyPatch(typeof(Door), "open", new[] { typeof(Vector3), typeof(Transform), typeof(float) })]
    public static class DoorOpenSyncPatch
    {
        private static void Postfix(Door __instance)
        {
            BroadcastDoorOpened(__instance);
        }

        internal static void BroadcastDoorOpened(Door door)
        {
            if (door == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            Vector3 pos = door.transform.position;
            string name = door.name ?? "";

            net.Broadcast(NetMessageType.DoorOpen,
                w => new DoorOpenMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    DoorName = name
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Dual-path: DoorState carries body rot / force for peers that miss DoorOpen.
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
                OpenForce = 0f,
                BodyRotY = bodyRotY,
                AngVelX = angVel.x,
                AngVelY = angVel.y,
                AngVelZ = angVel.z
            });

            ModRuntime.LegacyInfo(
                $"[DoorSync] sent door open: {name} at ({pos.x:F1}, {pos.y:F1}, {pos.z:F1}) " +
                $"role={net.Role} dream={DreamSyncManager.IsDreamActive}");
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
            if (LanNetworkManager.IsApplyingRemoteState) return;

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
            if (LanNetworkManager.IsApplyingRemoteState) return;

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

    /// <summary>
    /// After host fires a dialogue-door GameEvent, poll for Door.opened and re-broadcast
    /// (covers delayed GameEvent coroutines where open() happens frames later).
    /// Client: after applying the same event, force-open nearby doors if still closed.
    /// </summary>
    public static class DialogueDoorAftermath
    {
        private static readonly HashSet<int> _broadcastedOpen =
            new HashSet<int>();

        public static bool IsDialogueDoorEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            return eventName.IndexOf("DoorDialogue", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("onLeaveDoor", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("door_underground", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("opening_door", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void OnHostGameEventsFired(string eventName)
        {
            if (!IsDialogueDoorEvent(eventName)) return;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            ctrl.StartCoroutine(HostPollOpenedDoors());
        }

        public static void OnClientGameEventsApplied(string eventName, Vector3 eventPos)
        {
            if (!IsDialogueDoorEvent(eventName)) return;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            ctrl.StartCoroutine(ClientForceOpenNearbyDoors(eventPos));
        }

        public static void Reset()
        {
            _broadcastedOpen.Clear();
        }

        private static IEnumerator HostPollOpenedDoors()
        {
            // GameEvent.fire uses WaitForSeconds(delay) — cover 0–3s of delayed open/unlock.
            float[] waits = { 0.05f, 0.25f, 0.5f, 1f, 1.5f, 2.5f, 3.5f };
            for (int w = 0; w < waits.Length; w++)
            {
                yield return new WaitForSecondsRealtime(waits[w] - (w > 0 ? waits[w - 1] : 0f));
                TryBroadcastAnyOpenedDoors();
            }
        }

        private static void TryBroadcastAnyOpenedDoors()
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (ModRuntime.Network.Role != NetworkRole.Host) return;

            Door[] all = Object.FindObjectsOfType<Door>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Door d = all[i];
                if (d == null || !d.opened) continue;
                int id = d.GetInstanceID();
                if (!_broadcastedOpen.Add(id)) continue;
                DoorOpenSyncPatch.BroadcastDoorOpened(d);
            }
        }

        private static IEnumerator ClientForceOpenNearbyDoors(Vector3 eventPos)
        {
            float[] waits = { 0.1f, 0.4f, 0.9f, 1.6f, 2.5f };
            for (int w = 0; w < waits.Length; w++)
            {
                yield return new WaitForSecondsRealtime(waits[w] - (w > 0 ? waits[w - 1] : 0f));
                ForceOpenDialogueDoors(eventPos);
            }
        }

        private static void ForceOpenDialogueDoors(Vector3 eventPos)
        {
            // Anchor: dialogue NPC door_underground, then event pos, then all dream doors.
            Vector3 anchor = eventPos;
            Character[] chars = Object.FindObjectsOfType<Character>(true);
            for (int i = 0; i < chars.Length; i++)
            {
                Character c = chars[i];
                if (c == null) continue;
                string n = c.name ?? "";
                if (n.IndexOf("door_underground", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("door", System.StringComparison.OrdinalIgnoreCase) >= 0
                       && n.IndexOf("underground", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    anchor = c.transform.position;
                    break;
                }
            }

            Door[] all = Object.FindObjectsOfType<Door>(true);
            int opened = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Door d = all[i];
                if (d == null) continue;

                float dist = Vector3.Distance(d.transform.position, anchor);
                // Bunker dialogue door sits near the NPC; event GO is at location origin (~far).
                bool nearAnchor = dist < 25f;
                bool nearEvent = Vector3.Distance(d.transform.position, eventPos) < 40f;
                string dn = d.name ?? "";
                bool nameMatch = dn.IndexOf("door", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || dn.IndexOf("underground", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || dn.IndexOf("metal", System.StringComparison.OrdinalIgnoreCase) >= 0;

                // Only doors near the dialogue NPC / event — never every "door*" in the pad.
                if (!nearAnchor && !(nearEvent && nameMatch))
                    continue;

                // Clear lock / block first (modifyDoor unlock/unblock may have missed targets).
                try
                {
                    d.unblock();
                    d.unlock();
                }
                catch { /* ignore */ }

                Locked locked = d.GetComponent<Locked>();
                if (locked != null)
                    locked.locked = false;
                Padlock pad = d.GetComponent<Padlock>();
                if (pad != null)
                    pad.locked = false;

                if (d.opened)
                    continue;

                bool prev = LanNetworkManager.IsApplyingRemoteState;
                LanNetworkManager.IsApplyingRemoteState = true;
                try
                {
                    float force = d.type == Door.Type.metal ? 30000f : 0f;
                    d.open(d.transform.position + Vector3.forward * 2f, null, force);
                    opened++;
                }
                finally
                {
                    LanNetworkManager.IsApplyingRemoteState = prev;
                }
            }

            if (opened > 0)
                ModRuntime.LegacyInfo(
                    $"[DoorSync] client force-opened {opened} door(s) after dialogue door event (anchor={anchor})");
        }
    }
}
