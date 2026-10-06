using System;
using System.IO;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        /// <summary>Inbound handlers: World.</summary>
        private void RegisterWorldHandlers()
        {
            On(NetMessageType.PhysicsState, PhysicsStateMessage.Deserialize, m => WorldPhysicsHandlers.HandlePhysicsState(m));
            On(NetMessageType.ItemSpawn, ItemSpawnMessage.Deserialize, m => WorldPhysicsHandlers.HandleItemSpawn(m));
            On(NetMessageType.LightState, LightStateMessage.Deserialize, m => WorldPhysicsHandlers.HandleLightState(m));
            On(NetMessageType.EntityState, EntityStateMessage.Deserialize, m => PlayerPresenceHandlers.HandleEntityState(m));
            On(NetMessageType.EntityDespawn, EntityDespawnMessage.Deserialize, m => WorldObjectSendHandlers.HandleEntityDespawn(m));
            On(NetMessageType.ContainerItem, ContainerItemMessage.Deserialize, m => ContainerLootHandlers.HandleContainerItem(m));
            On(NetMessageType.BarricadeEvent, BarricadeEventMessage.Deserialize, m => BarricadeHandlers.HandleBarricadeEvent(m));
            On(NetMessageType.WorkbenchLevel, WorkbenchLevelMessage.Deserialize, m => JournalHandlers.HandleWorkbenchLevel(m));
            On(NetMessageType.JournalItem, JournalItemMessage.Deserialize, m => JournalHandlers.HandleJournalItem(m));
            On(NetMessageType.EntitySound, EntitySoundMessage.Deserialize, m => WorldFxHandlers.HandleEntitySound(m));
            On(NetMessageType.BansheeAgitation, BansheeAgitationMessage.Deserialize, m => WorldFxHandlers.HandleBansheeAgitation(m));
            On(NetMessageType.PorterTransport, PorterTransportMessage.Deserialize, m => Patches.PorterTransport.ApplyOnClient(this, m));
            On(NetMessageType.PlayerSpecial, PlayerSpecialMessage.Deserialize, m => Patches.ActorStoryFunctions.ApplyPlayerSpecial(this, m));
            On(NetMessageType.OxygenTankTier, OxygenTankTierMessage.Deserialize, m => OxygenTankParty.HandleTier(this, m));
            On(NetMessageType.QuestHandoff, QuestHandoffMessage.Deserialize, m => QuestItemHandoff.HandleOnClient(this, m));
            On(NetMessageType.DialogHandInGone, DialogHandInGoneMessage.Deserialize, m => DialogHandInArbiter.HandleOnClient(this, m));
            On(NetMessageType.WorldObjectRemoved, WorldObjectRemovedMessage.Deserialize, m => WorldFxHandlers.HandleWorldObjectRemoved(m));
            On(NetMessageType.SawState, SawStateMessage.Deserialize, m => StationHandlers.HandleSawState(m));
            On(NetMessageType.FeederState, FeederStateMessage.Deserialize, m => StationHandlers.HandleFeederState(m));
            On(NetMessageType.LureState, LureStateMessage.Deserialize, m => StationHandlers.HandleLureState(m));
            // Exclusive workbench lock is retired; older peers' packets are ignored.
            OnRaw(NetMessageType.WorkbenchLock, _ => { });
            On(NetMessageType.TradeSync, TradeSyncMessage.Deserialize, m => TradeHandlers.HandleTradeSync(m));
            On(NetMessageType.TradeInventorySync, TradeInventorySyncMessage.Deserialize, m => TradeHandlers.HandleTradeInventorySync(m));
            On(NetMessageType.TradeCommit, TradeCommitMessage.Deserialize, m => Patches.TradeCommit.Handle(this, m));
            On(NetMessageType.ActivateCursorAction, ActivateCursorActionMessage.Deserialize, m => CursorActionHandlers.HandleActivateCursorAction(m));
            On(NetMessageType.LocationTransport, LocationTransportMessage.Deserialize, m => CursorActionHandlers.HandleLocationTransport(m));
            On(NetMessageType.ChainState, ChainStateMessage.Deserialize, m => ChainHandlers.HandleChainState(m));
            On(NetMessageType.WorldBurnState, WorldBurnStateMessage.Deserialize, m => WorldBurnHandlers.HandleWorldBurnState(m));
            On(NetMessageType.ConstructibleConstruction, ConstructibleMessage.Deserialize, m => LockHandlers.HandleConstructible(m));
            On(NetMessageType.InteractiveItemSwitch, InteractiveItemSwitchMessage.Deserialize, m => LockHandlers.HandleInteractiveItemSwitch(m));
            On(NetMessageType.PadlockUnlock, PadlockUnlockMessage.Deserialize, m => LockHandlers.HandlePadlockUnlock(m));
            On(NetMessageType.LockedUnlock, LockedUnlockMessage.Deserialize, m => LockHandlers.HandleLockedUnlock(m));
            OnRaw(NetMessageType.MapMarker, payload =>
            {
                // Host stamps the real sender and relays that body; the generic
                // Forwardable relay would pass a client-claimed PlayerId to the others.
                var marker = MapMarkerMessage.Deserialize(new NetReader(payload));
                bool stamp = _role == NetworkRole.Host && _currentReceivePlayerId > 0;
                if (stamp) marker.PlayerId = _currentReceivePlayerId;
                MapHandlers.HandleMapMarker(marker);
                if (stamp) RelayStamped(w => marker.Serialize(w));
            });
            OnRaw(NetMessageType.MapMarkerRemove, payload =>
            {
                var marker = MapMarkerRemoveMessage.Deserialize(new NetReader(payload));
                bool stamp = _role == NetworkRole.Host && _currentReceivePlayerId > 0;
                if (stamp) marker.PlayerId = _currentReceivePlayerId;
                MapHandlers.HandleMapMarkerRemove(marker);
                if (stamp) RelayStamped(w => marker.Serialize(w));
            });
            On(NetMessageType.MapElementDiscovered, MapElementDiscoveredMessage.Deserialize, m => MapHandlers.HandleMapElementDiscovered(m));
            On(NetMessageType.DoorOpen, DoorOpenMessage.Deserialize, m => DoorHandlers.HandleDoorOpen(m));
            OnRaw(NetMessageType.LocationEnter, payload =>
            {
                var enter = LocationEnterMessage.Deserialize(new NetReader(payload));
                bool stamp = _role == NetworkRole.Host && _currentReceivePlayerId > 0;
                if (stamp) enter.PlayerId = _currentReceivePlayerId;
                LocationEnterExitHandlers.HandleLocationEnter(enter);
                if (stamp) RelayStamped(w => enter.Serialize(w));
            });
            OnRaw(NetMessageType.LocationExit, payload =>
            {
                var exit = LocationExitMessage.Deserialize(new NetReader(payload));
                bool stamp = _role == NetworkRole.Host && _currentReceivePlayerId > 0;
                if (stamp) exit.PlayerId = _currentReceivePlayerId;
                LocationEnterExitHandlers.HandleLocationExit(exit);
                if (stamp) RelayStamped(w => exit.Serialize(w));
            });
            On(NetMessageType.EntitySpawn, EntitySpawnMessage.Deserialize, m => LocationEntityTrapHandlers.HandleEntitySpawn(m));
            On(NetMessageType.TrapTriggered, TrapTriggeredMessage.Deserialize, m => LocationEntityTrapHandlers.HandleTrapTriggered(m));
            On(NetMessageType.LocationPadSlotRequest, LocationPadSlotRequestMessage.Deserialize, m => OutsidePadSlots.HandleRequest(this, _currentReceivePlayerId, m));
            On(NetMessageType.LocationPadSlotSync, LocationPadSlotSyncMessage.Deserialize, m => OutsidePadSlots.HandleSync(this, m));
            On(NetMessageType.TrapBulk, TrapBulkMessage.Deserialize, m => WorldObjectSendHandlers.HandleTrapBulk(m));
        }
    }
}
