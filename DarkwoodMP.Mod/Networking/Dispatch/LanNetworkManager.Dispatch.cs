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
        /// <summary>Shared LAN/Steam inbound dispatch (type + body after framing byte).</summary>
        private void ProcessInboundMessage(NetMessageType type, byte[] payload)
        {
            using (new NetworkApplyGuard())
            {
                try
                {
                    switch (type)
                    {
                        case NetMessageType.Handshake:
                            HandleHandshake(HandshakeMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerState:
                            HandlePlayerState(PlayerStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldSession:
                            HandleWorldSession(WorldSessionMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PhysicsState:
                            HandlePhysicsState(PhysicsStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ItemSpawn:
                            HandleItemSpawn(ItemSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LightState:
                            HandleLightState(LightStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntityState:
                            HandleEntityState(EntityStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntityDespawn:
                            HandleEntityDespawn(EntityDespawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerAttack:
                            HandlePlayerAttack(PlayerAttackMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DamagePlayer:
                            HandleDamagePlayer(DamagePlayerMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerDied:
                            HandlePlayerDied(PlayerDiedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DeathBagSpawn:
                            HandleDeathBagSpawn(DeathBagSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DeathBagLooted:
                            HandleDeathBagLooted(DeathBagLootedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.NightDeathState:
                            HandleNightDeathState(NightDeathStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ContainerItem:
                            HandleContainerItem(ContainerItemMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.BarricadeEvent:
                            HandleBarricadeEvent(BarricadeEventMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorkbenchLevel:
                            HandleWorkbenchLevel(WorkbenchLevelMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.JournalItem:
                            HandleJournalItem(JournalItemMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FriendlyFire:
                            HandleFriendlyFire(FriendlyFireMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerSound:
                            HandlePlayerSound(PlayerSoundMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerScare:
                            HandlePlayerScare(PlayerScareMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerEffectSync:
                            HandlePlayerEffectSync(PlayerEffectSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DragSync:
                            HandleDragSync(DragSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.SaveSync:
                            HandleSaveSync();
                            break;
                        case NetMessageType.TimeSync:
                            HandleTimeSync(TimeSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntitySound:
                            HandleEntitySound(EntitySoundMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldObjectRemoved:
                            HandleWorldObjectRemoved(WorldObjectRemovedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerLightState:
                            HandlePlayerLightState(PlayerLightStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ThrowableSpawn:
                            HandleThrowableSpawn(ThrowableSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ExplosionTrigger:
                            HandleExplosionTrigger(ExplosionTriggerMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerAudio:
                            HandlePlayerAudio(PlayerAudioMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.GasTrailSpawn:
                            HandleGasTrailSpawn(GasTrailSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.GasIgnite:
                            HandleGasIgnite(GasIgniteMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerAnimation:
                            HandlePlayerAnimation(PlayerAnimationMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerAnimLibrary:
                            HandlePlayerAnimLibrary(PlayerAnimLibraryMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.BulletImpact:
                            HandleBulletImpact(BulletImpactMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerFiredWeapon:
                            HandlePlayerFiredWeapon(PlayerFiredWeaponMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DroppedItemSpawn:
                            HandleDroppedItemSpawn(DroppedItemSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DroppedItemPickup:
                            HandleDroppedItemPickup(DroppedItemPickupMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.SawState:
                            HandleSawState(SawStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FeederState:
                            HandleFeederState(FeederStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LureState:
                            HandleLureState(LureStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.SleepEndRequest:
                            HandleSleepEndRequest(SleepEndRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.AfterNightEndRequest:
                            HandleAfterNightEndRequest(AfterNightEndRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PeerRoster:
                            HandlePeerRoster(PeerRosterMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.HostHandoff:
                            HandleHostHandoff(HostHandoffMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorkbenchLock:
                            HandleWorkbenchLock(WorkbenchLockMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ShadowEvent:
                            HandleShadowEvent(ShadowEventMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ShadowSpawn:
                            HandleShadowSpawn(ShadowSpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.NightShadowSpawnRequest:
                            HandleNightShadowSpawnRequest(
                                NightShadowSpawnRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ScenarioSync:
                            HandleScenarioSync(ScenarioSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ScenarioEventFired:
                            HandleScenarioEventFired(ScenarioEventFiredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntityBurning:
                            HandleEntityBurning(EntityBurningMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LiquidStopBurning:
                            HandleLiquidStopBurning(LiquidStopBurningMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ExplosionSpawnObject:
                            HandleExplosionSpawnObject(ExplosionSpawnObjectMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerBurning:
                            HandlePlayerBurning(PlayerBurningMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FlagSync:
                            HandleFlagSync(FlagSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.TradeSync:
                            HandleTradeSync(TradeSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.TradeInventorySync:
                            HandleTradeInventorySync(TradeInventorySyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DialogOutcomeSync:
                            HandleDialogOutcomeSync(DialogOutcomeSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MeleeWorldHit:
                            HandleMeleeWorldHit(MeleeWorldHitMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamStarted:
                            HandleDreamStarted(DreamStartedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamEnded:
                            HandleDreamEnded(DreamEndedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamStartRequest:
                            HandleDreamStartRequest(DreamStartRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamItemPickup:
                            HandleDreamItemPickup(DreamItemPickupMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamAudio:
                            HandleDreamAudio(DreamAudioMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamEntered:
                            HandleDreamEntered(DreamEnteredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamPropCollider:
                            HandleDreamPropCollider(DreamPropColliderMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamSessionBulk:
                            HandleDreamSessionBulk(DreamSessionBulkMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DreamChainStart:
                            HandleDreamChainStart(DreamChainStartMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FinalDreamsceneDeath:
                            HandleFinalDreamsceneDeath(FinalDreamsceneDeathMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.SceneLoad:
                            HandleSceneLoad(SceneLoadMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.CutsceneSync:
                            HandleCutsceneSync(CutsceneSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ChapterTransition:
                            HandleChapterTransition(ChapterTransitionMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ExamineObject:
                            HandleExamineObject(ExamineObjectMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ActivateCursorAction:
                            HandleActivateCursorAction(
                                ActivateCursorActionMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LocationTransport:
                            HandleLocationTransport(
                                LocationTransportMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PeerHasItem:
                            HandlePeerHasItem(PeerHasItemMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ChainState:
                            HandleChainState(ChainStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ShadowArmorState:
                            HandleShadowArmorState(
                                ShadowArmorStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldBurnState:
                            HandleWorldBurnState(
                                WorldBurnStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ConstructibleConstruction:
                            HandleConstructible(ConstructibleMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ClientStateBackup:
                            HandleClientStateBackup(ClientStateBackupMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.InteractiveItemSwitch:
                            HandleInteractiveItemSwitch(InteractiveItemSwitchMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PadlockUnlock:
                            HandlePadlockUnlock(PadlockUnlockMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LockedUnlock:
                            HandleLockedUnlock(LockedUnlockMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.GameEventsFired:
                            HandleGameEventsFired(GameEventsFiredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.GameEventsBulk:
                            HandleGameEventsBulk(GameEventsBulkMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.HideoutUpgrade:
                            HandleHideoutUpgrade(HideoutUpgradeMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MapMarker:
                            HandleMapMarker(MapMarkerMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MapMarkerRemove:
                            HandleMapMarkerRemove(MapMarkerRemoveMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MapElementDiscovered:
                            HandleMapElementDiscovered(MapElementDiscoveredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.OxygenTankStash:
                            HandleOxygenTankStash(OxygenTankStashMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.CompressorTankConvert:
                            HandleCompressorTankConvert(CompressorTankConvertMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.JournalBulkSync:
                            HandleJournalBulkSync(JournalBulkSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ShadowStateUpdate:
                            HandleShadowStateUpdate(ShadowStateUpdateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ContainerStateRequest:
                            HandleContainerStateRequest(ContainerStateRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ContainerStateSync:
                            HandleContainerStateSync(ContainerStateSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ContainerTakeDenied:
                            HandleContainerTakeDenied(ContainerTakeDeniedMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ReputationSync:
                            HandleReputationSync(ReputationSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DoorOpen:
                            HandleDoorOpen(DoorOpenMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LocationEnter:
                            HandleLocationEnter(LocationEnterMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.LocationExit:
                            HandleLocationExit(LocationExitMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.EntitySpawn:
                            HandleEntitySpawn(EntitySpawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.TrapTriggered:
                            HandleTrapTriggered(TrapTriggeredMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.TrapBulk:
                            HandleTrapBulk(TrapBulkMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ThrowableDespawn:
                            HandleThrowableDespawn(ThrowableDespawnMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.RemotePlayerForward:
                            {
                                var fwd = RemotePlayerForwardMessage.Deserialize(new NetReader(payload));
                                // Host trust: only the original player may ask the host to re-broadcast
                                // their own message. A claimed OriginalPlayerId that differs from the
                                // The actual sender does not match the claimed identity.
                                // Drop the forward.
                                if (_role == NetworkRole.Host && fwd.OriginalPlayerId != _currentReceivePlayerId)
                                {
                                    ModLog.Warn(LogCat.Network,
                                        "Reject RemotePlayerForward: claimed p" + fwd.OriginalPlayerId
                                        + " from p" + _currentReceivePlayerId);
                                    break;
                                }
                                int saved = _currentReceivePlayerId;
                                _currentReceivePlayerId = fwd.OriginalPlayerId;
                                _isForwardedMessage = true;
                                try
                                {
                                    DispatchRemotePlayerForward((NetMessageType)fwd.InnerType, fwd.InnerPayload);
                                }
                                finally
                                {
                                    _isForwardedMessage = false;
                                    _currentReceivePlayerId = saved;
                                }
                                break;
                            }
                        case NetMessageType.VaultState:
                            HandleVaultState(VaultStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WeatherSync:
                            HandleWeatherSync(WeatherSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.FlagBulkSync:
                            HandleFlagBulkSync(FlagBulkSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ReputationBulkSync:
                            HandleReputationBulkSync(ReputationBulkSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ScenarioStateSync:
                            HandleScenarioStateSync(ScenarioSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ScenarioStateBulk:
                            HandleScenarioStateBulk(ScenarioStateBulkMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.HideoutStateSync:
                            HandleHideoutStateSync(HideoutStateSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorkbenchLevelSync:
                            HandleWorkbenchLevelSync(WorkbenchLevelMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.MapStateSync:
                            HandleMapStateSync(MapStateSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.PlayerSkillsSync:
                            HandlePlayerSkillsSync(PlayerSkillsSyncMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldSaveBegin:
                            _worldSaveShare?.HandleBegin(WorldSaveBeginMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldSaveChunk:
                            _worldSaveShare?.HandleChunk(WorldSaveChunkMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldSaveEnd:
                            _worldSaveShare?.HandleEnd(WorldSaveEndMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.WorldRequest:
                            HandleWorldRequest(WorldRequestMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.ChatMessage:
                            {
                                var chat = ChatMessagePayload.Deserialize(new NetReader(payload));
                                if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                                    chat.SenderId = _currentReceivePlayerId;
                                // Sanitize peer input (Yokyy had no length/content clamp)
                                if (chat.Message != null && chat.Message.Length > 160)
                                    chat.Message = chat.Message.Substring(0, 160);
                                if (chat.SenderName != null && chat.SenderName.Length > 32)
                                    chat.SenderName = chat.SenderName.Substring(0, 32);
                                // Skip echo of our own send (we already drew locally)
                                if (chat.SenderId != _localPlayerId)
                                    ChatHud.OnRemote(chat);
                                if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                                {
                                    var chatWriter = new NetWriter();
                                    chat.Serialize(chatWriter);
                                    payload = chatWriter.CopyData();
                                }
                                break;
                            }
                        case NetMessageType.VoiceData:
                            {
                                var voice = VoiceDataMessage.Deserialize(new NetReader(payload));
                                if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                                    voice.PlayerId = _currentReceivePlayerId;
                                Audio.VoiceChatService.OnVoiceData(voice);
                                if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                                {
                                    var vw = new NetWriter();
                                    voice.Serialize(vw);
                                    byte[] body = vw.CopyData();
                                    SendToAllExcept(_currentReceivePlayerId, NetMessageType.VoiceData,
                                        w => w.PutRaw(body), DeliveryMethod.Unreliable);
                                    _suppressForwardThisMessage = true;
                                }
                                break;
                            }
                        case NetMessageType.DialogNpcLock:
                            HandleDialogNpcLock(DialogNpcLockMessage.Deserialize(new NetReader(payload)));
                            break;
                        case NetMessageType.DialogTreeState:
                            HandleDialogTreeState(DialogTreeStateMessage.Deserialize(new NetReader(payload)));
                            break;
                        default:
                            ModRuntime.Log?.LogWarning($"[Network] Unhandled message type: {type}");
                            break;
                    }
                }
                catch (InvalidDataException ex)
                {
                    ModLog.Warn(
                        LogCat.Network,
                        "Rejected malformed " + type + " packet from p"
                        + _currentReceivePlayerId + " (" + (payload != null ? payload.Length : 0)
                        + " bytes): " + ex.Message);
                    return;
                }
                finally
                {
                    _isForwardedMessage = false;
                }
            }

            // === Forward client messages to other clients (3+ support) ===
            if (_suppressForwardThisMessage)
            {
                _suppressForwardThisMessage = false;
                return;
            }

            if (!_isForwardedMessage && _role == NetworkRole.Host && _currentReceivePlayerId > 0)
            {
                if (_forwardableMap.TryGetValue(type, out var fwdKind))
                {
                    if (fwdKind == ForwardableKind.Direct)
                    {
                        // Direct rebroadcast must be reliable (default SendToAllExcept is Unreliable).
                        // PutRaw is already the message body; adding a length
                        // prefix would break deserializers.
                        SendToAllExcept(_currentReceivePlayerId, type, w => w.PutRaw(payload),
                            DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        var fwd = new RemotePlayerForwardMessage
                        {
                            OriginalPlayerId = _currentReceivePlayerId,
                            InnerType = (byte)type,
                            InnerPayload = payload
                        };
                        SendToAllExcept(_currentReceivePlayerId, NetMessageType.RemotePlayerForward,
                            w => fwd.Serialize(w), DeliveryMethod.ReliableOrdered);
                    }
                }
            }
        }
    }
}
