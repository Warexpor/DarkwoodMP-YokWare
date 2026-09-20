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
        /// <summary>Inbound dispatch slice: World.</summary>
        private bool TryDispatchWorld(NetMessageType type, byte[] payload)
        {
            switch (type)
            {
                        case NetMessageType.PhysicsState:
                            WorldPhysicsHandlers.HandlePhysicsState(PhysicsStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ItemSpawn:
                            WorldPhysicsHandlers.HandleItemSpawn(ItemSpawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.LightState:
                            WorldPhysicsHandlers.HandleLightState(LightStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.EntityState:
                            PlayerPresenceHandlers.HandleEntityState(EntityStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.EntityDespawn:
                            WorldObjectSendHandlers.HandleEntityDespawn(EntityDespawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ContainerItem:
                            ContainerHandlers.HandleContainerItem(
                                ContainerItemMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.BarricadeEvent:
                            BarricadeHandlers.HandleBarricadeEvent(
                                BarricadeEventMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorkbenchLevel:
                            JournalHandlers.HandleWorkbenchLevel(
                                WorkbenchLevelMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.JournalItem:
                            JournalHandlers.HandleJournalItem(
                                JournalItemMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.EntitySound:
                            WorldFxHandlers.HandleEntitySound(EntitySoundMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorldObjectRemoved:
                            WorldFxHandlers.HandleWorldObjectRemoved(WorldObjectRemovedMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.SawState:
                            StationHandlers.HandleSawState(
                                SawStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.FeederState:
                            StationHandlers.HandleFeederState(
                                FeederStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.LureState:
                            StationHandlers.HandleLureState(
                                LureStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorkbenchLock:
                            JournalHandlers.HandleWorkbenchLock(
                                WorkbenchLockMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.TradeSync:
                            TradeHandlers.HandleTradeSync(
                                TradeSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.TradeInventorySync:
                            TradeHandlers.HandleTradeInventorySync(
                                TradeInventorySyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ActivateCursorAction:
                            CursorActionHandlers.HandleActivateCursorAction(
                                ActivateCursorActionMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.LocationTransport:
                            CursorActionHandlers.HandleLocationTransport(
                                LocationTransportMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ChainState:
                            ChainHandlers.HandleChainState(
                                ChainStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorldBurnState:
                            WorldBurnHandlers.HandleWorldBurnState(
                                WorldBurnStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ConstructibleConstruction:
                            LockHandlers.HandleConstructible(
                                ConstructibleMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.InteractiveItemSwitch:
                            LockHandlers.HandleInteractiveItemSwitch(
                                InteractiveItemSwitchMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PadlockUnlock:
                            LockHandlers.HandlePadlockUnlock(
                                PadlockUnlockMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.LockedUnlock:
                            LockHandlers.HandleLockedUnlock(
                                LockedUnlockMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.MapMarker:
                            MapHandlers.HandleMapMarker(
                                MapMarkerMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.MapMarkerRemove:
                            MapHandlers.HandleMapMarkerRemove(
                                MapMarkerRemoveMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.MapElementDiscovered:
                            MapHandlers.HandleMapElementDiscovered(
                                MapElementDiscoveredMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.OxygenTankStash:
                            JournalHandlers.HandleOxygenTankStash(
                                OxygenTankStashMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.CompressorTankConvert:
                            JournalHandlers.HandleCompressorTankConvert(
                                CompressorTankConvertMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DoorOpen:
                            DoorHandlers.HandleDoorOpen(
                                DoorOpenMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.LocationEnter:
                            LocationHandlers.HandleLocationEnter(
                                LocationEnterMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.LocationExit:
                            LocationHandlers.HandleLocationExit(
                                LocationExitMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.EntitySpawn:
                            LocationHandlers.HandleEntitySpawn(
                                EntitySpawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.TrapTriggered:
                            LocationHandlers.HandleTrapTriggered(
                                TrapTriggeredMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.TrapBulk:
                            WorldObjectSendHandlers.HandleTrapBulk(TrapBulkMessage.Deserialize(new NetReader(payload)));
                            return true;
                default:
                    return false;
            }
        }
    }
}
