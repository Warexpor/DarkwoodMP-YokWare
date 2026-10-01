using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking.Steam
{
    /// <summary>
    /// Steam lobbies + SteamNetworkingSockets P2P for YokWare co-op.
    /// Same application framing as LAN (byte type + body); no LiteNetLib on this path.
    /// Requires the game's existing SteamManager (SteamAPI already Init).
    /// </summary>
    public sealed partial class SteamCoopTransport
    {
        public const string LobbyKeyMod = "yokware";
        public const string LobbyKeyProto = "proto";
        public const string LobbyKeyName = "name";
        /// <summary>Darkwood Steam AppID.</summary>
        public const uint DarkwoodAppId = 274520;
        /// <summary>SNS virtual port (matches friend Yokyy SteamTransport).</summary>
        private const int VirtualPort = 17;
        private const int MaxMessagesPerPoll = 128;
        private const int CloseReasonGeneric = 1000;
        private const int CloseReasonRejected = 1001;
        private const int CloseReasonShutdown = 1003;
        /// <summary>k_nSteamNetworkingSend_Reliable</summary>
        private const int SendReliable = 8;
        /// <summary>k_nSteamNetworkingSend_Unreliable | NoNagle | NoDelay ≈ friend unreliable=0; use NoNagle(1)+NoDelay(4)=5</summary>
        private const int SendUnreliableNoDelay = 0 | 1 | 4;

        private readonly LanNetworkManager _owner;
        private readonly IntPtr[] _recvBuffer = new IntPtr[MaxMessagesPerPoll];
        private readonly Dictionary<ulong, HSteamNetConnection> _connBySteamId =
            new Dictionary<ulong, HSteamNetConnection>();
        private readonly Dictionary<uint, ulong> _steamIdByConn = new Dictionary<uint, ulong>();
        private readonly Dictionary<uint, Queue<byte[]>> _reliableOutbox =
            new Dictionary<uint, Queue<byte[]>>();

        private Callback<GameLobbyJoinRequested_t> _cbLobbyJoinRequested;
        private Callback<LobbyChatUpdate_t> _cbLobbyChatUpdate;
        private Callback<SteamNetConnectionStatusChangedCallback_t> _cbConnStatus;
        private CallResult<LobbyCreated_t> _crLobbyCreated;
        private CallResult<LobbyEnter_t> _crLobbyEnter;

        private CSteamID _lobbyId = CSteamID.Nil;
        private CSteamID _hostSteamId = CSteamID.Nil;
        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;
        private HSteamNetConnection _serverConn = HSteamNetConnection.Invalid;
        private bool _active;
        private bool _hosting;
        private bool _clientTransportReady;
        private DateTime _clientConnectStartedUtc = DateTime.MinValue;
        private ulong _pendingLaunchLobby;

        private static readonly TimeSpan ClientConnectTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan HostLobbyGrace = TimeSpan.FromSeconds(4);

        // Connections the host has not yet accepted because the peer was not (yet)
        // visible in the lobby member list; member-list replication can lag after
        // JoinLobby. Re-checked in Poll(); rejected if still absent after the grace window.
        private struct PendingHostConnect { public HSteamNetConnection Conn; public DateTime Since; }
        private readonly Dictionary<ulong, PendingHostConnect> _pendingHostConnects = new Dictionary<ulong, PendingHostConnect>();

        /// <summary>
        /// Steam IDs allowed to ConnectP2P during host-grant (roster survivors).
        /// Covers crash migration when the old lobby is gone and peers are not members yet.
        /// </summary>
        private readonly HashSet<ulong> _migrationAllow = new HashSet<ulong>();
        private DateTime _migrationAllowUntilUtc = DateTime.MinValue;

        public bool IsActive => _active;
        public bool IsHosting => _hosting;
        public CSteamID LobbyId => _lobbyId;
        public CSteamID HostSteamId => _hostSteamId;
        public string LobbyIdString => _lobbyId.IsValid() ? _lobbyId.m_SteamID.ToString() : "";
        public ulong PendingLaunchLobby => _pendingLaunchLobby;
        public const int MigrationVirtualPort = VirtualPort;

        public SteamCoopTransport(LanNetworkManager owner)
        {
            _owner = owner;
        }

        public static bool IsSteamReady(out string failReason)
        {
            failReason = null;
            try
            {
                if (!SteamManager.Initialized)
                {
                    failReason = "SteamManager not initialized (launch via Steam / check steam_api).";
                    return false;
                }
                if (!SteamUser.BLoggedOn())
                {
                    failReason = "Steam user not logged on.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                failReason = "Steam API unavailable: " + ex.Message;
                return false;
            }
        }

        public static CSteamID LocalSteamId()
        {
            try { return SteamUser.GetSteamID(); }
            catch { return CSteamID.Nil; }
        }

        public ulong ConsumePendingLaunchLobby()
        {
            ulong id = _pendingLaunchLobby;
            _pendingLaunchLobby = 0;
            return id;
        }

        public void EnsureCallbacks()
        {
            if (_cbLobbyJoinRequested != null)
                return;
            _cbLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _cbConnStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _crLobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _crLobbyEnter = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
            ParseLaunchArgs();
        }

        private void ParseLaunchArgs()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase)
                        && ulong.TryParse(args[i + 1], out ulong lobby)
                        && lobby != 0)
                    {
                        _pendingLaunchLobby = lobby;
                        ModLog.Event(LogCat.Network,
                            "Steam launched via invite — pending lobby " + lobby);
                    }
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Chapter resume: re-open SNS listen on a lobby this host kept through the scene load
        /// (returning clients rejoin the same lobby id). Falls back to a fresh lobby when the
        /// kept one is gone or no longer owned by this user.
        /// </summary>
        public bool StartHost(CSteamID reuseLobby)
        {
            if (!reuseLobby.IsValid())
                return StartHost();
            if (!IsSteamReady(out string fail))
            {
                ModLog.Error(LogCat.Network, "Steam host failed: " + fail);
                return false;
            }

            CSteamID owner = CSteamID.Nil;
            try { owner = SteamMatchmaking.GetLobbyOwner(reuseLobby); }
            catch { /* lobby unknown */ }
            if (!owner.IsValid() || owner != LocalSteamId())
            {
                ModLog.Warn(LogCat.Network,
                    "Steam host resume: kept lobby " + reuseLobby.m_SteamID
                    + " is gone or not owned by this user — creating a new lobby (clients cannot rejoin the old id)");
                try { SteamMatchmaking.LeaveLobby(reuseLobby); }
                catch { /* tear */ }
                return StartHost();
            }

            EnsureCallbacks();
            ShutdownInternal(leaveLobby: false);
            SteamRelay.WarmRelay();
            if (!CreateListenSocket())
                return false;

            _hosting = true;
            _active = true;
            _hostSteamId = LocalSteamId();
            _lobbyId = reuseLobby;
            ApplyHostLobbyData();
            ModLog.Event(LogCat.Network,
                "Steam host resume: re-listening on kept lobby " + _lobbyId.m_SteamID);
            _owner.OnSteamLobbyReady(_lobbyId, isHost: true);
            return true;
        }

        public bool StartHost()
        {
            if (!IsSteamReady(out string fail))
            {
                ModLog.Error(LogCat.Network, "Steam host failed: " + fail);
                return false;
            }

            EnsureCallbacks();
            ShutdownInternal(leaveLobby: true);

            SteamRelay.WarmRelay();
            if (!CreateListenSocket())
                return false;

            int max = Mathf.Clamp(ModConfig.MaxPlayers?.Value ?? 8, 2, 16);
            ELobbyType lobbyType = ResolveLobbyType();

            _hosting = true;
            _active = true;
            _hostSteamId = LocalSteamId();

            SteamAPICall_t call = SteamMatchmaking.CreateLobby(lobbyType, max);
            _crLobbyCreated.Set(call);
            ModLog.Event(LogCat.Network,
                "Steam host: CreateLobby type=" + lobbyType + " max=" + max + " SNS listen");
            return true;
        }

        private static ELobbyType ResolveLobbyType()
        {
            string raw = (ModConfig.SteamLobbyType?.Value ?? "friends").Trim().ToLowerInvariant();
            switch (raw)
            {
                case "public":
                    return ELobbyType.k_ELobbyTypePublic;
                case "private":
                    return ELobbyType.k_ELobbyTypePrivate;
                default:
                    return ELobbyType.k_ELobbyTypeFriendsOnly;
            }
        }

        private bool CreateListenSocket()
        {
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
            if (_listenSocket == HSteamListenSocket.Invalid)
            {
                ModLog.Error(LogCat.Network, "CreateListenSocketP2P failed — is Steam running?");
                ShutdownInternal(leaveLobby: false);
                return false;
            }
            _pollGroup = SteamNetworkingSockets.CreatePollGroup();
            if (_pollGroup == HSteamNetPollGroup.Invalid)
            {
                ModLog.Error(LogCat.Network, "CreatePollGroup failed");
                ShutdownInternal(leaveLobby: false);
                return false;
            }
            return true;
        }

        public bool JoinLobby(CSteamID lobbyId)
        {
            if (!IsSteamReady(out string fail))
            {
                ModLog.Error(LogCat.Network, "Steam join failed: " + fail);
                return false;
            }
            if (!lobbyId.IsValid())
            {
                ModLog.Error(LogCat.Network, "Steam join failed: invalid lobby id.");
                return false;
            }

            EnsureCallbacks();
            ShutdownInternal(leaveLobby: true);
            SteamRelay.WarmRelay();

            _hosting = false;
            _active = true;
            _clientTransportReady = false;
            _clientConnectStartedUtc = DateTime.MinValue;
            _lobbyId = lobbyId;

            SteamAPICall_t call = SteamMatchmaking.JoinLobby(lobbyId);
            _crLobbyEnter.Set(call);
            ModLog.Event(LogCat.Network, "Steam join: JoinLobby " + lobbyId.m_SteamID);
            return true;
        }

        public bool TryParseLobbyId(string raw, out CSteamID lobbyId)
        {
            lobbyId = CSteamID.Nil;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            raw = raw.Trim();
            if (raw.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = raw.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (string.Equals(parts[i], "joinlobby", StringComparison.OrdinalIgnoreCase)
                        && i + 2 < parts.Length
                        && ulong.TryParse(parts[i + 2], out ulong lid))
                    {
                        lobbyId = new CSteamID(lid);
                        return lobbyId.IsValid();
                    }
                }
            }
            if (ulong.TryParse(raw, out ulong id))
            {
                lobbyId = new CSteamID(id);
                return lobbyId.IsValid();
            }
            return false;
        }

        public void Shutdown(bool leaveLobby = true)
        {
            ShutdownInternal(leaveLobby);
        }

        private void ShutdownInternal(bool leaveLobby)
        {
            if (_active)
            {
                try { _owner.CloseAllSteamSessions(); }
                catch { /* tear */ }
            }

            CloseAllConnections();

            if (_listenSocket != HSteamListenSocket.Invalid)
            {
                try { SteamNetworkingSockets.CloseListenSocket(_listenSocket); }
                catch { /* tear */ }
                _listenSocket = HSteamListenSocket.Invalid;
            }
            if (_pollGroup != HSteamNetPollGroup.Invalid)
            {
                try { SteamNetworkingSockets.DestroyPollGroup(_pollGroup); }
                catch { /* tear */ }
                _pollGroup = HSteamNetPollGroup.Invalid;
            }

            if (leaveLobby)
            {
                if (_lobbyId.IsValid())
                {
                    try { SteamMatchmaking.LeaveLobby(_lobbyId); }
                    catch { /* tear */ }
                }
                _lobbyId = CSteamID.Nil;
                _migrationAllow.Clear();
                _migrationAllowUntilUtc = DateTime.MinValue;
            }

            _hostSteamId = CSteamID.Nil;
            _serverConn = HSteamNetConnection.Invalid;
            _active = false;
            _hosting = false;
            _clientTransportReady = false;
            _clientConnectStartedUtc = DateTime.MinValue;
            _reliableOutbox.Clear();
            _pendingHostConnects.Clear();
        }
    }
}
