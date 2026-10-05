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
        /// <summary>Inbound handlers: Session.</summary>
        private void RegisterSessionHandlers()
        {
            On(NetMessageType.Handshake, HandshakeMessage.Deserialize, m => HandleHandshake(m));
            On(NetMessageType.SessionSettings, SessionSettingsMessage.Deserialize, m => HandleSessionSettings(m));
            On(NetMessageType.DesyncDigest, DesyncDigestMessage.Deserialize, m => Sync.DesyncCheck.ClientOnDigest(this, m));
            On(NetMessageType.DesyncDetailRequest, DesyncDetailRequestMessage.Deserialize,
                m => Sync.DesyncCheck.HostOnDetailRequest(this, _currentReceivePlayerId, m));
            On(NetMessageType.DesyncDetail, DesyncDetailMessage.Deserialize, m => Sync.DesyncCheck.ClientOnDetail(this, m));
            On(NetMessageType.DesyncReport, DesyncReportMessage.Deserialize,
                m => Sync.DesyncCheck.HostOnReport(this, _currentReceivePlayerId, m));
            On(NetMessageType.WorldSession, WorldSessionMessage.Deserialize, m => HandleWorldSession(m));
            OnRaw(NetMessageType.SaveSync, _ => HandleSaveSync());
            On(NetMessageType.TimeSync, TimeSyncMessage.Deserialize, m => WorldWeatherTimeHandlers.HandleTimeSync(m));
            On(NetMessageType.SleepEndRequest, SleepEndRequestMessage.Deserialize, m => NightHandlers.HandleSleepEndRequest(m));
            On(NetMessageType.AfterNightEndRequest, AfterNightEndRequestMessage.Deserialize, m => NightHandlers.HandleAfterNightEndRequest(m));
            On(NetMessageType.PeerRoster, PeerRosterMessage.Deserialize, m => HandlePeerRoster(m));
            On(NetMessageType.HostHandoff, HostHandoffMessage.Deserialize, m => HandleHostHandoff(m));
            On(NetMessageType.ScenarioSync, ScenarioSyncMessage.Deserialize, m => NightHandlers.HandleScenarioSync(m));
            On(NetMessageType.ScenarioEventFired, ScenarioEventFiredMessage.Deserialize, m => NightHandlers.HandleScenarioEventFired(m));
            On(NetMessageType.FlagSync, FlagSyncMessage.Deserialize, m => FlagHandlers.HandleFlagSync(m));
            On(NetMessageType.DreamSessionBulk, DreamSessionBulkMessage.Deserialize, m => DreamHandlers.HandleDreamSessionBulk(m));
            On(NetMessageType.ClientStateBackup, ClientStateBackupMessage.Deserialize, m => HandleClientStateBackup(m));
            On(NetMessageType.GameEventsBulk, GameEventsBulkMessage.Deserialize, m => GameEventHandlers.HandleGameEventsBulk(m));
            On(NetMessageType.HideoutUpgrade, HideoutUpgradeMessage.Deserialize, m => ContainerLootHandlers.HandleHideoutUpgrade(m));
            On(NetMessageType.JournalBulkSync, JournalBulkSyncMessage.Deserialize, m => JournalHandlers.HandleJournalBulkSync(m));
            On(NetMessageType.ContainerStateRequest, ContainerStateRequestMessage.Deserialize, m => ContainerDeathDropHandlers.HandleContainerStateRequest(m));
            On(NetMessageType.ContainerStateSync, ContainerStateSyncMessage.Deserialize, m => ContainerPendingHandlers.HandleContainerStateSync(m));
            On(NetMessageType.ContainerTakeDenied, ContainerTakeDeniedMessage.Deserialize, m => ContainerLootHandlers.HandleContainerTakeDenied(m));
            On(NetMessageType.ReputationSync, ReputationSyncMessage.Deserialize, m => ContainerLootHandlers.HandleReputationSync(m));
            On(NetMessageType.WeatherSync, WeatherSyncMessage.Deserialize, m => WorldWeatherTimeHandlers.HandleWeatherSync(m));
            On(NetMessageType.FlagBulkSync, FlagBulkSyncMessage.Deserialize, m => FlagHandlers.HandleFlagBulkSync(m));
            On(NetMessageType.ReputationBulkSync, ReputationBulkSyncMessage.Deserialize, m => BulkSyncHandlers.HandleReputationBulkSync(m));
            On(NetMessageType.ScenarioStateSync, ScenarioSyncMessage.Deserialize, m => BulkSyncHandlers.HandleScenarioStateSync(m));
            On(NetMessageType.ScenarioStateBulk, ScenarioStateBulkMessage.Deserialize, m => BulkSyncHandlers.HandleScenarioStateBulk(m));
            On(NetMessageType.HideoutStateSync, HideoutStateSyncMessage.Deserialize, m => BulkSyncHandlers.HandleHideoutStateSync(m));
            On(NetMessageType.WorkbenchLevelSync, WorkbenchLevelMessage.Deserialize, m => BulkSyncHandlers.HandleWorkbenchLevelSync(m));
            On(NetMessageType.MapStateSync, MapStateSyncMessage.Deserialize, m => BulkSyncHandlers.HandleMapStateSync(m));
            On(NetMessageType.PlayerSkillsSync, PlayerSkillsSyncMessage.Deserialize, m => BulkSyncHandlers.HandlePlayerSkillsSync(m));
            On(NetMessageType.WorldSaveBegin, WorldSaveBeginMessage.Deserialize, m => _worldSaveShare?.HandleBegin(m));
            On(NetMessageType.WorldSaveChunk, WorldSaveChunkMessage.Deserialize, m => _worldSaveShare?.HandleChunk(m));
            On(NetMessageType.WorldSaveEnd, WorldSaveEndMessage.Deserialize, m => _worldSaveShare?.HandleEnd(m));
            On(NetMessageType.WorldRequest, WorldRequestMessage.Deserialize, m => HandleWorldRequest(m));
            On(NetMessageType.HostWorldReady, HostWorldReadyMessage.Deserialize, m => HandleHostWorldReady(m));
            OnRaw(NetMessageType.ChatMessage, payload =>
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
                {
                    // A HUD failure must not cost the other peers the relay below.
                    try { ChatHud.OnRemote(chat); }
                    catch (System.Exception ex)
                    {
                        ModLog.Warn(LogCat.Network, "ChatHud.OnRemote failed: " + ex.Message);
                    }
                }
                // Relay the sanitised, host-stamped body, not the raw payload (client-claimed
                // SenderId, no length clamp).
                if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                    RelayStamped(w => chat.Serialize(w));
            });
            OnRaw(NetMessageType.VoiceData, payload =>
            {
                var voice = VoiceDataMessage.Deserialize(new NetReader(payload));
                if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                    voice.PlayerId = _currentReceivePlayerId;
                Audio.VoiceChatService.OnVoiceData(voice);
                if (_role == NetworkRole.Host && _currentReceivePlayerId > 0)
                    RelayStamped(w => voice.Serialize(w));
            });
        }
    }
}
