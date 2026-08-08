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
        private static void Prefix(Door __instance, out bool __state)
        {
            __state = __instance != null && TraverseHack.ReadDoorOpened(__instance);
        }

        private static void Postfix(Door __instance, bool __state)
        {
            // Already open before this call — skip rebroadcast (client spam / already-open host).
            if (__state) return;
            BroadcastDoorOpened(__instance);
        }

        internal static void BroadcastDoorOpened(Door door)
        {
            if (door == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (TraverseHack.ApplyingFromNetwork) return;
            // Leave-door GE already syncs via GameEventsFired — DoorOpen is a second openSound.
            if (DialogueDoorAftermath.SuppressDialogueDoorOpenBroadcast)
                return;
            // ProcessInboundMessage holds IsApplyingRemoteState for all inbound applies.
            // DialogOutcome world-only Door.open is host-authoritative and MUST fan out
            // (was silently dropped after NetworkApplyGuard became a real class in 0.7.8).
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            Vector3 pos = door.transform.position;
            string name = door.name ?? "";

            // During dreams only fan-out doors that belong to the dream pad.
            // Entry transition: IsDreamActive but dreamLocation not ready — suppress all
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

    /// <summary>
    /// After host fires a dialogue-door GameEvent, poll for Door.opened and re-broadcast
    /// (covers delayed GameEvent coroutines where open() happens frames later).
    /// Client: after applying the same event, force-open nearby doors if still closed.
    /// </summary>
    public static class DialogueDoorAftermath
    {
        private static readonly HashSet<int> _broadcastedOpen =
            new HashSet<int>();

        /// <summary>
        /// Leave-door GE already owns openSound/setActive. Skip DoorOpen fan-out + ForceOpen
        /// for a few seconds so the client does not hear openSound twice.
        /// </summary>
        private static float _leaveDoorGeTime = -999f;
        private const float LeaveDoorMuteSec = 3.5f;

        public static bool IsDialogueDoorEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            return eventName.IndexOf("DoorDialogue", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("onLeaveDoor", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("door_underground", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("opening_door", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool SuppressDialogueDoorOpenBroadcast =>
            Time.unscaledTime - _leaveDoorGeTime < LeaveDoorMuteSec;

        public static bool ShouldMuteRemoteDoorOpenSound =>
            Time.unscaledTime - _leaveDoorGeTime < LeaveDoorMuteSec;

        public static void NoteLeaveDoorGameEvent()
        {
            _leaveDoorGeTime = Time.unscaledTime;
            _clientDoorOpened = true;
        }

        public static void OnHostGameEventsFired(string eventName)
        {
            if (!IsDialogueDoorEvent(eventName)) return;
            if (eventName.IndexOf("onLeaveDoor", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("DoorDialogue", System.StringComparison.OrdinalIgnoreCase) >= 0)
                NoteLeaveDoorGameEvent();
            ArmHostDoorPoll();
        }

        /// <summary>
        /// After DialogOutcome world-only displayDialogue — fireWorldEvent / modifyDoor
        /// may open the bunker door with delays; poll even when GE names are not matched.
        /// </summary>
        public static void OnHostDialogWorldApplied()
        {
            ArmHostDoorPoll();
        }

        private static void ArmHostDoorPoll()
        {
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            ctrl.StartCoroutine(HostPollOpenedDoors());
        }

        public static void OnClientGameEventsApplied(string eventName, Vector3 eventPos)
        {
            if (!IsDialogueDoorEvent(eventName)) return;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            // Defer WorldGrid work off the GE apply frame — synchronous enterAllNodes
            // during leave-door was hitching both peers for hundreds of ms.
            ctrl.StartCoroutine(ClientAfterDialogueDoorRoutine(eventPos));
        }

        /// <summary>
        /// Leave-door GE already runs modifyDoor → openSound. Mark GE as owner of the open
        /// so ForceOpen / DoorOpen do not play a second openSound (setActive doors often
        /// leave Door.opened=false, which previously always ForceOpened).
        /// </summary>
        public static void OnClientLeaveDoorGameEventsApplied(string eventName, Vector3 eventPos)
        {
            if (!IsDialogueDoorEvent(eventName)) return;
            NoteLeaveDoorGameEvent();
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            ctrl.StartCoroutine(ClientLeaveDoorBackupOpenRoutine(eventPos));
        }

        private static IEnumerator ClientLeaveDoorBackupOpenRoutine(Vector3 eventPos)
        {
            yield return new WaitForSecondsRealtime(1.2f);
            if (_clientDoorOpened) yield break;
            // If a dream-pad door near the event is already open, GE succeeded — no ForceOpen.
            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            Door[] all = GetDoorsCached();
            for (int i = 0; i < all.Length; i++)
            {
                Door d = all[i];
                if (d == null || !d.opened) continue;
                if (dreamRoot != null && !d.transform.IsChildOf(dreamRoot)
                    && Vector3.Distance(d.transform.position, dreamRoot.position) > 200f)
                    continue;
                if (Vector3.Distance(d.transform.position, eventPos) < 120f)
                {
                    _clientDoorOpened = true;
                    yield break;
                }
            }
            ForceOpenDialogueDoors(eventPos);
        }

        public static void Reset()
        {
            _broadcastedOpen.Clear();
            _doorFindCache = null;
            _doorFindCacheTime = -999f;
            _clientDoorOpened = false;
            _leaveDoorGeTime = -999f;
        }

        private static Door[] _doorFindCache;
        private static float _doorFindCacheTime = -999f;
        private static bool _clientDoorOpened;
        private const float DoorFindCacheTtl = 2.5f;

        private static Door[] GetDoorsCached()
        {
            if (_doorFindCache != null && Time.unscaledTime - _doorFindCacheTime < DoorFindCacheTtl)
                return _doorFindCache;
            _doorFindCache = Object.FindObjectsOfType<Door>(true);
            _doorFindCacheTime = Time.unscaledTime;
            return _doorFindCache;
        }

        private static IEnumerator ClientAfterDialogueDoorRoutine(Vector3 eventPos)
        {
            // One deferred grid refresh (not every force-open retry).
            yield return null;
            try
            {
                if (DreamSyncManager.IsDreamActive && Singleton<WorldGrid>.Instance != null)
                    Singleton<WorldGrid>.Instance.enterAllNodes();
            }
            catch { /* ignore */ }

            float[] waits = { 0.1f, 0.4f, 0.9f };
            float elapsed = 0f;
            for (int w = 0; w < waits.Length; w++)
            {
                float step = waits[w] - elapsed;
                elapsed = waits[w];
                if (step > 0f)
                    yield return new WaitForSecondsRealtime(step);
                if (_clientDoorOpened) yield break;
                ForceOpenDialogueDoors(eventPos);
            }
        }

        private static IEnumerator HostPollOpenedDoors()
        {
            // GameEvent.fire uses WaitForSeconds(delay) — cover 0–2s of delayed open/unlock.
            // Stop once we have broadcast an open dream door (avoid repeated FindObjects).
            float[] waits = { 0.05f, 0.25f, 0.5f, 1f, 2f };
            for (int w = 0; w < waits.Length; w++)
            {
                yield return new WaitForSecondsRealtime(waits[w] - (w > 0 ? waits[w - 1] : 0f));
                int before = _broadcastedOpen.Count;
                TryBroadcastAnyOpenedDoors();
                if (_broadcastedOpen.Count > before)
                    yield break;
            }
        }

        private static void TryBroadcastAnyOpenedDoors()
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (ModRuntime.Network.Role != NetworkRole.Host) return;

            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            Door[] all = GetDoorsCached();
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

        private static void ForceOpenDialogueDoors(Vector3 eventPos)
        {
            TryForceOpenDialogueDoor(eventPos, broadcast: false);
        }

        /// <summary>
        /// Host: unlock/open the dream bunker door near the dialogue NPC and fan out DoorOpen.
        /// Used when onCloseDialogue / onLeaveDoorDialogue GE did not open anything.
        /// </summary>
        public static void HostEnsureDialogueDoorOpen(Vector3 anchor)
        {
            TryForceOpenDialogueDoor(anchor, broadcast: true);
        }

        private static void TryForceOpenDialogueDoor(Vector3 eventPos, bool broadcast)
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

            Door[] all = GetDoorsCached();
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

            if (best == null)
                return;

            if (!best.opened)
            {
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

                // Host broadcast path must NOT set IsApplyingRemoteState (that blocks DoorOpen
                // unless DialogHostApplyGuard is also active). Client apply path stays silent.
                if (broadcast)
                {
                    float force = best.type == Door.Type.metal ? 30000f : 0f;
                    best.open(best.transform.position + Vector3.forward * 2f, null, force);
                    // DoorOpenSyncPatch.Postfix already broadcast — do not dual-send.
                    ModRuntime.LegacyInfo(
                        $"[DoorSync] host force-opened dialogue door '{best.name}' (anchor={anchor})");
                }
                else
                {
                    // GE modifyDoor may already be opening — skip second openSound.
                    if (best.opened || ShouldMuteRemoteDoorOpenSound)
                    {
                        _clientDoorOpened = true;
                        return;
                    }
                    bool prev = LanNetworkManager.IsApplyingRemoteState;
                    LanNetworkManager.IsApplyingRemoteState = true;
                    try
                    {
                        float force = best.type == Door.Type.metal ? 30000f : 0f;
                        string prevSound = best.openSound;
                        best.openSound = "";
                        try
                        {
                            best.open(best.transform.position + Vector3.forward * 2f, null, force);
                        }
                        finally
                        {
                            best.openSound = prevSound;
                        }
                    }
                    finally
                    {
                        LanNetworkManager.IsApplyingRemoteState = prev;
                    }
                    _clientDoorOpened = true;
                    ModRuntime.LegacyInfo(
                        $"[DoorSync] client force-opened dialogue door '{best.name}' (anchor={anchor})");
                }
            }
            else if (broadcast)
            {
                // Already open locally — still fan out for peers that missed it.
                DoorOpenSyncPatch.BroadcastDoorOpened(best);
            }
            else
            {
                _clientDoorOpened = true;
            }
        }
    }
}
