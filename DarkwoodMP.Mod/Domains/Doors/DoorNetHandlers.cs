using DWMPHorde;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Door open/unblock handlers composed for 0.8.</summary>
    internal sealed class DoorNetHandlers
    {
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

            Door door = Sync.ListTracker<Door>.FindByPosition(pos);
            if (door == null)
                door = WorldQueryHelper.FindDoorByPosLoose(pos, 4f);
            if (door == null)
            {
                Door[] all = WorldQueryHelper.GetCachedSceneComponents<Door>();
                string want = DialogOutcomeNetHandlers.StripCloneSuffix(doorName);
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
                    if (dist > 20f) continue;
                    if (!string.IsNullOrEmpty(want))
                    {
                        string n = DialogOutcomeNetHandlers.StripCloneSuffix(d.name);
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
                ModRuntime.Log?.LogWarning($"[DoorSync] Door '{msg.DoorName}' not found at {pos}");
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

            // Suppress re-Broadcast (dream doors are bidirectional).
            bool prev = LanNetworkManager.IsApplyingRemoteState;
            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                try { door.unblock(); } catch { /* ignore */ }
                try { door.unlock(); } catch { /* ignore */ }
                Locked locked = door.GetComponent<Locked>();
                if (locked != null) locked.locked = false;
                Padlock pad = door.GetComponent<Padlock>();
                if (pad != null) pad.locked = false;

                if (unblockOnly)
                {
                    ModRuntime.LegacyInfo($"[DoorSync] unblocked door '{door.name}' (msg={msg.DoorName}) at {pos}");
                    return;
                }

                if (door.opened)
                {
                    ModRuntime.LegacyInfo($"[DoorSync] already open '{door.name}' at {pos}");
                    return;
                }

                float force = door.type == Door.Type.metal ? 30000f : 0f;
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
                    door.open(pos, null, force);
                }
                finally
                {
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
