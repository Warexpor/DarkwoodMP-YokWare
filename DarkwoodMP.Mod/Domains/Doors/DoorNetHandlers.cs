using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Door open/unblock handlers composed for 0.8.</summary>
    internal sealed class DoorNetHandlers
    {
        private const float NamedDoorFallbackRadius = 2f;

        private readonly LanNetworkManager _net;

        internal DoorNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleDoorOpen(DoorOpenMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            string doorName = msg.DoorName ?? "";
            bool unblockOnly = doorName.StartsWith("unblock:", System.StringComparison.OrdinalIgnoreCase);
            if (unblockOnly)
                doorName = doorName.Substring("unblock:".Length);

            // Mid-dream: ignore overworld DoorOpen (name "Wooden door" matched the wrong bunker door).
            Transform dreamRoot = DreamSyncManager.GetDreamLocationTransform();
            if (DreamSyncManager.IsDreamActive && dreamRoot != null
                && Vector3.Distance(pos, dreamRoot.position) > 250f)
            {
                ModRuntime.LegacyInfo(
                    $"[DoorSync] drop non-dream DoorOpen '{msg.DoorName}' at {pos}");
                return;
            }

            // Tracker (tight first) then overlap; an active door wins over an inactive twin.
            Door door = WorldQueryHelper.FindDoorByPosLoose(pos, 4f);
            if (door == null)
            {
                Door[] all = WorldQueryHelper.GetCachedSceneComponents<Door>();
                string want = DialogOutcomeCloseNetHandlers.StripCloneSuffix(doorName);
                Door named = null;
                float bestNamed = float.MaxValue;
                for (int i = 0; i < all.Length; i++)
                {
                    Door d = all[i];
                    if (d == null) continue;
                    if (dreamRoot != null && DreamSyncManager.IsDreamActive
                        && !d.transform.IsChildOf(dreamRoot)
                        && Vector3.Distance(d.transform.position, dreamRoot.position) > 200f)
                        continue;
                    float dist = Vector3.Distance(d.transform.position, pos);
                    // A name alone is weak (many "Wooden door"s share it): only a body offset
                    // from the event position, never another door across the room.
                    if (dist > NamedDoorFallbackRadius) continue;
                    if (!string.IsNullOrEmpty(want))
                    {
                        string n = DialogOutcomeCloseNetHandlers.StripCloneSuffix(d.gameObject.name);
                        if (string.Equals(n, want, System.StringComparison.OrdinalIgnoreCase) && dist < bestNamed)
                        {
                            bestNamed = dist;
                            named = d;
                        }
                    }
                }
                if (named != null)
                    door = named;
            }
            // Dream event positions can be the location origin, while the door
            // body is offset, so widen the local search.
            // but stay under the dream pad.
            if (door == null && DreamSyncManager.IsDreamActive)
                door = WorldQueryHelper.FindDoorByPosLoose(pos, 40f);

            if (door == null)
            {
                ModLog.WarnRate(LogCat.World, "door-open-miss:" + msg.DoorName,
                    $"[DoorSync] Door '{msg.DoorName}' not found at {pos}");
                return;
            }

            if (dreamRoot != null && DreamSyncManager.IsDreamActive
                && !door.transform.IsChildOf(dreamRoot)
                && Vector3.Distance(door.transform.position, dreamRoot.position) > 200f)
            {
                ModRuntime.LegacyInfo(
                    $"[DoorSync] reject door outside dream pad '{door.name}' for msg={msg.DoorName}");
                return;
            }

            if (msg.AttemptOnly)
            {
                if (_net.Role == NetworkRole.Host)
                {
                    _net.SuppressRelay();
                    DialogHostApplyGuard.RunHostWorldFanout(() =>
                        Core.sendTriggerInfo(door.gameObject, EventTrigger.Type.onTryToOpenLocked));
                    ModRuntime.LegacyInfo(
                        $"[DoorSync] locked attempt trigger '{door.name}' at {pos}");
                }
                return;
            }

            // Suppress re-Broadcast (dream doors are bidirectional).
            bool prev = LanNetworkManager.IsApplyingRemoteState;
            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                // Locks travel on their own messages (LockedUnlock, PadlockUnlock) and "blocked" on
                // the unblock form of this one. A plain open no longer clears them: a story event
                // forcing a locked door open left every peer unlocked while the host stayed locked.
                if (unblockOnly)
                {
                    try { door.unblock(); } catch { /* ignore */ }
                    ModRuntime.LegacyInfo($"[DoorSync] unblocked door '{door.name}' (msg={msg.DoorName}) at {pos}");
                    return;
                }

                if (door.opened)
                {
                    ModRuntime.LegacyInfo($"[DoorSync] already open '{door.name}' at {pos}");
                    return;
                }

                // Boarded up (or broken) meanwhile, e.g. a teammate finished the barricade while
                // this open was on its way: vanilla never opens such a door.
                if (door.barricaded || door.destroyed)
                {
                    ModRuntime.LegacyInfo($"[DoorSync] not opening barricaded/destroyed '{door.name}' at {pos}");
                    return;
                }

                // Wire OpenForce + opener (thump 45000 → door_hit_run).
                float force = msg.OpenForce;
                Vector3 openerPos = new Vector3(msg.OpenerPosX, msg.OpenerPosY, msg.OpenerPosZ);
                // Leave-door GE already played openSound; mute Door.open audio on apply.
                string prevSound = null;
                bool mute = DWMPHorde.Patches.DialogueDoorAftermath.ShouldMuteRemoteDoorOpenSound;
                if (mute)
                {
                    prevSound = door.openSound;
                    door.openSound = "";
                }
                try
                {
                    if (_net.Role == NetworkRole.Host)
                        DialogHostApplyGuard.BeginWorldOnly();
                    door.open(openerPos, null, force);
                    // Vanilla alerts creatures only when the opener is a Player; here it is a peer
                    // whose open the host carries out, so creatures by the door heard nothing.
                    if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0
                        && _net.CurrentReceivePlayerId != _net.LocalPlayerId)
                    {
                        bool thump = force >= WorldPhysicsSyncService.DoorThumpForce;
                        Character.alertInArea(door.transform.position,
                            thump ? door.openRunSoundDistance : door.openSoundDistance, dangerousSound: false, 1f);
                    }
                }
                finally
                {
                    if (_net.Role == NetworkRole.Host)
                        DialogHostApplyGuard.EndWorldOnly();
                    if (mute)
                        door.openSound = prevSound;
                }
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = prev;
            }

            ModRuntime.LegacyInfo($"[DoorSync] opened door '{door.name}' (msg={msg.DoorName}) at {pos}");
        }

    }
}
