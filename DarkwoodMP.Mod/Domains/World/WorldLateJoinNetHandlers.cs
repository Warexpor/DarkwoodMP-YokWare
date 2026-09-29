using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Late-join world light / generator bulk sync composed for 0.8.</summary>
    internal sealed class WorldLateJoinNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldLateJoinNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>
        /// Host → new peer: all world lights/switchables currently isOn so late join
        /// matches hideout lamps without waiting for a toggle or generator event.
        /// </summary>
        internal void SyncExistingWorldLightsTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;

            Item[] items = WorldQueryHelper.GetCachedSceneComponents<Item>();
            int sent = 0;
            const int maxSend = 256;
            for (int i = 0; i < items.Length && sent < maxSend; i++)
            {
                Item item = items[i];
                if (item == null || item.gameObject == null || !item.gameObject.scene.IsValid())
                    continue;
                if (!item.isLight && !item.switchable)
                    continue;
                if (!item.isOn)
                    continue;
                if (item.GetComponent<Generator>() != null)
                    continue;

                Vector3 p = item.transform.position;
                string itemType = item.invItem != null ? item.invItem.type : "";
                string itemName = item.name ?? "";
                _net.SendToPlayer(targetPlayerId, NetMessageType.LightState,
                    w => new LightStateMessage
                    {
                        PosX = p.x,
                        PosY = p.y,
                        PosZ = p.z,
                        IsOn = true,
                        ItemName = itemName,
                        ItemType = itemType
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0 || Config.ModConfig.IsVerboseLightSync)
                ModLog.Event(LogCat.Session, $"[BulkSync] World lights → p{targetPlayerId}: on={sent}");
        }

        /// <summary>
        /// Host → peer: generator isOn/fuel so restorePower/cutPower matches before lamp bulk applies.
        /// Vanilla generators cycle power only; they must not overwrite per-lamp isOn via LightState.
        /// </summary>
        internal void SyncExistingGeneratorsTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;

            IList<Generator> gens = Sync.ListTracker<Generator>.GetAll();
            int sent = 0;
            const int maxSend = 32;
            for (int i = 0; i < gens.Count && sent < maxSend; i++)
            {
                Generator gen = gens[i];
                if (gen == null || gen.gameObject == null || !gen.gameObject.scene.IsValid())
                    continue;

                Vector3 p = gen.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(p.x * 10f) / 10f,
                    Mathf.Round(p.y * 10f) / 10f,
                    Mathf.Round(p.z * 10f) / 10f);
                Item itemComp = gen.GetComponent<Item>();
                string itemType = itemComp != null && itemComp.invItem != null ? itemComp.invItem.type : "";

                var gs = new Sync.GeneratorState
                {
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    IsOn = gen.isOn,
                    Fuel = gen.fuel,
                    LowPower = gen.lowPower,
                    ItemType = itemType
                };
                var msg = Sync.WorldPhysicsSyncService.StampSnapshot(
                    new Sync.PhysicsStateMessage { Generators = new[] { gs } });
                _net.SendToPlayer(targetPlayerId, NetMessageType.PhysicsState,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0 || Config.ModConfig.IsVerboseLightSync)
                ModLog.Event(LogCat.Session, $"[BulkSync] Generators → p{targetPlayerId}: {sent}");
        }

        /// <summary>
        /// Host: re-push isOn world lights (and gens) after a peer enters a location so
        /// LightState that missed while the grid was unloaded is recovered.
        /// </summary>
        internal void ResyncWorldLightsForPeer(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;
            SyncExistingGeneratorsTo(targetPlayerId);
            SyncExistingWorldLightsTo(targetPlayerId);
        }

        /// <summary>
        /// Host: peer's first local <c>spawnLocation</c> instantiates a virgin prefab.
        /// Late-join bulk often ran while that pad was absent (door/item find failed or
        /// pending queue capped). Re-send the same idempotent barricade / opened-door /
        /// NPC visual snapshots lights already get via <see cref="ResyncWorldLightsForPeer"/>.
        /// Container loot stays on open <c>ContainerStateRequest</c> (no bulk here).
        /// </summary>
        internal void ResyncOutsideLocationPadForPeer(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return;

            int barrDoors = _net.BarricadeHandlers.SendBarricadeDoorsTo(targetPlayerId);
            int barrWindows = _net.BarricadeHandlers.SendBarricadeWindowsTo(targetPlayerId);
            int barrItems = _net.BarricadeHandlers.SendBarricadeItemsTo(targetPlayerId);
            int opened = SendOpenedDoorStatesNearLocationTo(targetPlayerId, loc);
            _net.BulkSyncHandlers.SendReputationBulkSyncTo(targetPlayerId);

            ModLog.Event(LogCat.Session,
                "[LocationSync] pad resync → p" + targetPlayerId
                + " barrDoor=" + barrDoors
                + " win=" + barrWindows
                + " item=" + barrItems
                + " opened=" + opened
                + " loc=" + (loc.gameObject != null ? loc.gameObject.name : loc.name));
        }

        /// <summary>
        /// Host→peer: DoorState for opened doors under/near the pad. Continuous
        /// PhysicsState only re-sends on change, so sticky opens from before the
        /// peer spawned the pad never arrive otherwise.
        /// </summary>
        private int SendOpenedDoorStatesNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = 2500f * 2500f;

            Door[] doors = WorldQueryHelper.GetCachedSceneComponents<Door>();
            int sent = 0;
            const int maxSend = 128;
            for (int i = 0; i < doors.Length && sent < maxSend; i++)
            {
                Door door = doors[i];
                if (door == null || door.transform == null) continue;
                if (!IsUnderOrNearLocation(door.transform, root, anchor, maxDistSqr))
                    continue;
                if (!TraverseHack.ReadDoorOpened(door))
                    continue;

                Vector3 p = door.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(p.x * 10f) / 10f,
                    Mathf.Round(p.y * 10f) / 10f,
                    Mathf.Round(p.z * 10f) / 10f);
                float bodyRotY = door.body != null ? door.body.eulerAngles.y : 0f;
                Vector3 angVel = Vector3.zero;
                if (door.body != null)
                {
                    Rigidbody rb = door.body.GetComponent<Rigidbody>();
                    if (rb != null) angVel = rb.angularVelocity;
                }

                var ds = new DoorState
                {
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    Opened = true,
                    BodyRotY = bodyRotY,
                    AngVelX = angVel.x,
                    AngVelY = angVel.y,
                    AngVelZ = angVel.z
                };
                var msg = WorldPhysicsSyncService.StampSnapshot(
                    new PhysicsStateMessage { Doors = new[] { ds } });
                _net.SendToPlayer(targetPlayerId, NetMessageType.PhysicsState,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                sent++;
            }

            return sent;
        }

        private static bool IsUnderOrNearLocation(
            Transform t, Transform root, Vector3 anchor, float maxDistSqr)
        {
            if (t == null) return false;
            if (root != null && (t == root || t.IsChildOf(root)))
                return true;
            float dx = t.position.x - anchor.x;
            float dz = t.position.z - anchor.z;
            return dx * dx + dz * dz <= maxDistSqr;
        }
    }
}
