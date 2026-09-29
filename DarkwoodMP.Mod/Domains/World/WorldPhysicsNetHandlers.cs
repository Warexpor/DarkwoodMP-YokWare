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
                _net.Broadcast(NetMessageType.PhysicsState, w => msg.Serialize(w));
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
            bool isClientOrigin = _net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0;
            int ocPre = state.Objects?.Length ?? 0;
            bool isEventStyle = isClientOrigin && ocPre == 0
                && ((state.Doors != null && state.Doors.Length > 0)
                    || (state.Traps != null && state.Traps.Length > 0)
                    || (state.Generators != null && state.Generators.Length > 0));

            if (isClientOrigin && !isEventStyle)
            {
                if ((state.Doors != null && state.Doors.Length > 0)
                    || (state.Traps != null && state.Traps.Length > 0)
                    || (state.Generators != null && state.Generators.Length > 0))
                {
                    state.Doors = System.Array.Empty<DoorState>();
                    state.Traps = System.Array.Empty<TrapState>();
                    state.Generators = System.Array.Empty<GeneratorState>();
                }
            }

            int oc = state.Objects?.Length ?? 0;
            int dc = state.Doors?.Length ?? 0;
            int tc = state.Traps?.Length ?? 0;
            int gc = state.Generators?.Length ?? 0;
            if ((oc > 0 || dc > 0 || tc > 0 || gc > 0) && ++_net.PhysicsRecvLogCounter % 30 == 0 && ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[Physics] objects=" + oc + " doors=" + dc + " traps=" + tc + " gens=" + gc + " from " + fromPeer);

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
            if (isClientOrigin && isEventStyle && tc > 0 && _net.Role == NetworkRole.Host
                && state.Traps != null)
            {
                for (int i = 0; i < state.Traps.Length; i++)
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

            if (isClientOrigin && isEventStyle && gc > 0 && oc == 0 && dc == 0 && tc == 0)
            {
                // Pure generator event: include pourer so host-auth abs (clamp / concurrent
                // sum) converges on the originator. Reliable like SendGeneratorState.
                _net.Broadcast(NetMessageType.PhysicsState, w => state.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
            }
            else if (isClientOrigin && (oc > 0 || isEventStyle))
            {
                _net.SendToAllExcept(_net.CurrentReceivePlayerId, NetMessageType.PhysicsState,
                    w => state.Serialize(w));
            }
        }

        internal void HandleItemSpawn(ItemSpawnMessage msg)
        {
            string fromPeer = (_net.Role == NetworkRole.Host) ? "client" : "host";
            ModRuntime.LegacyInfo("[ItemSpawn] received " + msg.ItemType + " at " + msg.PosX + "," + msg.PosY + "," + msg.PosZ + " from " + fromPeer);

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

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);
            GameObject go = Core.AddPrefab(itemDef.item, pos, rot, null);
            if (go != null)
            {
                Trigger trig = go.GetComponent<Trigger>();
                if (trig != null)
                    trig.setByPlayer = true;
            }
            else
            {
                ModRuntime.Log?.LogWarning("[ItemSpawn] Core.AddPrefab returned null for " + msg.ItemType);
            }

            // Forward client-placed items to other clients (3+ support)
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
                _net.SendToAllExcept(_net.CurrentReceivePlayerId, NetMessageType.ItemSpawn, w => msg.Serialize(w));
        }

        internal void HandleLightState(LightStateMessage ls)
        {
            string fromPeer = (_net.Role == NetworkRole.Host) ? "client" : "host";
            Sync.WorldPhysicsSyncService.ApplyLightState(ls, fromPeer);
        }
    }
}
