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

            // During dreams only fan-out doors that belong to the dream pad.
            if (DreamSyncManager.IsDreamActive)
            {
                Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
                if (dreamRoot != null
                    && !door.transform.IsChildOf(dreamRoot)
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

            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            Door[] all = Object.FindObjectsOfType<Door>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Door d = all[i];
                if (d == null || !d.opened) continue;
                // Only dream-pad doors — overworld opened doors were flooding DoorOpen and
                // clients name-matched the wrong "Wooden door" inside the bunker.
                if (dreamRoot != null && !d.transform.IsChildOf(dreamRoot)
                    && Vector3.Distance(d.transform.position, dreamRoot.position) > 200f)
                    continue;
                if (dreamRoot == null && !DreamSyncManager.IsDreamActive)
                    continue;
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
            // Anchor: dialogue NPC door_underground under the dream pad only.
            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            Vector3 anchor = eventPos;
            bool foundNpc = false;
            float bestNpcDist = float.MaxValue;
            Character[] chars = Object.FindObjectsOfType<Character>(true);
            for (int i = 0; i < chars.Length; i++)
            {
                Character c = chars[i];
                if (c == null) continue;
                string n = c.name ?? "";
                if (n.IndexOf("door_underground", System.StringComparison.OrdinalIgnoreCase) < 0
                    && !(n.IndexOf("door", System.StringComparison.OrdinalIgnoreCase) >= 0
                        && n.IndexOf("underground", System.StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                if (dreamRoot != null
                    && !c.transform.IsChildOf(dreamRoot)
                    && Vector3.Distance(c.transform.position, dreamRoot.position) > 200f)
                    continue;
                float d = Vector3.Distance(c.transform.position, eventPos);
                if (d < bestNpcDist)
                {
                    bestNpcDist = d;
                    anchor = c.transform.position;
                    foundNpc = true;
                }
            }

            // Without the bunker dialogue NPC, do not spray-open doors (wrong-door desync).
            if (!foundNpc)
                return;

            Door[] all = Object.FindObjectsOfType<Door>(true);
            int opened = 0;
            Door best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < all.Length; i++)
            {
                Door d = all[i];
                if (d == null) continue;
                if (dreamRoot != null && !d.transform.IsChildOf(dreamRoot)
                    && Vector3.Distance(d.transform.position, dreamRoot.position) > 200f)
                    continue;

                float dist = Vector3.Distance(d.transform.position, anchor);
                if (dist > 18f) continue;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }

            if (best == null || best.opened)
                return;

            try
            {
                best.unblock();
                best.unlock();
            }
            catch { /* ignore */ }

            Locked locked = best.GetComponent<Locked>();
            if (locked != null)
                locked.locked = false;
            Padlock pad = best.GetComponent<Padlock>();
            if (pad != null)
                pad.locked = false;

            bool prev = LanNetworkManager.IsApplyingRemoteState;
            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                float force = best.type == Door.Type.metal ? 30000f : 0f;
                best.open(best.transform.position + Vector3.forward * 2f, null, force);
                opened++;
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = prev;
            }

            if (opened > 0)
                ModRuntime.LegacyInfo(
                    $"[DoorSync] client force-opened dialogue door '{best.name}' (anchor={anchor})");
        }
    }
}
