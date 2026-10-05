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
        /// <param name="scope">When set, only lights inside this pad scope are sent.</param>
        internal void SyncExistingWorldLightsTo(int targetPlayerId, PadScope? scope = null)
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
                if (scope.HasValue && !scope.Value.Contains(item.transform))
                    continue;
                if (item.GetComponent<Generator>() != null)
                    continue;
                // The host's own prologue pads are not the world.
                if (PersonalPrologue.IsOnProloguePad(item.transform))
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
            if (sent >= maxSend)
                ModLog.Warn(LogCat.Session, $"[BulkSync] World lights → p{targetPlayerId}: cap {maxSend} reached, later lamps not sent");
        }

        /// <summary>
        /// Host → peer: generator isOn/fuel so restorePower/cutPower matches before lamp bulk applies.
        /// Vanilla generators cycle power only; they must not overwrite per-lamp isOn via LightState.
        /// </summary>
        /// <param name="scope">When set, only generators inside this pad scope are sent.</param>
        internal void SyncExistingGeneratorsTo(int targetPlayerId, PadScope? scope = null)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;

            IList<Generator> gens = Sync.ListTracker<Generator>.GetAll();
            const int maxSend = 32;
            var batch = new List<Sync.GeneratorState>(Mathf.Min(gens.Count, maxSend));
            for (int i = 0; i < gens.Count && batch.Count < maxSend; i++)
            {
                Generator gen = gens[i];
                if (gen == null || gen.gameObject == null || !gen.gameObject.scene.IsValid())
                    continue;
                if (scope.HasValue && !scope.Value.Contains(gen.transform))
                    continue;
                if (PersonalPrologue.IsOnProloguePad(gen.transform))
                    continue;

                Vector3 p = gen.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(p.x * 10f) / 10f,
                    Mathf.Round(p.y * 10f) / 10f,
                    Mathf.Round(p.z * 10f) / 10f);
                Item itemComp = gen.GetComponent<Item>();
                string itemType = itemComp != null && itemComp.invItem != null ? itemComp.invItem.type : "";

                batch.Add(new Sync.GeneratorState
                {
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    IsOn = gen.isOn,
                    Fuel = gen.fuel,
                    LowPower = gen.lowPower,
                    ItemType = itemType
                });
            }

            // One snapshot carries every generator instead of one reliable packet each.
            if (batch.Count > 0)
            {
                var msg = Sync.WorldPhysicsSyncService.StampSnapshot(
                    new Sync.PhysicsStateMessage { Generators = batch.ToArray() });
                _net.SendToPlayer(targetPlayerId, NetMessageType.PhysicsState,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }

            if (batch.Count > 0 || Config.ModConfig.IsVerboseLightSync)
                ModLog.Event(LogCat.Session, $"[BulkSync] Generators → p{targetPlayerId}: {batch.Count}");
        }

        /// <summary>Pad resync filter: under the location root, or near the pad anchor (XZ).</summary>
        internal struct PadScope
        {
            public Transform Root;
            public Vector3 Anchor;

            public bool Contains(Transform t) =>
                IsUnderOrNearLocation(t, Root, Anchor, PadResyncMaxDistSqr);
        }

        /// <summary>
        /// Host: re-push isOn world lights (and gens) after a peer enters a location so
        /// LightState that missed while the grid was unloaded is recovered.
        /// </summary>
        /// <summary>
        /// First-enter pad resync scope: objects under the location root, or within this XZ
        /// radius of the pad (spawned objects not parented to it). The old 2500 u radius covered
        /// the whole map and sent hundreds of reliable packets on every first enter.
        /// </summary>
        internal const float PadResyncMaxDistSqr = 250f * 250f;
        /// <summary>Opened doors per batched PhysicsState in the pad resync.</summary>
        private const int PadDoorBatch = 32;

        internal void ResyncWorldLightsForPeer(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;
            // Only the pad the peer just entered: the rest of the map was covered by the
            // join-time bulk and live LightState.
            if (!TryResolvePeerPadScope(targetPlayerId, out PadScope scope))
            {
                ModLog.Event(LogCat.Session, $"[BulkSync] pad light resync p{targetPlayerId}: no pad / position");
                return;
            }
            SyncExistingGeneratorsTo(targetPlayerId, scope);
            SyncExistingWorldLightsTo(targetPlayerId, scope);
        }

        private bool TryResolvePeerPadScope(int playerId, out PadScope scope)
        {
            scope = default;
            if (_net.RemoteOutsideLocation.TryGetValue(playerId, out string locName)
                && !string.IsNullOrEmpty(locName))
            {
                var ol = Singleton<OutsideLocations>.Instance;
                Location loc = ol != null ? LocationEnterExitNetHandlers.ResolveOutsideLocation(ol, locName) : null;
                if (loc != null && loc.gameObject != null)
                {
                    Transform root = loc.transform;
                    scope.Root = root;
                    scope.Anchor = loc.playerSpawn != null ? loc.playerSpawn.transform.position : root.position;
                    return true;
                }
            }
            // No resolvable pad: fall back to the area around the peer's last known position.
            if (PlayerPositionManager.TryGetRemote(playerId, out Vector3 pos, out _))
            {
                scope.Anchor = pos;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Host: peer's first local <c>spawnLocation</c> instantiates a virgin prefab.
        /// Late-join bulk often ran while that pad was absent (door/item find failed or
        /// pending queue capped / GE pending aged out). Re-send the same idempotent
        /// barricade / opened-door / unlocked-padlock / unlocked-Locked / fired-GE /
        /// InteractiveItem isOn / constructed / trap / burn / chain / shadow-armor /
        /// station / NPC visual snapshots lights already get via
        /// <see cref="ResyncWorldLightsForPeer"/>.
        /// Container loot stays on open <c>ContainerStateRequest</c> (no bulk here).
        /// DoorState opened replay does not clear <see cref="Padlock.locked"/> or
        /// <see cref="Locked.locked"/> (DoorOpen apply does; first-enter uses DoorState).
        /// </summary>
        internal void ResyncOutsideLocationPadForPeer(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return;

            int barrDoors = _net.BarricadeHandlers.SendBarricadeDoorsTo(targetPlayerId);
            int barrWindows = _net.BarricadeHandlers.SendBarricadeWindowsTo(targetPlayerId);
            int barrItems = _net.BarricadeHandlers.SendBarricadeItemsTo(targetPlayerId);
            int opened = SendOpenedDoorStatesNearLocationTo(targetPlayerId, loc);
            int padlocks = SendUnlockedPadlocksNearLocationTo(targetPlayerId, loc);
            int lockeds = SendUnlockedLockedsNearLocationTo(targetPlayerId, loc);
            int firedGe = _net.GameEventHandlers.SendFiredGameEventsNearLocationTo(
                targetPlayerId, loc);
            int interactives = _net.LockHandlers.SendInteractivesNearLocationTo(
                targetPlayerId, loc);
            int constructed = _net.LockHandlers.SendConstructedSitesNearLocationTo(
                targetPlayerId, loc);
            int traps = _net.WorldObjectSendHandlers.SendTrapsNearLocationTo(
                targetPlayerId, loc);
            int burns = _net.WorldBurnHandlers.SendWorldBurnStatesNearLocationTo(
                targetPlayerId, loc);
            int chains = _net.ChainHandlers.SendChainStatesNearLocationTo(
                targetPlayerId, loc);
            int shadowArmor = _net.ShadowArmorHandlers.SendShadowArmorStatesNearLocationTo(
                targetPlayerId, loc);
            int stations = _net.StationHandlers.SendStationsNearLocationTo(
                targetPlayerId, loc);
            _net.BulkSyncHandlers.SendReputationBulkSyncTo(targetPlayerId);
            int taken = SendTakenPickupsNearLocationTo(targetPlayerId, loc);

            ModLog.Event(LogCat.Session,
                "[LocationSync] pad resync → p" + targetPlayerId
                + " taken=" + taken
                + " barrDoor=" + barrDoors
                + " win=" + barrWindows
                + " item=" + barrItems
                + " opened=" + opened
                + " padlock=" + padlocks
                + " locked=" + lockeds
                + " firedGe=" + firedGe
                + " interactive=" + interactives
                + " construct=" + constructed
                + " trap=" + traps
                + " burn=" + burns
                + " chain=" + chains
                + " shadowArmor=" + shadowArmor
                + " station=" + stations
                + " loc=" + (loc.gameObject != null ? loc.gameObject.name : loc.name));
        }

        /// <summary>
        /// Host→peer: ground items already taken in this location. A location spawns from its
        /// prefab on each machine; a player arriving later saw (and could pick at) items others had
        /// long taken there.
        /// </summary>
        private int SendTakenPickupsNearLocationTo(int targetPlayerId, Location loc)
        {
            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            int sent = 0;
            var log = WorldPhysicsSyncService.ConsumedWorldPickupLog;
            for (int i = 0; i < log.Count; i++)
            {
                Vector3 p = log[i].Key;
                if ((p - anchor).sqrMagnitude > WorldLateJoinNetHandlers.PadResyncMaxDistSqr)
                    continue;
                var rm = new WorldObjectRemovedMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    ObjectName = log[i].Value,
                    Mode = WorldObjectRemovedMessage.ModeRemove
                };
                _net.SendToPlayer(targetPlayerId, NetMessageType.WorldObjectRemoved, w => rm.Serialize(w), DeliveryMethod.ReliableOrdered);
                sent++;
            }
            return sent;
        }

        /// <summary>
        /// Host→peer: DoorState for opened doors under/near the pad. Continuous
        /// PhysicsState only re-sends on change, so sticky opens from before the
        /// peer spawned the pad never arrive otherwise.
        /// </summary>
        private int SendOpenedDoorStatesNearLocationTo(int targetPlayerId, Location loc)
        {
            if (loc == null)
                return 0;
            return SendDoorStatesTo(targetPlayerId, loc, 128);
        }

        /// <summary>
        /// Host: every door's open or closed state (around <paramref name="loc"/>, or the whole world
        /// when null). Closed ones too: a peer coming back with its own copy of the world kept doors
        /// open that others had closed meanwhile (the live scan only reports changes).
        /// </summary>
        internal int SendDoorStatesTo(int targetPlayerId, Location loc, int maxSend)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0)
                return 0;

            Transform root = loc != null ? loc.transform : null;
            Vector3 anchor = loc == null ? Vector3.zero
                : loc.playerSpawn != null
                    ? loc.playerSpawn.transform.position
                    : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = WorldLateJoinNetHandlers.PadResyncMaxDistSqr;

            Door[] doors = WorldQueryHelper.GetCachedSceneComponents<Door>();
            int sent = 0;
            var batch = new List<DoorState>(PadDoorBatch);
            for (int i = 0; i < doors.Length && sent < maxSend; i++)
            {
                Door door = doors[i];
                if (door == null || door.transform == null) continue;
                if (loc != null && !IsUnderOrNearLocation(door.transform, root, anchor, maxDistSqr))
                    continue;
                if (PersonalPrologue.IsOnProloguePad(door.transform))
                    continue;
                bool opened = TraverseHack.ReadDoorOpened(door);

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

                batch.Add(new DoorState
                {
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    Opened = opened,
                    BodyRotY = bodyRotY,
                    AngVelX = angVel.x,
                    AngVelY = angVel.y,
                    AngVelZ = angVel.z
                });
                sent++;
                if (batch.Count >= PadDoorBatch)
                    SendDoorBatch(targetPlayerId, batch);
            }
            SendDoorBatch(targetPlayerId, batch);

            return sent;
        }

        /// <summary>One reliable PhysicsState for a batch of door states; clears the batch.</summary>
        private void SendDoorBatch(int targetPlayerId, List<DoorState> batch)
        {
            if (batch.Count == 0) return;
            var msg = WorldPhysicsSyncService.StampSnapshot(
                new PhysicsStateMessage { Doors = batch.ToArray() });
            _net.SendToPlayer(targetPlayerId, NetMessageType.PhysicsState,
                w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            batch.Clear();
        }

        /// <summary>
        /// Host→peer: PadlockUnlock for unlocked padlocks under/near the pad.
        /// Late-join PadlockUnlock pending is FIFO-capped at 64 and is not age-refreshed
        /// on first spawn — virgin prefab padlocks stay locked without this resync.
        /// </summary>
        private int SendUnlockedPadlocksNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = WorldLateJoinNetHandlers.PadResyncMaxDistSqr;

            Padlock[] pads = WorldQueryHelper.GetCachedSceneComponents<Padlock>();
            int sent = 0;
            const int maxSend = 64;
            for (int i = 0; i < pads.Length && sent < maxSend; i++)
            {
                Padlock p = pads[i];
                if (p == null || p.locked || p.transform == null) continue;
                if (!p.gameObject.scene.IsValid()) continue;
                if (!IsUnderOrNearLocation(p.transform, root, anchor, maxDistSqr))
                    continue;

                Vector3 pos = p.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                _net.SendToPlayer(targetPlayerId, NetMessageType.PadlockUnlock,
                    w => new PadlockUnlockMessage
                    {
                        PosX = key.x,
                        PosY = key.y,
                        PosZ = key.z
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                sent++;
            }

            return sent;
        }

        /// <summary>
        /// Host→peer: LockedUnlock for unlocked key locks under/near the pad.
        /// Same FIFO-capped pending as PadlockUnlock (shared MaxPendingLocks=64).
        /// DoorState first-enter open does not clear <see cref="Locked.locked"/>;
        /// covers unlocked-but-still-closed doors and non-door Locked (chests).
        /// Apply is idempotent (<c>wasLocked</c> gates host onActivate synth).
        /// </summary>
        private int SendUnlockedLockedsNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = WorldLateJoinNetHandlers.PadResyncMaxDistSqr;

            Locked[] locks = WorldQueryHelper.GetCachedSceneComponents<Locked>();
            int sent = 0;
            const int maxSend = 64;
            for (int i = 0; i < locks.Length && sent < maxSend; i++)
            {
                Locked l = locks[i];
                if (l == null || l.locked || l.transform == null) continue;
                if (!l.gameObject.scene.IsValid()) continue;
                if (!IsUnderOrNearLocation(l.transform, root, anchor, maxDistSqr))
                    continue;

                Vector3 pos = l.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                _net.SendToPlayer(targetPlayerId, NetMessageType.LockedUnlock,
                    w => new LockedUnlockMessage
                    {
                        PosX = key.x,
                        PosY = key.y,
                        PosZ = key.z
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
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
