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
        internal void OnSteamPacket(CSteamID remote, byte typeByte, byte[] body, bool reliable)
        {
            if (_backend != ConnectionBackend.Steam || body == null)
                return;

            // Host: first packet from unknown steam user → register peer (like OnPeerConnected).
            if (_role == NetworkRole.Host && !_steamPeers.IsKnown(remote.m_SteamID))
            {
                if (!TryHostAcceptSteamPeer(remote))
                    return;
            }

            if (!_steamPeers.TryGetPlayerId(remote.m_SteamID, out int playerId))
            {
                // Client: first packet from the host may arrive before the map; bind the host.
                if (_role == NetworkRole.Client && Steam.HostSteamId.IsValid() && remote == Steam.HostSteamId)
                {
                    _steamPeers.Set(1, remote);
                    playerId = 1;
                }
                else
                {
                    return;
                }
            }

            DispatchSteamPayload(remote, playerId, (NetMessageType)typeByte, body,
                reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
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
            // A migration survivor is not a new player: it is still on the dream pad.
            bool survivor = IsMigrationSurvivorAddress(remote.m_SteamID.ToString());
            if (!allowDreamJoin && !survivor
                && !Sync.DreamSession.IsFirstPlayTutorial
                && (Sync.DreamSession.ShouldRejectNewConnections
                    || Sync.DreamSyncManager.IsDreamActive
                    || Sync.DreamSyncManager.IsHostDreamEntryPending))
            {
                ModLog.Warn(LogCat.Network, "Steam reject " + remote.m_SteamID + " — dream join blocked");
                Steam.CloseSession(remote);
                return false;
            }

            Steam.AcceptSession(remote);
            int playerId = _nextPlayerId++;
            _steamPeers.Set(playerId, remote);

            if (_session.Link.Handshaked.Count == 0)
                _session.Link.HandshakeComplete = false;
            StatusText = $"Steam player {playerId} connected";
            ModLog.Event(LogCat.Network,
                $"Steam player {playerId} connected sid={remote.m_SteamID} (peers={_steamPeers.Count})");

            if (HostRequiresPassword())
            {
                // Password mode: only the host Handshake goes out now (the client answers it with
                // the key). WorldSession and the join bulk follow once the password is verified
                // (SessionHandlers), and a peer that never proves it is dropped after a grace.
                _steamPeers.MarkUnauthenticated(playerId, UnityEngine.Time.unscaledTime);
                SendHostHandshake(playerId);
                return true;
            }

            CompleteHostPeerJoin(playerId);
            return true;
        }

        private void SendHostHandshake(int playerId)
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
        }

        /// <summary>Shared host post-connect (Handshake + WorldSession) for LAN and Steam.</summary>
        private void CompleteHostPeerJoin(int playerId, bool sendHandshake = true)
        {
            if (sendHandshake)
                SendHostHandshake(playerId);

            WorldSessionMessage session = _worldSync.BuildHostSession();
            SendToPlayer(playerId, NetMessageType.WorldSession, w => session.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (HostHasShareableWorld())
            {
                ModLog.Event(LogCat.Session,
                    "Peer " + playerId + " connected while host fully in-world — "
                    + "deferring gameplay bulk until after handshake / world share");
            }
            else
            {
                // Title / mid-load joiners: do NOT dump sticky bulk yet — wait for
                // HostWorldReady + handshake AlreadyInWorld / share pipeline.
                ModLog.Event(LogCat.Session,
                    "Peer " + playerId + " connected while host not fully in-world — "
                    + "holding late-join bulk until host ready or phase-3 reconnect");
            }

            Connected?.Invoke();
        }

        /// <summary>Shared client post-connect (outbound Handshake) for LAN and Steam.</summary>
        private void CompleteClientPeerJoin()
        {
            _session.Link.HandshakeComplete = false;
            _session.Link.Handshaked.Clear();

            bool alreadyInWorld = ClientReportsAlreadyInWorld() || _migrationInProgress;
            short preferredId = _localPlayerId > 0 ? (short)_localPlayerId : (short)0;
            string lanKey = ClientStateBackup.GetOrCreateLanClientKey() ?? string.Empty;
            // Identity of the world we actually have loaded; the host verifies AlreadyInWorld against it.
            GetLocalWorldIdentity(mint: false, out string worldCampaignId, out int worldChapterId);
            Broadcast(NetMessageType.Handshake, w =>
            {
                new HandshakeMessage
                {
                    ProtocolVersion = PluginInfo.ProtocolVersion,
                    PlayerId = preferredId,
                    AlreadyInWorld = alreadyInWorld,
                    StableClientKey = lanKey,
                    CampaignId = worldCampaignId,
                    ChapterId = worldChapterId,
                    ConnectionKey = IsSteamSession ? Config.ModConfig.GetConnectionKey() : string.Empty,
                }.Serialize(w);
            }, DeliveryMethod.ReliableOrdered);

            if (alreadyInWorld)
                ModLog.Event(LogCat.Session,
                    "Join pipeline phase 3: co-op reconnect (AlreadyInWorld) — host should skip share");

            SyncCurrentLightState();
            SyncCurrentAnimLibrary();
            Connected?.Invoke();
        }

        private void DispatchSteamPayload(CSteamID remote, int playerId, NetMessageType type, byte[] body,
            DeliveryMethod deliveryMethod)
        {
            // Steam has no connection-request key like LiteNetLib: until a peer's Handshake proved the
            // host password, nothing it sends is processed (it could otherwise drive host handlers).
            if (_role == NetworkRole.Host && HostRequiresPassword()
                && type != NetMessageType.Handshake && !_steamPeers.IsPasswordOk(remote.m_SteamID))
            {
                if (NetLogThrottle.ShouldLog("steam-unauth:" + remote.m_SteamID, 10f, out int dropped))
                    ModLog.Warn(LogCat.Network,
                        "Dropping " + type + " from unauthenticated Steam peer p" + playerId
                        + " (password not yet verified)" + NetLogThrottle.SuppressedSuffix(dropped));
                return;
            }

            _currentReceivePeer = null;
            _currentReceiveSteamId = remote;
            _currentReceivePlayerId = playerId;
            if (IsConnected)
                ClientPerfProbe.NotePacketRx(type);
            try
            {
                ProcessInboundMessage(type, body, deliveryMethod);
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

            // Same N-peer claim release as LAN OnPeerDisconnected.
            if (playerId > 0)
                PlayerInteractHandlers?.ReleaseDragClaimsForDisconnectedPlayer(
                    playerId, broadcastStop: _role == NetworkRole.Host);

            if (_role == NetworkRole.Host)
            {
                if (playerId > 0)
                {
                    // Remove Steam slot first so roster build excludes the leaver.
                    RemovePeerSlot(playerId);
                    OnHostPeerDisconnectedGameplay(playerId, removeLanSlot: false, reasonTag: "steam peer disconnect");
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
            // Always wipe Steam routing so a later LAN session cannot leak Steam ids.
            // Host-grant reconnect rebuilds the map after ConnectP2PDirect / promote.
            _steamPeers.Clear();
            _currentReceiveSteamId = CSteamID.Nil;
            if (_backend == ConnectionBackend.Steam)
                _backend = ConnectionBackend.None;
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
                if (_steamPeers.TryGetPlayerId(_currentReceiveSteamId.m_SteamID, out int pid))
                    HandleSteamPeerDisconnected(pid, "protocol mismatch");
            }
        }

        private int TryRebindPreferredSteamPlayerId(int provisionalId, int preferredId, CSteamID steamId,
            bool reservedForResume)
        {
            if (preferredId <= 0 || preferredId == provisionalId || !steamId.IsValid())
                return provisionalId;
            if (preferredId == _localPlayerId)
                return provisionalId;
            if (_steamPeers.Contains(preferredId))
                return provisionalId;
            if (!reservedForResume
                && !TryConsumeMigrationReservation(preferredId, steamId.m_SteamID.ToString()))
                return provisionalId;

            if (!_steamPeers.Rebind(provisionalId, preferredId))
                return provisionalId;
            RebindPlayerIdState(provisionalId, preferredId);

            ModLog.Event(LogCat.Network,
                "Steam rebind peer id " + provisionalId + " → preferred " + preferredId);
            return preferredId;
        }
    }
}
