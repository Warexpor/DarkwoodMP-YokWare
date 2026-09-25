using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking.Steam;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Steam SNS backend, separate from LiteNetLib LAN. Both transports use the same Horde messages.
    /// Host migration uses SteamID roster + ConnectP2P (same elect/promote as LAN).
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal void OnSteamPacket(CSteamID remote, byte[] payload)
        {
            if (_backend != ConnectionBackend.Steam || payload == null || payload.Length == 0)
                return;

            // Host: first packet from unknown steam user → register peer (like OnPeerConnected).
            if (_role == NetworkRole.Host && !_steamIdToPlayer.ContainsKey(remote.m_SteamID))
            {
                if (!TryHostAcceptSteamPeer(remote))
                    return;
            }

            if (!_steamIdToPlayer.TryGetValue(remote.m_SteamID, out int playerId))
            {
                // Client: first packet from the host may arrive before the map; bind the host.
                if (_role == NetworkRole.Client && Steam.HostSteamId.IsValid() && remote == Steam.HostSteamId)
                {
                    _steamPeers[1] = remote;
                    _steamIdToPlayer[remote.m_SteamID] = 1;
                    playerId = 1;
                }
                else
                {
                    return;
                }
            }

            DispatchSteamPayload(remote, playerId, payload);
        }

        private bool TryHostAcceptSteamPeer(CSteamID remote)
        {
            int maxPlayers = Config.ModConfig.MaxPlayers?.Value ?? 8;
            if (maxPlayers < 2) maxPlayers = 8;
            // Host counts as 1.
            if (_steamPeers.Count + 1 >= maxPlayers)
            {
                ModLog.Warn(LogCat.Network, "Steam reject " + remote.m_SteamID + " — full");
                Steam.CloseSession(remote);
                return false;
            }

            bool allowDreamJoin = Config.ModConfig.AllowJoinDuringDream != null
                && Config.ModConfig.AllowJoinDuringDream.Value;
            if (!allowDreamJoin
                && (Sync.DreamSession.ShouldRejectNewConnections
                    || Sync.DreamSyncManager.IsDreamActive))
            {
                ModLog.Warn(LogCat.Network, "Steam reject " + remote.m_SteamID + " — dream join blocked");
                Steam.CloseSession(remote);
                return false;
            }

            Steam.AcceptSession(remote);
            int playerId = _nextPlayerId++;
            _steamPeers[playerId] = remote;
            _steamIdToPlayer[remote.m_SteamID] = playerId;

            if (_handshakedPeers.Count == 0)
                _handshakeComplete = false;
            StatusText = $"Steam player {playerId} connected";
            ModLog.Event(LogCat.Network,
                $"Steam player {playerId} connected sid={remote.m_SteamID} (peers={_steamPeers.Count})");

            CompleteHostPeerJoin(playerId);
            return true;
        }

        /// <summary>Shared host post-connect (Handshake + WorldSession) for LAN and Steam.</summary>
        private void CompleteHostPeerJoin(int playerId)
        {
            SendToPlayer(playerId, NetMessageType.Handshake, w =>
            {
                new HandshakeMessage
                {
                    ProtocolVersion = PluginInfo.ProtocolVersion,
                    PlayerId = (short)playerId,
                    HostPlayerId = (short)_localPlayerId
                }.Serialize(w);
            }, DeliveryMethod.ReliableOrdered);

            WorldSessionMessage session = _worldSync.BuildHostSession();
            SendToPlayer(playerId, NetMessageType.WorldSession, w => session.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (HostHasShareableWorld())
            {
                ModLog.Event(LogCat.Session,
                    "Peer " + playerId + " connected while host in-world — "
                    + "deferring gameplay bulk until after world share");
            }
            else
            {
                SendLateJoinGameplayBulk(playerId);
            }

            Connected?.Invoke();
        }

        /// <summary>Shared client post-connect (outbound Handshake) for LAN and Steam.</summary>
        private void CompleteClientPeerJoin()
        {
            _handshakeComplete = false;
            _handshakedPeers.Clear();

            bool alreadyInWorld = ClientReportsAlreadyInWorld() || _migrationInProgress;
            short preferredId = _localPlayerId > 0 ? (short)_localPlayerId : (short)0;
            Broadcast(NetMessageType.Handshake, w =>
            {
                new HandshakeMessage
                {
                    ProtocolVersion = PluginInfo.ProtocolVersion,
                    PlayerId = preferredId,
                    AlreadyInWorld = alreadyInWorld,
                }.Serialize(w);
            }, DeliveryMethod.ReliableOrdered);

            if (alreadyInWorld)
                ModLog.Event(LogCat.Session,
                    "Join pipeline phase 3: co-op reconnect (AlreadyInWorld) — host should skip share");

            SyncCurrentLightState();
            Connected?.Invoke();
        }

        private void DispatchSteamPayload(CSteamID remote, int playerId, byte[] payload)
        {
            if (payload == null || payload.Length < 1)
                return;

            var type = (NetMessageType)payload[0];
            byte[] body;
            if (payload.Length == 1)
            {
                body = new byte[0];
            }
            else
            {
                body = new byte[payload.Length - 1];
                Buffer.BlockCopy(payload, 1, body, 0, body.Length);
            }

            _currentReceivePeer = null;
            _currentReceiveSteamId = remote;
            _currentReceivePlayerId = playerId;
            if (IsConnected)
                ClientPerfProbe.NotePacketRx(type);
            try
            {
                ProcessInboundMessage(type, body);
            }
            finally
            {
                _currentReceiveSteamId = CSteamID.Nil;
            }
        }

        private void HandleSteamPeerDisconnected(int playerId, string reason)
        {
            // Reuse LAN disconnect cleanup by synthesizing the host branch.
            ModLog.Event(LogCat.Network, $"Steam player {playerId} disconnected: " + reason);

            var toRemove = new List<string>();
            foreach (var kv in _dragClaims)
            {
                if (kv.Value == playerId)
                    toRemove.Add(kv.Key);
            }
            foreach (string key in toRemove)
            {
                _dragClaims.Remove(key);
                ReleaseRemoteDragKinematic(key);
                RemoveRemoteDragIds(key);
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(key);
            }

            if (_role == NetworkRole.Host)
            {
                if (playerId > 0)
                {
                    Sync.WorkbenchOpenLock.HostReleaseAllForPlayer(this, playerId);
                    RemovePeerSlot(playerId);
                    _handshakedPeers.Remove(playerId);
                    bool wasLoadingOnly = _peersLoadingWorld.Contains(playerId)
                        && !_peersCoopReconnect.Contains(playerId)
                        && (!_awaitingLateJoinBulk.TryGetValue(playerId, out float seen) || seen <= 0f);
                    bool expectedJoinDetach = _peersLoadingWorld.Contains(playerId)
                        && !_peersCoopReconnect.Contains(playerId);

                    _awaitingLateJoinBulk.Remove(playerId);
                    _pendingHeavyLateJoinBulk.Remove(playerId);
                    _peersLoadingWorld.Remove(playerId);
                    _peersCoopReconnect.Remove(playerId);
                    if (_handshakedPeers.Count == 0)
                        _handshakeComplete = false;
                    WorldProxyHandlers.DestroyRemoteProxy(playerId);
                    DestroyRemoteFlareLight(playerId);
                    DestroyRemoteItemLight(playerId);
                    _remotePlayers.Remove(playerId);
                    PlayerPositionManager.RemovePlayer(playerId);
                    _remoteOutsideLocation.Remove(playerId);
                    Sync.FinalDreamsceneManager.OnRemoteDisconnected(playerId);
                    if (!expectedJoinDetach && !wasLoadingOnly)
                    {
                        if (DeathStateTracker.OnRemoteDisconnected(playerId))
                            DeathStateTracker.TryResolveNightMorning("steam peer disconnect");
                    }
                    StatusText = $"Steam player {playerId} left ({_steamPeers.Count} remaining)";
                }
            }
            else
            {
                if (_suppressHostMigration)
                    return;
                // Client lost host SNS; use the same grant path as a LAN peer drop.
                TryBeginHostMigration("steam host SNS lost");
            }
        }

        private void PollSteamBackend()
        {
            if (_backend != ConnectionBackend.Steam || _steam == null || !_steam.IsActive)
                return;
            _steam.Poll();
        }

        private void ShutdownSteamBackend(bool leaveLobby = true)
        {
            if (_steam != null && _steam.IsActive)
                _steam.Shutdown(leaveLobby);
            // Do not clear peer maps here when called from ClearAllPeerSlots;
            // always wipe steam routing so a later LAN session cannot leak Steam ids.
            // Host-grant reconnect rebuilds maps after ConnectP2PDirect / promote.
            _steamPeers.Clear();
            _steamIdToPlayer.Clear();
            _currentReceiveSteamId = CSteamID.Nil;
            if (_backend == ConnectionBackend.Steam)
                _backend = ConnectionBackend.None;
        }

        private bool SendSteamToPlayer(int playerId, byte[] data, DeliveryMethod method)
            => SendSteamToPlayer(playerId, data, data != null ? data.Length : 0, method);

        private bool SendSteamToPlayer(int playerId, byte[] data, int length, DeliveryMethod method)
        {
            if (!_steamPeers.TryGetValue(playerId, out CSteamID sid))
                return false;
            return Steam.Send(sid, data, length, method);
        }

        private void DisconnectCurrentReceivePeer()
        {
            if (_currentReceivePeer != null)
            {
                _currentReceivePeer.Disconnect();
                return;
            }
            if (_currentReceiveSteamId.IsValid())
            {
                Steam.CloseSession(_currentReceiveSteamId);
                if (_steamIdToPlayer.TryGetValue(_currentReceiveSteamId.m_SteamID, out int pid))
                    HandleSteamPeerDisconnected(pid, "protocol mismatch");
            }
        }

        private int TryRebindPreferredSteamPlayerId(int provisionalId, int preferredId, CSteamID steamId)
        {
            if (preferredId <= 0 || preferredId == provisionalId || !steamId.IsValid())
                return provisionalId;
            if (preferredId == _localPlayerId)
                return provisionalId;
            if (_steamPeers.ContainsKey(preferredId))
                return provisionalId;

            _steamPeers.Remove(provisionalId);
            _steamPeers[preferredId] = steamId;
            _steamIdToPlayer[steamId.m_SteamID] = preferredId;
            if (_handshakedPeers.Remove(provisionalId))
                _handshakedPeers.Add(preferredId);
            if (_awaitingLateJoinBulk.TryGetValue(provisionalId, out float t))
            {
                _awaitingLateJoinBulk.Remove(provisionalId);
                _awaitingLateJoinBulk[preferredId] = t;
            }
            if (_pendingHeavyLateJoinBulk.TryGetValue(provisionalId, out int heavyPhase))
            {
                _pendingHeavyLateJoinBulk.Remove(provisionalId);
                _pendingHeavyLateJoinBulk[preferredId] = heavyPhase;
            }
            if (_peersLoadingWorld.Remove(provisionalId))
                _peersLoadingWorld.Add(preferredId);
            if (_peersCoopReconnect.Remove(provisionalId))
                _peersCoopReconnect.Add(preferredId);

            if (preferredId >= _nextPlayerId)
                _nextPlayerId = preferredId + 1;

            ModLog.Event(LogCat.Network,
                "Steam rebind peer id " + provisionalId + " → preferred " + preferredId);
            return preferredId;
        }
    }
}
