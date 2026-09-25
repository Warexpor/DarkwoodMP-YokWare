using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        private void HandleHandshake(HandshakeMessage handshake)
        {
            if (handshake.ProtocolVersion != PluginInfo.ProtocolVersion)
            {
                ModLog.Error(LogCat.Network,
                    "Protocol mismatch. Local="
                    + PluginInfo.ProtocolVersion
                    + " remote="
                    + handshake.ProtocolVersion
                    + " — update both mods to the same version.");
                DisconnectCurrentReceivePeer();
                return;
            }

            if (_role == NetworkRole.Client)
            {
                // Client: store our assigned playerId from host's Handshake
                if (handshake.PlayerId > 0)
                    _localPlayerId = handshake.PlayerId;
                if (handshake.HostPlayerId > 0)
                    _hostPlayerId = handshake.HostPlayerId;
                _handshakeComplete = true;
                _handshakedPeers.Clear();
                // Host id may not be 1 after host-grant migration.
                int hostId = _hostPlayerId > 0 ? _hostPlayerId : 1;
                _handshakedPeers.Add(hostId);
                // Rebind provisional peer slot if host id changed.
                if (_currentReceivePeer != null)
                {
                    int oldKey = -1;
                    foreach (var kvp in _peers)
                    {
                        if (kvp.Value == _currentReceivePeer) { oldKey = kvp.Key; break; }
                    }
                    if (oldKey > 0 && oldKey != hostId)
                    {
                        _peers.Remove(oldKey);
                        _peers[hostId] = _currentReceivePeer;
                    }
                    else if (oldKey < 0)
                        _peers[hostId] = _currentReceivePeer;
                }
                else if (IsSteamSession && _currentReceiveSteamId.IsValid())
                {
                    int oldKey = -1;
                    foreach (var kvp in _steamPeers)
                    {
                        if (kvp.Value == _currentReceiveSteamId) { oldKey = kvp.Key; break; }
                    }
                    if (oldKey > 0 && oldKey != hostId)
                    {
                        _steamPeers.Remove(oldKey);
                        _steamPeers[hostId] = _currentReceiveSteamId;
                        _steamIdToPlayer[_currentReceiveSteamId.m_SteamID] = hostId;
                    }
                    else if (oldKey < 0)
                    {
                        _steamPeers[hostId] = _currentReceiveSteamId;
                        _steamIdToPlayer[_currentReceiveSteamId.m_SteamID] = hostId;
                    }
                }

                _migrationInProgress = false;
                _migrationRetryCount = 0;

                if (ClientReportsAlreadyInWorld())
                {
                    StatusText = "Reconnected — co-op sync…";
                    ModLog.Event(LogCat.Network,
                        "Handshake OK — assigned PlayerId=" + _localPlayerId
                        + " hostId=" + hostId
                        + " (phase 3 / migration reconnect)");
                    BeginClientBackupRestoreWait();
                }
                else
                {
                    StatusText = "Connected — waiting for host world…";
                    ModLog.Event(LogCat.Network, "Handshake OK — assigned PlayerId=" + _localPlayerId
                        + " hostId=" + hostId);
                    if (Core.mainMenu)
                        ModLog.Event(LogCat.Session,
                            "Join pipeline phase 1: transfer link up — waiting for world share, then offline load.");
                }
            }
            else
            {
                int playerId = _currentReceivePlayerId;
                // Migration reconnect: client put preferred id in Handshake.PlayerId.
                if (handshake.AlreadyInWorld && handshake.PlayerId > 0)
                {
                    int rebound = playerId;
                    if (_currentReceivePeer != null)
                        rebound = TryRebindPreferredPlayerId(playerId, handshake.PlayerId, _currentReceivePeer);
                    else if (IsSteamSession && _currentReceiveSteamId.IsValid())
                        rebound = TryRebindPreferredSteamPlayerId(playerId, handshake.PlayerId, _currentReceiveSteamId);

                    if (rebound != playerId)
                    {
                        playerId = rebound;
                        _currentReceivePlayerId = rebound;
                        // Confirm preferred id to client (same wire as assign).
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
                }

                if (playerId > 0)
                    _handshakedPeers.Add(playerId);
                // Host gameplay traffic stays up for already-ready peers; first ready peer enables send loop.
                _handshakeComplete = _handshakedPeers.Count > 0;
                ModLog.Event(LogCat.Network, $"Handshake OK from Player {playerId} (ready peers: {_handshakedPeers.Count})");
                StatusText = $"Player {playerId} joined";

                // Sync current weather state to the newly connected client only
                SendWeatherSyncTo(playerId);
                // Roster ASAP so survivors can migrate if *this* host later dies.
                BroadcastPeerRoster();
                _peerRosterTimer = 0f;

                // Join pipeline:
                //   phase 1 title join  → share world (client offline-loads, disconnects)
                //   phase 3 reconnect   → AlreadyInWorld: skip share, late-join bulk only
                if (playerId > 0)
                {
                    if (handshake.AlreadyInWorld)
                    {
                        ModLog.Event(LogCat.Session,
                            "Join pipeline phase 3: peer " + playerId
                            + " already in world — skip world share, queue late-join bulk");
                        // Do NOT MarkPeerLoadingWorld: soft-reconnect peers already have a
                        // playable body. Muting PlayerState prevented host proxy on client
                        // (log: Light RX drop p1 proxy=null, no Created proxy for player 1).
                        // Entity/physics still gate on first ready via IsPeerReadyForGameplay
                        // only after explicit loading mark; leave them open for phase 3.
                        _peersCoopReconnect.Add(playerId);
                        _awaitingLateJoinBulk[playerId] = 0f;
                        // Immediate bulk settle path (shorter for reconnect).
                        MarkPeerGameplayReady(playerId);
                        WorldGenerator intro = Singleton<WorldGenerator>.Instance;
                        if (intro != null && intro.playingIntro)
                            PrologueSync.SendStartTo(playerId);
                        else if (Player.Instance != null && Player.Instance.firstPlay)
                            PrologueSync.SendEndTo(playerId);
                    }
                    else if (HostHasShareableWorld())
                    {
                        ModLog.Event(LogCat.Save,
                            "Join pipeline phase 1: client " + playerId
                            + " handshaked on title — scheduling world share");
                        MarkPeerLoadingWorld(playerId);
                        StartCoroutine(DelayedWorldShareTo(playerId, 0.75f));
                    }
                    else
                    {
                        ModLog.Warn(LogCat.Save,
                            "Client " + playerId + " joined but host is NOT in-world (mainMenu="
                            + Core.mainMenu + " profile=" + (Core.currentProfile != null)
                            + " player=" + (Player.Instance != null)
                            + " loaded=" + Core.loadedGame
                            + ") — no auto world share yet. Will share when host enters chapter, or use F2 Resend.");
                    }
                }
            }
        }

        private System.Collections.IEnumerator DelayedWorldShareTo(int playerId, float delaySec)
        {
            float t = 0f;
            while (t < delaySec)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (_role != NetworkRole.Host) yield break;
            if (!_handshakedPeers.Contains(playerId)) yield break;
            if (!HostHasShareableWorld())
            {
                ModLog.Warn(LogCat.Save, "Delayed world share aborted — host left world before share for p" + playerId);
                // Still try bulk; the client may load a matching save manually.
                SendLateJoinGameplayBulk(playerId);
                yield break;
            }
            ModLog.Event(LogCat.Save, "Auto world share → player " + playerId + " starting now");
            MarkPeerLoadingWorld(playerId);
            _worldSaveShare?.ScheduleHostShareToPlayer(playerId);
            // Mark bulk pending (settle clock starts on first valid PlayerState).
            _awaitingLateJoinBulk[playerId] = 0f;
            ModLog.Event(LogCat.Session,
                "Player " + playerId + " queued for late-join bulk after "
                + ClientBulkSettleSeconds.ToString("F0") + "s settled in-world");
        }

        private float _lastWorldRequestSentAt = -999f;
        private const float WorldRequestMinIntervalSec = 12f;

        /// <summary>
        /// Client pull: ask host to push world share (Yokyy RequestWorld equivalent).
        /// Rate-limited; only while on title and not already receiving.
        /// </summary>
        public bool RequestHostWorld(string reason = null)
        {
            if (_role != NetworkRole.Client || !IsConnected || !_handshakeComplete)
                return false;
            if (!Core.mainMenu)
                return false;
            if (_worldSaveShare != null && _worldSaveShare.IsClientReceivingOrApplying)
                return false;
            if (UnityEngine.Time.unscaledTime - _lastWorldRequestSentAt < WorldRequestMinIntervalSec)
                return false;

            _lastWorldRequestSentAt = UnityEngine.Time.unscaledTime;
            Send(NetMessageType.WorldRequest,
                w => new WorldRequestMessage { RequesterId = _localPlayerId }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            ModLog.Event(LogCat.Save,
                "WorldRequest → host (" + (reason ?? "manual") + ") localId=" + _localPlayerId);
            StatusText = "Requesting host world…";
            return true;
        }

        private void HandleWorldRequest(WorldRequestMessage msg)
        {
            if (_role != NetworkRole.Host)
                return;

            int playerId = _currentReceivePlayerId;
            if (playerId <= 0)
                playerId = msg.RequesterId;
            if (playerId <= 0)
            {
                ModLog.Warn(LogCat.Save, "WorldRequest ignored — no player id");
                return;
            }
            if (!_handshakedPeers.Contains(playerId))
            {
                ModLog.Warn(LogCat.Save, "WorldRequest from p" + playerId + " ignored — not handshaked");
                return;
            }

            if (!HostHasShareableWorld())
            {
                ModLog.Warn(LogCat.Save,
                    "WorldRequest from p" + playerId
                    + " — host not in-world yet (mainMenu=" + Core.mainMenu
                    + " player=" + (Player.Instance != null)
                    + "). Will share when host enters chapter, or client retries.");
                return;
            }

            ModLog.Event(LogCat.Save,
                "WorldRequest from p" + playerId + " — scheduling share to that peer");
            MarkPeerLoadingWorld(playerId);
            _worldSaveShare?.ScheduleHostShareToPlayer(playerId);
            _awaitingLateJoinBulk[playerId] = 0f;
        }

        private void HandleWorldSession(WorldSessionMessage session)
        {
            _worldSync.ApplyHostSession(session, asClient: _role == NetworkRole.Client);
            if (_role == NetworkRole.Client)
                StatusText = "Synced — load " + session.SaveSlotName + " (ch" + session.ChapterId + ")";
        }

    }
}
