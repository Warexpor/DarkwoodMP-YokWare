using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using LiteNetLib.Utils;
using DarkwoodMP.Packets;

namespace DarkwoodMP.Network;

/// <summary>
/// UDP network layer. Runs in two modes:
///  - Client: connects to a dedicated server or an in-game host.
///  - Host: acts as a lightweight server (handshake, ID assignment,
///    packet relay between peers, heartbeat and timeout handling).
///
/// v0.6: SendReliable is a real ack/resend layer (it used to be an alias
/// for Send). Reliable packets travel inside a sequenced envelope (0xE0);
/// the receiving hop acks (0xE1), dedupes and unwraps. Reliability is
/// hop-by-hop: client-&gt;host and host-&gt;client each run their own sequence
/// stream, and the host re-originates relayed reliable packets to every
/// other peer with its own per-peer stream. Delivery is at-least-once on
/// the wire and exactly-once to the game (dedupe window). ORDER is not
/// guaranteed - every sync channel is idempotent / absolute-state /
/// forward-only by design, which is what makes this lightweight layer
/// sufficient.
/// </summary>
public class NetworkLayer : DarkwoodMP.Packets.ITransport
{
    /// <summary>Set true to log every datagram (very noisy - debugging only).</summary>
    public static bool VerboseLogging = false;

    // --- Reliability tuning (LiteNetLib 1.3.5 Utils only; custom hop reliability) ---
    private static readonly TimeSpan ResendInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan CriticalResendInterval = TimeSpan.FromMilliseconds(200);
    private const int MaxResendAttempts = 20;
    /// <summary>Critical packets (world transfer, backups, chapter) never give up — only peer drop clears them.</summary>
    private const int CriticalMaxResendAttempts = int.MaxValue / 4;
    private const uint RecvWindow = 2048;
    private const byte EnvelopeByte = (byte)PacketType.ReliableEnvelope;
    private const byte AckByte = (byte)PacketType.ReliableAck;

    private class PendingSend
    {
        public byte[] Data = Array.Empty<byte>();
        public byte InnerType;
        public DateTime FirstSend;
        public DateTime LastSend;
        public int Attempts;
        public bool Critical;
    }

    /// <summary>One reliable hop (us -&gt; one remote endpoint).</summary>
    private class ReliableChannel
    {
        /// <summary>null = the server (client mode), else a peer endpoint (host mode).</summary>
        public IPEndPoint? Target;
        public uint NextSeq;
        public readonly Dictionary<uint, PendingSend> Pending = new();
        public uint RecvFloor;                            // every seq <= floor was delivered
        public readonly HashSet<uint> RecvAbove = new();  // delivered seqs above the floor

        public void Reset()
        {
            NextSeq = 0;
            Pending.Clear();
            RecvFloor = 0;
            RecvAbove.Clear();
        }
    }

    private class PeerInfo
    {
        public int Id;
        public string Name = "Player";
        public DateTime LastActivity = DateTime.UtcNow;
        public readonly ReliableChannel Channel = new();
    }

    private UdpClient? _client;
    private UdpClient? _server;
    private bool _isHost;
    private int _localClientId = -1;
    private string _password = "";
    private string _playerName = "Player";
    private readonly Dictionary<IPEndPoint, PeerInfo> _peers = new();
    private int _nextPeerId = 1;
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime _lastHeartbeat = DateTime.UtcNow;
    private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _peerTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _serverTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _connectRetryInterval = TimeSpan.FromSeconds(1);
    private bool _pendingConnect;
    private bool _connected;
    private IPEndPoint? _serverEndpoint;

    // Client-mode reliability state (host mode: per peer in PeerInfo)
    private ReliableChannel? _serverChannel;
    private byte[]? _connectRequestBytes;
    private DateTime _lastConnectAttempt;

    // Reliability statistics ([Reliable] report once a minute when non-trivial)
    private long _relSent, _relAcked, _relResent, _relDup, _relDropped;
    private DateTime _lastRelReport = DateTime.UtcNow;

    public bool IsConnected => (_client != null && _connected) || (_server != null);
    /// <summary>Client handshake in flight (retries for up to 10s before giving up).</summary>
    public bool IsConnecting => _pendingConnect && _client != null;
    public bool IsHost => _isHost;
    public int LocalClientId => _localClientId;
    public int PeerCount => _peers.Count;

