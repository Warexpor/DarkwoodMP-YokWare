using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Entity / item / trap / door outbound sends (incl. despawns) composed for 0.8.</summary>
    internal sealed class WorldObjectSendNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldObjectSendNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleTrapBulk(TrapBulkMessage msg)
        {
            if (msg.Entries == null || msg.Entries.Length == 0) return;
            int applied = 0, pending = 0;
            try
            {
                TraverseHack.ApplyingFromNetwork = true;
                for (int i = 0; i < msg.Entries.Length; i++)
                {
                    var e = msg.Entries[i];
                    Vector3 pos = new Vector3(e.PosX, e.PosY, e.PosZ);
                    GameObject go = e.TrapNetId > 0
                        ? Sync.TrapNetworkId.FindById(e.TrapNetId)
                        : null;
                    if (go == null)
                        go = WorldPhysicsSyncService.FindTrapByPos(pos);
                    if (go == null)
                    {
                        Sync.TrapNetworkId.QueuePending(e.TrapNetId, pos, e.Triggered);
                        pending++;
                        continue;
                    }
                    if (e.TrapNetId > 0)
                        Sync.TrapNetworkId.Ensure(go, e.TrapNetId);
                    WorldPhysicsSyncService.ApplyTrapState(go, e.Triggered);
                    applied++;
                }
            }
            finally
            {
                TraverseHack.ApplyingFromNetwork = false;
            }
            ModLog.Event(LogCat.Session, "[BulkSync] Trap bulk applied=" + applied + " pending=" + pending);
        }

        internal void SendThrowableDespawn(ThrowableDespawnMessage msg)
        {
            if (!_net.IsConnected) return;
            _net.Broadcast(NetMessageType.ThrowableDespawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void SendEntityDespawn(short entityId)
        {
            if (_net.Role != NetworkRole.Host || !_net.IsConnected || entityId == 0) return;
            var msg = new EntityDespawnMessage { EntityId = entityId };
            _net.Broadcast(NetMessageType.EntityDespawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal void HandleEntityDespawn(EntityDespawnMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;
            ClientEntityInterpolationService.ApplyHostDespawn(msg.EntityId);
        }

        internal void HandleThrowableDespawn(ThrowableDespawnMessage msg)
        {
            WorldPhysicsSyncService.ApplyThrownDespawn(msg);
        }

        internal void SendItemSpawn(ItemSpawnMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            ModRuntime.LegacyInfo("[ItemSpawn] sending " + msg.ItemType + " at " + msg.PosX + "," + msg.PosY + "," + msg.PosZ);
            _net.Broadcast(NetMessageType.ItemSpawn, w => msg.Serialize(w));
        }

        internal void SendThrowableSpawn(ThrowableSpawnMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.ThrowableSpawn, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        internal void SendDroppedItemSpawn(DroppedItemSpawnMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            _net.Broadcast(NetMessageType.DroppedItemSpawn, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        internal void SendDroppedItemPickup(DroppedItemPickupMessage msg)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (!string.IsNullOrEmpty(msg.Guid))
                LanNetworkManager.ConsumedDropGuids.Add(msg.Guid); // local consume before peers process
            _net.Broadcast(NetMessageType.DroppedItemPickup, w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        /// <summary>True if this GUID was already picked up (local or network).</summary>
        internal static bool IsDropGuidConsumed(string guid)
        {
            return !string.IsNullOrEmpty(guid) && LanNetworkManager.ConsumedDropGuids.Contains(guid);
        }

        /// <summary>
        /// Host: send all live GUID-tagged ground drops to a late-joining peer.
        /// </summary>
        internal void SyncExistingDroppedItems(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;

            var list = new System.Collections.Generic.List<Players.DroppedItemIdentifier>(32);
            Players.DroppedItemIdentifier.CopyAll(list);
            int sent = 0;
            foreach (var ident in list)
            {
                if (ident == null || string.IsNullOrEmpty(ident.Id) || ident.gameObject == null)
                    continue;
                if (LanNetworkManager.ConsumedDropGuids.Contains(ident.Id))
                    continue;

                Inventory inv = ident.GetComponent<Inventory>();
                InvItemClass item = null;
                if (inv != null && inv.slots != null && inv.slots.Count > 0)
                    item = inv.slots[0].invItem;
                if (InvItemClass.isNull(item))
                    continue;

                Vector3 pos = ident.transform.position;
                Vector3 euler = ident.transform.eulerAngles;
                string prefab = "Items/DroppedItem";
                string n = ident.gameObject.name ?? "";
                if (n.IndexOf("water", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    prefab = "Items/DroppedItem_water";

                int ammo = 0;
                if (item.baseClass != null && item.baseClass.hasAmmo)
                    ammo = item.ammo;

                var msg = new DroppedItemSpawnMessage
                {
                    Guid = ident.Id,
                    PrefabPath = prefab,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = euler.x,
                    RotY = euler.y,
                    RotZ = euler.z,
                    ItemType = item.type,
                    Amount = item.amount,
                    Durability = item.durability,
                    Ammo = ammo
                };
                _net.SendToPlayer(targetPlayerId, NetMessageType.DroppedItemSpawn,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0)
                ModRuntime.LegacyInfo($"[DroppedItem] Synced {sent} existing drop(s) to player {targetPlayerId}");
        }

        public void SendDoorState(DoorState door)
        {
            if (!_net.IsConnected) return;
            // DialogHostApplyGuard: host replaying client dialogue close must fan out DoorState.
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active) return;
            var msg = WorldPhysicsSyncService.StampSnapshot(
                new PhysicsStateMessage { Doors = new[] { door } });
            _net.Broadcast(NetMessageType.PhysicsState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Host: when a client sends silent trap disarm, mint id if missing then fan-out
        /// so 2p host apply is not the only peer that sees the trap dead.
        /// </summary>
        public void SendTrapState(TrapState ts)
        {
            if (!_net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            var msg = WorldPhysicsSyncService.StampSnapshot(
                new PhysicsStateMessage { Traps = new[] { ts } });
            ModLog.Event(LogCat.World, "[TrapSync] sending trap triggered id=" + ts.TrapNetId
                + " silent=" + (ts.OccupantPlayerId == TrapState.OccupantSilentDisarm)
                + " at " + ts.PosX + "," + ts.PosY + "," + ts.PosZ
                + " role=" + _net.Role);
            _net.Broadcast(NetMessageType.PhysicsState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Host→peer: full known trap table for late join.</summary>
        internal void SendTrapBulkTo(int playerId)
        {
            if (_net.Role != NetworkRole.Host || playerId <= 0 || !_net.IsConnected)
                return;

            var list = new List<TrapBulkEntry>(32);
            foreach (var kv in Sync.TrapNetworkId.EnumerateRegistered())
            {
                GameObject go = kv.Value;
                if (go == null) continue;
                Vector3 p = go.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(p.x * 10f) / 10f,
                    Mathf.Round(p.y * 10f) / 10f,
                    Mathf.Round(p.z * 10f) / 10f);
                bool triggered = false;
                try
                {
                    var t = go.GetComponent<Trigger>();
                    if (t != null)
                        triggered = t.triggered;
                }
                catch { /* ignore */ }

                short occ = WorldPhysicsSyncService.ResolveTrapOccupant(kv.Key, key);
                list.Add(new TrapBulkEntry
                {
                    TrapNetId = kv.Key,
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    Triggered = triggered || ReadTrapTriggeredSafe(go),
                    OccupantPlayerId = occ
                });
            }

            // Also include known traps that may lack registry (mint now)
            foreach (var go in WorldPhysicsSyncService.GetKnownTrapsSnapshot())
            {
                if (go == null) continue;
                int id = Sync.TrapNetworkId.GetOrMintHost(go);
                bool already = false;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].TrapNetId == id) { already = true; break; }
                if (already) continue;
                Vector3 p = go.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(p.x * 10f) / 10f,
                    Mathf.Round(p.y * 10f) / 10f,
                    Mathf.Round(p.z * 10f) / 10f);
                list.Add(new TrapBulkEntry
                {
                    TrapNetId = id,
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    Triggered = ReadTrapTriggeredSafe(go),
                    OccupantPlayerId = WorldPhysicsSyncService.ResolveTrapOccupant(id, key)
                });
            }

            var bulk = new TrapBulkMessage { Entries = list.ToArray() };
            _net.SendToPlayer(playerId, NetMessageType.TrapBulk, w => bulk.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.Session, "[BulkSync] Traps → p" + playerId + ": " + list.Count);
        }

        private static bool ReadTrapTriggeredSafe(GameObject go)
        {
            if (go == null) return false;
            try
            {
                Trigger t = go.GetComponent<Trigger>();
                if (t != null) return t.triggered;
            }
            catch { /* ignore */ }
            return false;
        }

    }
}
