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
        /// <summary>Inbound dispatch slice: Session.</summary>
        private bool TryDispatchSession(NetMessageType type, byte[] payload)
        {
            switch (type)
            {
                        case NetMessageType.Handshake:
                            HandleHandshake(HandshakeMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorldSession:
                            HandleWorldSession(WorldSessionMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.SaveSync:
                            HandleSaveSync();
                            return true;
                        case NetMessageType.TimeSync:
                            WorldWeatherTimeHandlers.HandleTimeSync(TimeSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.SleepEndRequest:
                            NightHandlers.HandleSleepEndRequest(
                                SleepEndRequestMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.AfterNightEndRequest:
                            NightHandlers.HandleAfterNightEndRequest(
                                AfterNightEndRequestMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PeerRoster:
                            HandlePeerRoster(PeerRosterMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.HostHandoff:
                            HandleHostHandoff(HostHandoffMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ScenarioSync:
                            NightHandlers.HandleScenarioSync(
                                ScenarioSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ScenarioEventFired:
                            NightHandlers.HandleScenarioEventFired(
                                ScenarioEventFiredMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.FlagSync:
                            FlagHandlers.HandleFlagSync(
                                FlagSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DreamSessionBulk:
                            DreamHandlers.HandleDreamSessionBulk(
                                DreamSessionBulkMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ClientStateBackup:
                            HandleClientStateBackup(ClientStateBackupMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.GameEventsBulk:
                            GameEventHandlers.HandleGameEventsBulk(
                                GameEventsBulkMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.HideoutUpgrade:
                            ContainerHandlers.HandleHideoutUpgrade(
                                HideoutUpgradeMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.JournalBulkSync:
                            JournalHandlers.HandleJournalBulkSync(
                                JournalBulkSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ContainerStateRequest:
                            ContainerHandlers.HandleContainerStateRequest(
                                ContainerStateRequestMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ContainerStateSync:
                            ContainerHandlers.HandleContainerStateSync(
                                ContainerStateSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ContainerTakeDenied:
                            ContainerHandlers.HandleContainerTakeDenied(
                                ContainerTakeDeniedMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ReputationSync:
                            ContainerHandlers.HandleReputationSync(
                                ReputationSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WeatherSync:
                            WorldWeatherTimeHandlers.HandleWeatherSync(WeatherSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.FlagBulkSync:
                            FlagHandlers.HandleFlagBulkSync(
                                FlagBulkSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ReputationBulkSync:
                            BulkSyncHandlers.HandleReputationBulkSync(
                                ReputationBulkSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ScenarioStateSync:
                            BulkSyncHandlers.HandleScenarioStateSync(
                                ScenarioSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ScenarioStateBulk:
                            BulkSyncHandlers.HandleScenarioStateBulk(
                                ScenarioStateBulkMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.HideoutStateSync:
                            BulkSyncHandlers.HandleHideoutStateSync(
                                HideoutStateSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorkbenchLevelSync:
                            BulkSyncHandlers.HandleWorkbenchLevelSync(
                                WorkbenchLevelMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.MapStateSync:
                            BulkSyncHandlers.HandleMapStateSync(
                                MapStateSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerSkillsSync:
                            BulkSyncHandlers.HandlePlayerSkillsSync(
                                PlayerSkillsSyncMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorldSaveBegin:
                            _worldSaveShare?.HandleBegin(WorldSaveBeginMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorldSaveChunk:
                            _worldSaveShare?.HandleChunk(WorldSaveChunkMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorldSaveEnd:
                            _worldSaveShare?.HandleEnd(WorldSaveEndMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.WorldRequest:
                            HandleWorldRequest(WorldRequestMessage.Deserialize(new NetReader(payload)));
                            return true;
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
                                return true;
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
                                return true;
                            }
                default:
                    return false;
            }
        }
    }
}