    public event Action<byte[]>? OnDataReceived;
    public event Action<int, string, bool, string>? OnConnectResponse;
    public event Action<int, string>? OnPlayerJoined;
    public event Action<int, string>? OnPlayerLeft;
    public event Action<PlayerListPacket>? OnPlayerList;
    public event Action<HeartbeatPacket>? OnHeartbeat;
    public event Action<HeartbeatAckPacket>? OnHeartbeatAck;
    public Action<int, string>? OnHostStartedCallback;
    public event Action? OnDisconnected;

    /// <summary>Supplies the current game state when a client requests it from an in-game host.</summary>
    public Func<GameStateSyncPacket>? GameStateProvider;

    public void StartHost(int port, string password = "", string playerName = "Host")
    {
        Disconnect();
        _isHost = true;
        _password = password;
        _playerName = playerName;
        _localClientId = 0;
        try
        {
            // Socket options must be set BEFORE binding - UdpClient(port) binds
            // immediately and setting ExclusiveAddressUse afterwards throws on Mono
            _server = new UdpClient();
            _server.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _server.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[NetworkLayer] Could not bind UDP port {port}: {ex.Message}");
            _server?.Close();
            _server = null;
            _isHost = false;
            _localClientId = -1;
            return;
        }
        ModLogger.Msg($"[NetworkLayer] Host started on port {port}");
        OnHostStartedCallback?.Invoke(0, "Host");
    }

    public void ConnectToServer(string ip, int port, string password = "", string playerName = "Player")
    {
        Disconnect();
        _isHost = false;
        _password = password;
        _playerName = playerName;

        IPAddress? address;
        if (!IPAddress.TryParse(ip, out address))
        {
            try
            {
                address = Dns.GetHostAddresses(ip)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[NetworkLayer] DNS lookup for '{ip}' failed: {ex.Message}");
                return;
            }
            if (address == null)
            {
                ModLogger.Error($"[NetworkLayer] Could not resolve '{ip}' to an IPv4 address");
                return;
            }
        }

        _client = new UdpClient();
        _serverEndpoint = new IPEndPoint(address, port);
        _serverChannel = new ReliableChannel(); // Target = null -> server
        _client.Connect(_serverEndpoint);
        ModLogger.Msg($"[NetworkLayer] Connecting to {_serverEndpoint} as '{playerName}'...");

        try
        {
            var writer = new NetDataWriter();
            var connectReq = new ConnectRequestPacket
            {
                Name = playerName,
                Password = password,
                Version = "0.9",
                IronbarkVersion = Ironbark.Version
            };
            writer.Put(connectReq.MessageId);
            connectReq.Serialize(writer);

            // Keep the request bytes: while the handshake is pending it is
            // re-sent every second (a single lost datagram used to mean a
            // guaranteed 10s timeout - the host dedupes retries anyway)
            _connectRequestBytes = new byte[writer.Length];
            Buffer.BlockCopy(writer.Data, 0, _connectRequestBytes, 0, writer.Length);

            _client.Client.SendTo(writer.Data, writer.Length, SocketFlags.None, _serverEndpoint);
            _pendingConnect = true;
            _lastActivity = DateTime.UtcNow;
            _lastConnectAttempt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[NetworkLayer] Failed to send connect request: {ex.Message}");
            _client.Close();
            _client = null;
            _serverEndpoint = null;
            _serverChannel = null;
        }
    }

    public void Update()
    {
        // Connect retry + timeout
        if (_pendingConnect && _client != null)
        {
            var now = DateTime.UtcNow;
            if (_connectRequestBytes != null && (now - _lastConnectAttempt) >= _connectRetryInterval)
            {
                _lastConnectAttempt = now;
                try
                {
                    _client.Client.SendTo(_connectRequestBytes, _connectRequestBytes.Length,
                        SocketFlags.None, _serverEndpoint);
                }
                catch { }
            }

            if ((now - _lastActivity) > _connectTimeout)
            {
                _pendingConnect = false;
                ModLogger.Warning("[NetworkLayer] Connect timeout (10s) - server not responding");
                ModLogger.Warning("[NetworkLayer] Check: server running? correct IP/port? UDP allowed through firewall?");
                _client.Close();
                _client = null;
                _serverEndpoint = null;
                _serverChannel = null;
                OnDisconnected?.Invoke();
            }
        }

        // Server silence detection after successful connect
        if (_connected && _client != null && (DateTime.UtcNow - _lastActivity) > _serverTimeout)
        {
            ModLogger.Warning("[NetworkLayer] Server stopped responding - disconnecting");
            _connected = false;
            OnDisconnected?.Invoke();
            return;
        }

        DrainSocket(_client);
        DrainSocket(_server);

        ReliableTick();

        if (_server != null)
            HostTick();
    }

