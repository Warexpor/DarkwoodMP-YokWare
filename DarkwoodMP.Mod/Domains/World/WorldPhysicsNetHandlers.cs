using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Physics snapshot / item spawn / light-state apply composed for 0.8.</summary>
    internal sealed class WorldPhysicsNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldPhysicsNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void SendWorldSnapshot()
        {
            if (!_net.IsConnected)
                return;

            if (Sync.WorldPhysicsSyncService.TryBuildWorldSnapshot(out var msg))
                _net.SendPhysicsStateStream(msg);
        }

        internal void HandlePhysicsState(PhysicsStateMessage state)
        {
            string fromPeer = (_net.Role == NetworkRole.Host) ? "client" : "host";
            if (!_net.AcceptSnapshotSequence(
                state.Reliable
                    ? _net.LastReliablePhysicsStateSequence
                    : _net.LastPhysicsStateSequence,
                _net.CurrentReceivePlayerId,
                state.Sequence,
                state.Reliable ? "ReliablePhysicsState" : "PhysicsState"))
                return;

            // Client event snapshots (SendDoorState / SendTrapState / SendGeneratorState)
            // only carry those arrays and must be applied + fan-out so co-op peers stay
            // in sync. Bulk free-body snapshots from a client may still include stale
            // door, trap, and generator copies; strip them so they cannot fight host ownership.
            // The arrays are recycled receive buffers: only the Effective*Count prefix is live.
            bool isClientOrigin = _net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0;
            int oc = state.EffectiveObjectCount;
            int dc = state.EffectiveDoorCount;
            int tc = state.EffectiveTrapCount;
            int gc = state.EffectiveGeneratorCount;
            bool isEventStyle = isClientOrigin && oc == 0 && (dc > 0 || tc > 0 || gc > 0);

            if (isClientOrigin && !isEventStyle && (dc > 0 || tc > 0 || gc > 0))
            {
                // Drop the stale copies completely (arrays AND counts: Serialize/Apply read the counts).
                state.Doors = null;
                state.DoorCount = 0;
                state.Traps = null;
                state.TrapCount = 0;
                state.Generators = null;
                state.GeneratorCount = 0;
                dc = 0;
                tc = 0;
                gc = 0;
            }

            if ((oc > 0 || dc > 0 || tc > 0 || gc > 0) && ++_net.PhysicsRecvLogCounter % 30 == 0 && ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Physics] objects={oc} doors={dc} traps={tc} gens={gc} from {fromPeer}");

            if (_net.Role == NetworkRole.Client)
            {
                var psw = System.Diagnostics.Stopwatch.StartNew();
                Sync.WorldPhysicsSyncService.ApplySnapshot(state, fromPeer);
                psw.Stop();
                ClientPerfProbe.NotePhysApply(oc, psw.Elapsed.TotalMilliseconds);
            }
            else
            {
                Sync.WorldPhysicsSyncService.ApplySnapshot(state, fromPeer);
            }

            // Forward client-originated free-body physics and event-style door/trap/gen
            // snapshots to other clients (3+ support). Host already applied above.
            // For silent trap disarm from client: also ensure host re-broadcasts with minted id
            // when the inbound packet had TrapNetId=0 (so late flush / logs stay consistent).
            if (isClientOrigin && isEventStyle && tc > 0 && state.Traps != null)
            {
                for (int i = 0; i < tc; i++)
                {
                    TrapState entry = state.Traps[i];
                    if (entry.TrapNetId > 0) continue;
                    if (entry.OccupantPlayerId != TrapState.OccupantSilentDisarm)
                        continue;
                    Vector3 tp = new Vector3(entry.PosX, entry.PosY, entry.PosZ);
                    GameObject go = Sync.WorldPhysicsSyncService.FindTrapByPos(tp);
                    if (go == null) continue;
                    entry.TrapNetId = Sync.TrapNetworkId.GetOrMintHost(go);
                    state.Traps[i] = entry;
                }
            }

            if (!isClientOrigin || (oc == 0 && !isEventStyle))
                return;

            // Receivers key the sequence by the immediate sender (this host), so a forward must carry
            // the HOST's own counter. Forwarding the client's Sequence made the third player compare
            // it against the host's stream and reject it (or advance the host's high-water mark).
            if (isEventStyle)
            {
                // Door / trap / generator events are one-shot: always the reliable stream, host-stamped
                // (Sequence 0 makes StampSnapshot take the next number from the host's counter).
                state.Sequence = 0;
                state = Sync.WorldPhysicsSyncService.StampSnapshot(state);
                if (gc > 0 && oc == 0 && dc == 0 && tc == 0)
                {
                    // Pure generator event: include pourer so host-auth abs (clamp / concurrent
                    // sum) converges on the originator.
                    _net.Broadcast(NetMessageType.PhysicsState, w => state.Serialize(w),
                        LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
                else
                {
                    _net.SendToAllExcept(_net.CurrentReceivePlayerId, NetMessageType.PhysicsState,
                        w => state.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
                return;
            }

            // Client free bodies: only the ones that exist in the host world go on (the host is the
            // authority on item existence), on the unreliable stream, re-stamped and split to the MTU.
            int kept = KeepHostResolvedObjects(ref state);
            if (kept == 0)
                return;
            _net.SendPhysicsStateStream(state, excludePlayerId: _net.CurrentReceivePlayerId);
        }

        /// <summary>
        /// Compact the object prefix to the entries ApplySnapshot resolved on the host (it clears the
        /// name of anything it could not find). Returns the kept count.
        /// </summary>
        private static int KeepHostResolvedObjects(ref PhysicsStateMessage state)
        {
            int oc = state.EffectiveObjectCount;
            int kept = 0;
            for (int i = 0; i < oc; i++)
            {
                if (string.IsNullOrEmpty(state.Objects[i].Name))
                    continue;
                if (kept != i)
                    state.Objects[kept] = state.Objects[i];
                kept++;
            }
            state.ObjectCount = kept;
            return kept;
        }

        internal void HandleItemSpawn(ItemSpawnMessage msg)
        {
            string fromPeer = (_net.Role == NetworkRole.Host) ? "client" : "host";
            ModRuntime.LegacyInfo($"[ItemSpawn] received {msg.ItemType} at {msg.PosX},{msg.PosY},{msg.PosZ} from {fromPeer}");

            // Bike-bell (porterWhistle): world spawn of Events/porterSpawner, not the InvItem
            // trap prefab. ItemsDatabase.hasItem("porterWhistle") is true — must branch first.
            if (string.Equals(
                    msg.ItemType,
                    Patches.PorterSpawnerAuth.PorterWhistleItemSpawnType,
                    System.StringComparison.Ordinal))
            {
                if (_net.Role != NetworkRole.Host)
                    return;
                Vector3 porterPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                Patches.PorterSpawnerAuth.TryApplyPorterWhistleOnHost(porterPos);
                // Do not fan ItemSpawn — Porter NPC reaches peers via entity snapshots.
                return;
            }

            if (Singleton<ItemsDatabase>.Instance == null)
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] ItemsDatabase not available");
                return;
            }

            if (!Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] unknown item type: " + msg.ItemType);
                return;
            }

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(msg.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] no prefab for " + msg.ItemType);
                return;
            }

            // The host knows who sent it; a client's own claim is not trusted.
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
                msg.PlacerId = (short)_net.CurrentReceivePlayerId;

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);
            GameObject go = Core.AddPrefab(itemDef.item, pos, rot, null);
            if (go != null)
            {
                // Vanilla Player.progressBarCompleted placement, as on the placing peer.
                Trigger trig = go.GetComponent<Trigger>();
                if (trig != null)
                {
                    trig.setByPlayer = true;
                    if (!string.IsNullOrEmpty(trig.useSound))
                        AudioController.Play(trig.useSound, go.transform);
                    if (msg.PlacerId > 0)
                        (go.GetComponent<TrapPlacer>() ?? go.AddComponent<TrapPlacer>()).PlayerId = msg.PlacerId;
                }
                Core.addToSaveable(go, isDynamic: true, assignID: true);
                Singleton<WorldGrid>.Instance?.registerToNode(go);
                if (_net.Role == NetworkRole.Host && TrapNetworkId.IsWorldTrap(go))
                {
                    TrapNetworkId.GetOrMintHost(go);
                    TrapLedger.NotePlaced(msg, go);
                }
            }
            else
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] Core.AddPrefab returned null for " + msg.ItemType);
            }

            // Forward client-placed items to other clients (3+ support)
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
                _net.SendToAllExcept(_net.CurrentReceivePlayerId, NetMessageType.ItemSpawn, w => msg.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        internal void HandleLightState(LightStateMessage ls)
        {
            string fromPeer = (_net.Role == NetworkRole.Host) ? "client" : "host";
            Sync.WorldPhysicsSyncService.ApplyLightState(ls, fromPeer);
        }
    }
}