    private void DrainSocket(UdpClient? socket)
    {
        if (socket == null) return;
        try
        {
            var remoteEP = new IPEndPoint(IPAddress.Any, 0);
            // Socket.Poll works on Windows Mono where Available always returns 0
            while (socket.Client != null && socket.Client.Poll(0, SelectMode.SelectRead))
            {
                byte[] data = socket.Receive(ref remoteEP);
                if (VerboseLogging)
                    ModLogger.Msg($"[NetworkLayer] Recv {data.Length} bytes from {remoteEP}");
                OnDatagramReceived(remoteEP, data);
            }
        }
        catch (SocketException ex)
        {
            // ConnectionReset = ICMP port unreachable from a previous send; harmless for UDP
            if (ex.SocketErrorCode != SocketError.ConnectionReset)
                ModLogger.Error($"[NetworkLayer] Recv error: {ex.SocketErrorCode}");
        }
        catch (ObjectDisposedException) { }
    }

    private void HostTick()
    {
        var now = DateTime.UtcNow;

        // Periodic heartbeat so clients know the host is alive
        if (now - _lastHeartbeat >= _heartbeatInterval)
        {
            _lastHeartbeat = now;
            var hb = new HeartbeatPacket
            {
                Timestamp = now.ToBinary(),
                PlayerCount = _peers.Count + 1
            };
            var writer = new NetDataWriter();
            writer.Put(hb.MessageId);
            hb.Serialize(writer);
            BroadcastRaw(writer.Data, writer.Length, null);
        }

        // Drop peers that went silent
        List<IPEndPoint>? stale = null;
        foreach (var kvp in _peers)
        {
            if (now - kvp.Value.LastActivity > _peerTimeout)
                (stale ??= new List<IPEndPoint>()).Add(kvp.Key);
        }
        if (stale != null)
        {
            foreach (var ep in stale)
            {
                var peer = _peers[ep];
                _peers.Remove(ep);
                ModLogger.Msg($"[NetworkLayer] Peer '{peer.Name}' (ID {peer.Id}) timed out");
                BroadcastPacketReliable(new PlayerLeftPacket { PlayerId = peer.Id, PlayerName = peer.Name }, null);
                OnPlayerLeft?.Invoke(peer.Id, peer.Name);
            }
        }
    }

    // ------------------------------------------------------------------
    // Reliability framing
    // ------------------------------------------------------------------

    private static uint DecodeSeq(byte[] data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static void EncodeSeq(byte[] data, int offset, uint seq)
    {
        data[offset] = (byte)seq;
        data[offset + 1] = (byte)(seq >> 8);
        data[offset + 2] = (byte)(seq >> 16);
        data[offset + 3] = (byte)(seq >> 24);
    }

    /// <summary>The reliable channel for datagrams arriving from this endpoint.</summary>
    private ReliableChannel? ChannelFor(IPEndPoint remoteEP)
    {
        if (_isHost)
            return _peers.TryGetValue(remoteEP, out var peer) ? peer.Channel : null;
        // Client socket is connected - only server datagrams arrive here
        return _serverChannel ??= new ReliableChannel();
    }

    private void TouchActivity(IPEndPoint remoteEP)
    {
        if (_isHost)
        {
            if (_peers.TryGetValue(remoteEP, out var peer))
                peer.LastActivity = DateTime.UtcNow;
        }
        else
        {
            _lastActivity = DateTime.UtcNow;
        }
    }

    private void SendRaw(IPEndPoint? target, byte[] data, int length)
    {
        try
        {
            if (target == null)
            {
                if (_client != null && _serverEndpoint != null)
                    _client.Client.SendTo(data, length, SocketFlags.None, _serverEndpoint);
            }
            else if (_server != null)
            {
                _server.Send(data, length, target);
            }
        }
        catch (Exception ex)
        {
            if (VerboseLogging)
                ModLogger.Error($"[Reliable] Raw send failed: {ex.Message}");
        }
    }

    /// <summary>Wrap inner datagram bytes in an envelope, remember it for resends, send it.</summary>
    private void SendEnvelope(ReliableChannel channel, byte[] inner, int innerLength, bool critical = false)
    {
        var seq = ++channel.NextSeq;
        var buf = new byte[innerLength + 5];
        buf[0] = EnvelopeByte;
        EncodeSeq(buf, 1, seq);
        Buffer.BlockCopy(inner, 0, buf, 5, innerLength);

        var now = DateTime.UtcNow;
        // Critical if flagged or Ironbark registry
        if (!critical && innerLength > 0)
            critical = IsCriticalInner(inner, innerLength);

        channel.Pending[seq] = new PendingSend
        {
            Data = buf,
            InnerType = innerLength > 0 ? inner[0] : (byte)0,
            FirstSend = now,
            LastSend = now,
            Attempts = 1,
            Critical = critical
        };
        _relSent++;
        SendRaw(channel.Target, buf, buf.Length);
    }

    private static bool ShouldSendCritical(Packet packet)
    {
        if (packet == null) return false;
        return IronbarkRegistry.IsCritical(packet.MessageId);
    }

    /// <summary>Critical from Ironbark registry (v2 MessageId u16 LE at start of inner).</summary>
    private static bool IsCriticalInner(byte[] inner, int length)
    {
        if (inner == null || length < 2) return false;
        ushort msgId = (ushort)(inner[0] | (inner[1] << 8));
        return IronbarkRegistry.IsCritical(msgId);
    }

    /// <summary>
    /// Mark a sequence as delivered. Returns false for duplicates. A seq far
    /// above the window forces the floor forward - the sender gives up on a
    /// packet after MaxResendAttempts, so a permanently missing seq must not
    /// pin the window (state still converges via the pending/snapshot paths).
    /// </summary>
    private static bool MarkDelivered(ReliableChannel channel, uint seq)
    {
        if (seq <= channel.RecvFloor || channel.RecvAbove.Contains(seq)) return false;

        if (seq == channel.RecvFloor + 1)
        {
            channel.RecvFloor = seq;
            while (channel.RecvAbove.Remove(channel.RecvFloor + 1)) channel.RecvFloor++;
        }
        else
        {
            channel.RecvAbove.Add(seq);
            if (seq > channel.RecvFloor + RecvWindow)
            {
                channel.RecvFloor = seq - RecvWindow / 2;
                channel.RecvAbove.RemoveWhere(s => s <= channel.RecvFloor);
                while (channel.RecvAbove.Remove(channel.RecvFloor + 1)) channel.RecvFloor++;
            }
        }
        return true;
    }

    private void ReliableTick()
    {
        var now = DateTime.UtcNow;
        if (_serverChannel != null && _client != null)
            ResendChannel(_serverChannel, now);
        if (_server != null)
        {
            foreach (var peer in _peers.Values)
                ResendChannel(peer.Channel, now);
        }

        if ((now - _lastRelReport) > TimeSpan.FromSeconds(60))
        {
            _lastRelReport = now;
            if (_relResent > 0 || _relDropped > 0 || VerboseLogging)
                ModLogger.Msg("Reliable", $"sent={_relSent} acked={_relAcked} resent={_relResent} dup={_relDup} dropped={_relDropped}");
        }
    }

    private void ResendChannel(ReliableChannel channel, DateTime now)
    {
        if (channel.Pending.Count == 0) return;

        List<uint>? dead = null;
        foreach (var kvp in channel.Pending)
        {
            var p = kvp.Value;
            var interval = p.Critical ? CriticalResendInterval : ResendInterval;
            if (now - p.LastSend < interval) continue;

            // Critical: never give up (only peer disconnect clears Pending).
            // Non-critical: max attempts or wall-clock give-up.
            if (!p.Critical
                && (p.Attempts >= MaxResendAttempts || now - p.FirstSend > GiveUpAfter))
            {
                (dead ??= new List<uint>()).Add(kvp.Key);
                continue;
            }
            if (p.Critical && p.Attempts >= CriticalMaxResendAttempts)
            {
                (dead ??= new List<uint>()).Add(kvp.Key);
                continue;
            }

            p.LastSend = now;
            p.Attempts++;
            _relResent++;
            SendRaw(channel.Target, p.Data, p.Data.Length);
            if (p.Critical && p.Attempts % 50 == 0)
                ModLogger.Warning("Reliable", $"waiting ack seq={kvp.Key} type=0x{p.InnerType:X2} attempts={p.Attempts} (critical)");
        }

        if (dead != null)
        {
            foreach (var seq in dead)
            {
                var p = channel.Pending[seq];
                channel.Pending.Remove(seq);
                _relDropped++;
                ModLogger.Warning("Reliable", $"gave up seq={seq} type=0x{p.InnerType:X2} after {p.Attempts} attempts critical={p.Critical}");
            }
        }
    }

    // ------------------------------------------------------------------
    // Receive path
    // ------------------------------------------------------------------

    private void OnDatagramReceived(IPEndPoint remoteEP, byte[] data)
    {
        if (data.Length < 1) return;

        // Reliability framing is handled before normal packet dispatch
        if (data[0] == AckByte)
        {
            if (data.Length < 5) return;
            var channel = ChannelFor(remoteEP);
            if (channel != null && channel.Pending.Remove(DecodeSeq(data, 1)))
                _relAcked++;
            TouchActivity(remoteEP);
            return;
        }

        if (data[0] == EnvelopeByte)
        {
            if (data.Length < 6) return;
            var channel = ChannelFor(remoteEP);
            if (channel == null) return; // envelope from an endpoint that never completed the handshake
            TouchActivity(remoteEP);

            var seq = DecodeSeq(data, 1);

            // Always ack, even duplicates - the previous ack may have been lost
            var ack = new byte[5];
            ack[0] = AckByte;
            EncodeSeq(ack, 1, seq);
            SendRaw(channel.Target, ack, 5);

            if (!MarkDelivered(channel, seq))
            {
                _relDup++;
                return;
            }

            var inner = new byte[data.Length - 5];
            Buffer.BlockCopy(data, 5, inner, 0, inner.Length);
            HandleDatagram(remoteEP, inner, reliable: true);
            return;
        }

        HandleDatagram(remoteEP, data, reliable: false);
    }

    private void HandleDatagram(IPEndPoint remoteEP, byte[] data, bool reliable)
    {
        if (data.Length < 1) return;

        var reader = new NetDataReader(data);
        // Ironbark v2: MessageId u16 LE (handshake + game). Framing 0xE0/E1 handled above.
        if (data.Length < 2) return;
        var messageId = reader.GetUShort();
        var packetType = (PacketType)messageId;

        if (_isHost)
        {
            HandleHostDatagram(remoteEP, packetType, messageId, reader, data, reliable);
            return;
        }

        // --- Client mode ---
        _lastActivity = DateTime.UtcNow;

        switch (packetType)
        {
            case PacketType.ConnectResponse:
                {
                    var pkt = new ConnectResponsePacket();
                    pkt.Deserialize(reader);
                    _localClientId = pkt.ClientId;
                    _pendingConnect = false;
                    _connected = pkt.Accepted;
                    OnConnectResponse?.Invoke(_localClientId, pkt.ClientId.ToString(), pkt.Accepted, pkt.Message);
                    break;
                }
            case PacketType.PlayerJoined:
                {
                    var pkt = new PlayerJoinedPacket();
                    pkt.Deserialize(reader);
                    OnPlayerJoined?.Invoke(pkt.PlayerId, pkt.PlayerName);
                    break;
                }
            case PacketType.PlayerLeft:
                {
                    var pkt = new PlayerLeftPacket();
                    pkt.Deserialize(reader);
                    OnPlayerLeft?.Invoke(pkt.PlayerId, pkt.PlayerName);
                    break;
                }
            case PacketType.PlayerList:
                {
                    var pkt = new PlayerListPacket();
                    pkt.Deserialize(reader);
                    OnPlayerList?.Invoke(pkt);
                    break;
                }
            case PacketType.Heartbeat:
                {
                    var pkt = new HeartbeatPacket();
                    pkt.Deserialize(reader);
                    OnHeartbeat?.Invoke(pkt);
                    break;
                }
            case PacketType.HeartbeatAck:
                {
                    var pkt = new HeartbeatAckPacket();
                    pkt.Deserialize(reader);
                    OnHeartbeatAck?.Invoke(pkt);
                    break;
                }
            default:
                // All game-state packets go through the PacketReceiver
                OnDataReceived?.Invoke(data);
                break;
        }
    }

    private void HandleHostDatagram(IPEndPoint remoteEP, PacketType packetType, ushort messageId, NetDataReader reader, byte[] data, bool reliable)
    {
        if (packetType == PacketType.ConnectRequest)
        {
            HandleConnectRequest(remoteEP, reader);
            return;
        }

        // Ignore traffic from endpoints that never completed the handshake
        if (!_peers.TryGetValue(remoteEP, out var peer)) return;
        peer.LastActivity = DateTime.UtcNow;

        switch (packetType)
        {
            case PacketType.HeartbeatAck:
                break; // activity timestamp already refreshed

            case PacketType.PlayerLeft:
                {
                    var pkt = new PlayerLeftPacket();
                    pkt.Deserialize(reader);
                    _peers.Remove(remoteEP);
                    ModLogger.Msg($"[NetworkLayer] Peer '{peer.Name}' (ID {peer.Id}) left");
                    BroadcastPacketReliable(new PlayerLeftPacket { PlayerId = peer.Id, PlayerName = peer.Name }, null);
                    OnPlayerLeft?.Invoke(peer.Id, peer.Name);
                    break;
                }

            case PacketType.GameStateRequest:
                {
                    var state = GameStateProvider?.Invoke();
                    if (state != null)
                        SendToReliable(remoteEP, state);
                    break;
                }

            case PacketType.ChatMessage:
                // Chat is echoed to everyone including the sender
                if (reliable) RelayReliable(data, data.Length, null);
                else BroadcastRaw(data, data.Length, null);
                OnDataReceived?.Invoke(data);
                break;

            default:
                // Ironbark v2: host fan-out from registry only.
                if (IronbarkRegistry.ShouldFanOut(messageId))
                {
                    if (reliable) RelayReliable(data, data.Length, remoteEP);
                    else BroadcastRaw(data, data.Length, remoteEP);
                }
                OnDataReceived?.Invoke(data);
                break;
        }
    }

    private void HandleConnectRequest(IPEndPoint remoteEP, NetDataReader reader)
    {
        var req = new ConnectRequestPacket();
        try { req.Deserialize(reader); }
        catch { return; }

        // Ironbark hard reject — wrong / missing protocol version never enters session
        if (req.IronbarkVersion != Ironbark.Version)
        {
            var msg = $"{Ironbark.Abbrev} mismatch: need {Ironbark.Version}, peer sent {req.IronbarkVersion}";
            ModLogger.Warning($"[Ironbark] Rejected '{req.Name}' from {remoteEP}: {msg}");
            SendTo(remoteEP, new ConnectResponsePacket
            {
                Accepted = false,
                Message = msg,
                IronbarkVersion = Ironbark.Version,
                Capabilities = Ironbark.Caps.Local
            });
            return;
        }

        // Duplicate request: either a handshake retry (response was lost) or a
        // RECONNECT from the same endpoint (fresh mod instance, sequence
        // numbers restart at 1). Reset the reliable channel and resend the
        // full welcome - all sync channels tolerate the replay (idempotent).
        if (_peers.TryGetValue(remoteEP, out var existing))
        {
            existing.Channel.Reset();
            existing.LastActivity = DateTime.UtcNow;
            SendWelcome(remoteEP, existing);
            return;
        }

        if (!string.IsNullOrEmpty(_password) && req.Password != _password)
        {
            ModLogger.Msg($"[NetworkLayer] Rejected '{req.Name}' from {remoteEP}: wrong password");
            SendTo(remoteEP, new ConnectResponsePacket
            {
                Accepted = false,
                Message = "Wrong password",
                IronbarkVersion = Ironbark.Version,
                Capabilities = Ironbark.Caps.Local
            });
            return;
        }

        var peer = new PeerInfo { Id = _nextPeerId++, Name = req.Name };
        peer.Channel.Target = remoteEP;
        _peers[remoteEP] = peer;
        ModLogger.Msg($"[Ironbark] Peer '{peer.Name}' connected as ID {peer.Id} ({remoteEP}) {Ironbark.Banner}");

        SendWelcome(remoteEP, peer);

        // Tell everyone else about the new player
        var joined = new PlayerJoinedPacket { PlayerId = peer.Id, PlayerName = peer.Name };
        BroadcastPacketReliable(joined, remoteEP);
        OnPlayerJoined?.Invoke(peer.Id, peer.Name);
    }

    /// <summary>Connect response + player list + game state for a (re)joining peer.</summary>
    private void SendWelcome(IPEndPoint remoteEP, PeerInfo peer)
    {
        // The response itself stays unreliable: the client re-sends its
        // ConnectRequest every second until a response arrives
        SendTo(remoteEP, new ConnectResponsePacket
        {
            ClientId = peer.Id,
            Accepted = true,
            Message = "Connected",
            IronbarkVersion = Ironbark.Version,
            Capabilities = Ironbark.Caps.Local
        });

        // Full player list (host + all peers)
        var players = new List<PlayerListPacket.PlayerInfo>
        {
            new PlayerListPacket.PlayerInfo(0, _playerName)
        };
        foreach (var p in _peers.Values)
            players.Add(new PlayerListPacket.PlayerInfo(p.Id, p.Name));
        SendToReliable(remoteEP, new PlayerListPacket { Players = players.ToArray() });

        // Push current game state right away
        var state = GameStateProvider?.Invoke();
        if (state != null)
            SendToReliable(remoteEP, state);
    }

    // ------------------------------------------------------------------
    // Send API
    // ------------------------------------------------------------------

    /// <summary>
    /// Guaranteed delivery (ack/resend + dedupe), NO ordering guarantee.
    /// Client mode: to the server. Host mode: to every peer.
    /// </summary>
    public void SendReliable<T>(T packet) where T : Packet
    {
        Broadcast(packet, reliable: true);
    }

    /// <summary>
    /// Never-give-up reliable send (world transfer, backups, chapter, trade stock).
    /// Resends until ack or peer disconnect; auto-critical for known packet/action types.
    /// </summary>
    public void SendReliableCritical<T>(T packet) where T : Packet
    {
        Broadcast(packet, reliable: true, critical: true);
    }

    public void Send<T>(T packet) where T : Packet
    {
        Broadcast(packet, reliable: false);
    }

    // ITransport (Ironbark v2)
    void ITransport.Broadcast(Packet packet, TransportDelivery delivery)
    {
        if (delivery == TransportDelivery.Unreliable)
            Broadcast(packet, reliable: false);
        else
            Broadcast(packet, reliable: true, critical: delivery == TransportDelivery.Critical);
    }

    void ITransport.SendToPlayer(int playerId, Packet packet, TransportDelivery delivery)
    {
        SendToPlayer(playerId, packet, reliable: delivery != TransportDelivery.Unreliable);
    }

    /// <summary>
    /// Horde-style Broadcast: host → all peers; client → server only.
    /// Prefer this over inventing first-peer-only sends.
    /// </summary>
    public void Broadcast(Packet packet, bool reliable = true, bool critical = false)
    {
        if (packet == null) return;
        var writer = WritePacket(packet);
        if (reliable && !critical)
            critical = ShouldSendCritical(packet);

        if (_client != null && _serverEndpoint != null)
        {
            if (reliable && _serverChannel != null)
            {
                if (VerboseLogging)
                    ModLogger.Msg($"[NetworkLayer] Broadcast(reliable{(critical ? ",critical" : "")}) {writer.Length} bytes type=0x{(byte)packet.Type:X2}");
                SendEnvelope(_serverChannel, writer.Data, writer.Length, critical);
            }
            else
            {
                try
                {
                    if (VerboseLogging)
                        ModLogger.Msg($"[NetworkLayer] Broadcast {writer.Length} bytes type=0x{(byte)packet.Type:X2}");
                    _client.Client.SendTo(writer.Data, writer.Length, SocketFlags.None, _serverEndpoint);
                }
                catch (Exception ex) { ModLogger.Error($"[NetworkLayer] Broadcast failed: {ex.Message}"); }
            }
            return;
        }

        if (_server != null)
        {
            if (reliable)
            {
                foreach (var peer in _peers.Values)
                    SendEnvelope(peer.Channel, writer.Data, writer.Length, critical);
            }
            else
            {
                BroadcastRaw(writer.Data, writer.Length, null);
            }
        }
    }

    /// <summary>
    /// Targeted delivery by player id. Host: one peer. Client: only hop is the
    /// server (packet still carries target semantics for the host to apply).
    /// </summary>
    public void SendToPlayer(int playerId, Packet packet, bool reliable = true)
    {
        if (packet == null) return;

        if (!_isHost)
        {
            // Client has a single uplink; host routes / applies by payload PlayerId.
            Broadcast(packet, reliable);
            return;
        }

        if (playerId == _localClientId)
            return;

        var ep = FindPeerEndpoint(playerId);
        if (ep == null)
        {
            ModLogger.Warning($"[NetworkLayer] SendToPlayer: no peer for id {playerId}");
            return;
        }

        if (reliable)
            SendToReliable(ep, packet);
        else
            SendTo(ep, packet);
    }

    /// <summary>
    /// Host rebroadcast to every peer except one (3+ fan-out). Client: same as Broadcast.
    /// </summary>
    public void SendToAllExcept(int excludePlayerId, Packet packet, bool reliable = true)
    {
        if (packet == null) return;

        if (!_isHost)
        {
            Broadcast(packet, reliable);
            return;
        }

        var writer = WritePacket(packet);
        foreach (var kvp in _peers)
        {
            if (kvp.Value.Id == excludePlayerId) continue;
            if (reliable)
                SendEnvelope(kvp.Value.Channel, writer.Data, writer.Length);
            else
            {
                try { _server?.Send(writer.Data, writer.Length, kvp.Key); }
                catch { /* peer gone */ }
            }
        }
    }

    /// <summary>Look up peer endpoint by assigned player id (host only).</summary>
    public IPEndPoint? FindPeerEndpoint(int playerId)
    {
        foreach (var kvp in _peers)
        {
            if (kvp.Value.Id == playerId)
                return kvp.Key;
        }
        return null;
    }

    private static NetDataWriter WritePacket(Packet packet)
    {
        // Ironbark v2: MessageId u16 LE + payload
        var writer = new NetDataWriter();
        writer.Put(packet.MessageId);
        packet.Serialize(writer);
        return writer;
    }

    private void SendTo(IPEndPoint endpoint, Packet packet)
    {
        if (_server == null) return;
        var writer = WritePacket(packet);
        try { _server.Send(writer.Data, writer.Length, endpoint); }
        catch (Exception ex) { ModLogger.Error($"[NetworkLayer] SendTo {endpoint} failed: {ex.Message}"); }
    }

    /// <summary>Host: reliable send to one specific peer.</summary>
    private void SendToReliable(IPEndPoint endpoint, Packet packet)
    {
        if (_server == null) return;
        if (!_peers.TryGetValue(endpoint, out var peer))
        {
            SendTo(endpoint, packet); // not handshaken (rejects etc.) - best effort
            return;
        }
        var writer = WritePacket(packet);
        SendEnvelope(peer.Channel, writer.Data, writer.Length, ShouldSendCritical(packet));
    }

    /// <summary>Host: reliably re-originate raw inner datagram bytes to all peers except one.</summary>
    private void RelayReliable(byte[] inner, int length, IPEndPoint? except)
    {
        var critical = IsCriticalInner(inner, length);
        foreach (var kvp in _peers)
        {
            if (except != null && kvp.Key.Equals(except)) continue;
            SendEnvelope(kvp.Value.Channel, inner, length, critical);
        }
    }

    private void BroadcastPacket(Packet packet, IPEndPoint? except)
    {
        var writer = WritePacket(packet);
        BroadcastRaw(writer.Data, writer.Length, except);
    }

    private void BroadcastPacketReliable(Packet packet, IPEndPoint? except)
    {
        var writer = WritePacket(packet);
        RelayReliable(writer.Data, writer.Length, except);
    }

    private void BroadcastRaw(byte[] data, int length, IPEndPoint? except)
    {
        if (_server == null) return;
        foreach (var ep in _peers.Keys)
        {
            if (except != null && ep.Equals(except)) continue;
            try { _server.Send(data, length, ep); }
            catch { }
        }
    }

    public void Disconnect()
    {
        // Notify server we're leaving (only if connected to an external server).
        // Best effort: the socket closes right after, so no resends happen.
        if (_client != null && _localClientId >= 0 && _connected)
        {
            try
            {
                SendReliable(new PlayerLeftPacket { PlayerId = _localClientId, PlayerName = _playerName });
            }
            catch { }
        }

        // Tell peers the host is shutting down
        if (_server != null && _peers.Count > 0)
        {
            BroadcastPacket(new PlayerLeftPacket { PlayerId = 0, PlayerName = _playerName }, null);
        }

        _client?.Close();
        _server?.Close();
        _client = null;
        _server = null;
        _serverEndpoint = null;
        _serverChannel = null;
        _connectRequestBytes = null;
        _isHost = false;
        _localClientId = -1;
        _pendingConnect = false;
        _connected = false;
        _peers.Clear();
        _nextPeerId = 1;
        _relSent = _relAcked = _relResent = _relDup = _relDropped = 0;
    }
}
