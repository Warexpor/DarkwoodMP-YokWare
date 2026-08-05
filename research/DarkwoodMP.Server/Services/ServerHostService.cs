using System.Net;
using System.Net.Sockets;
using DarkwoodMP.Packets;
using DarkwoodMP.Server.Config;
using DarkwoodMP.Server.Models;
using LiteNetLib.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DarkwoodMP.Server.Services;

public class ServerHostService : BackgroundService
{
    private readonly ILogger<ServerHostService> _logger;
    private readonly ServerConfig _config;
    private readonly ConnectionService _connectionService;
    private readonly WorldService _worldService;
    private readonly SaveService _saveService;
    private readonly PacketRegistryService _packetRegistry;
    private readonly WorldCacheService _worldCache;
    private readonly WorldState _worldState;
    private UdpClient? _udpServer;
    private readonly object _broadcastLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private long _packetsReceived;

    private readonly TimeSpan _disconnectTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastHeartbeat = DateTime.UtcNow;

    public ServerHostService(
        ILogger<ServerHostService> logger,
        IOptions<ServerConfig> config,
        ConnectionService connectionService,
        WorldService worldService,
        SaveService saveService,
        PacketRegistryService packetRegistry,
        WorldCacheService worldCache)
    {
        _logger = logger;
        _config = config.Value;
        _connectionService = connectionService;
        _worldService = worldService;
        _saveService = saveService;
        _packetRegistry = packetRegistry;
        _worldCache = worldCache;
        _worldCache.SendReliable = SendToEndpointReliable;
        // The elected simulation authority (mirrors the mod's IsTimeAuthority:
        // lowest-id connected player) may push canonical-save refreshes.
        _worldCache.IsAuthorityEndpoint = endpoint =>
        {
            var sender = _connectionService.GetByEndpoint(endpoint);
            if (sender == null) return false;
            foreach (var p in _connectionService.Players.Values)
                if (p.Id < sender.Id) return false;
            return true;
        };
        _worldState = WorldState.LoadFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "world.json"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting DarkwoodMP server on port {Port} ({Mode})",
            _config.Port, _config.AuthoritativeWorld ? "authoritative-world" : "reliable-relay");

        try
        {
            _udpServer = new UdpClient();
            _udpServer.Client.ExclusiveAddressUse = false;
            _udpServer.Client.Bind(new IPEndPoint(IPAddress.Any, _config.Port));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdownCts.Token);

            var receiveTask = ReceiveLoop(cts.Token);
            var tickTimer = new System.Timers.Timer(1000.0 / _config.TickRate);
            tickTimer.AutoReset = true;
            tickTimer.Elapsed += (_, _) => ProcessTick();
            tickTimer.Start();

            await receiveTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Server stopping gracefully");
        }
        finally
        {
            _saveService.Save(_worldState);
            _udpServer?.Close();
            _logger.LogInformation("DarkwoodMP server stopped");
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await _udpServer!.ReceiveAsync(token);
                HandlePacket(result.RemoteEndPoint, result.Buffer);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    private void ProcessTick()
    {
        // Authoritative-world mode only: invent a game clock and re-broadcast
        // accumulated world state. In the DEFAULT reliable-relay mode the clock
        // and day/night come from the elected time-authority client, so the
        // server must NOT run its own (drifting) clock or echo stale state.
        if (_config.AuthoritativeWorld)
        {
            _worldState.TotalPlaytime += 1f / _config.TickRate;
            _worldState.TimeOfDay += 1f / (24f * 3600f * 60f);
            if (_worldState.TimeOfDay >= 1f)
            {
                _worldState.TimeOfDay -= 1f;
                _worldState.DayNumber++;
                _logger.LogInformation("Day {DayNumber} started", _worldState.DayNumber);
            }
            _worldState.IsNight = _worldState.TimeOfDay > 0.5f;
            BroadcastGameStateIfNeeded();

            // Rate-limited world-state repair (redundant with the now-reliable
            // live relays; only meaningful in authoritative mode)
            var syncPackets = _worldService.SyncTick(_worldState, _config.TickRate, _config.EnemySyncRate);
            if (syncPackets.Count > 0)
            {
                var endpoints = AllEndpoints();
                foreach (var pkt in syncPackets)
                {
                    var data = _packetRegistry.Serialize(pkt);
                    foreach (var ep in endpoints)
                        _udpServer?.Send(data, data.Length, ep);
                }
            }
        }

        // Always: world-transfer drain, heartbeat, reliability resend, disconnect, save
        _worldCache.Tick();
        ProcessHeartbeat();
        ReliableResendTick();
        ProcessDisconnects();

        if ((DateTime.UtcNow - _worldState.LastSave).TotalSeconds >= _config.SaveInterval)
        {
            _saveService.Save(_worldState);
        }
    }

    private int _lastBroadcastDay = -1;
    private bool _lastBroadcastNight;

    private void BroadcastGameStateIfNeeded()
    {
        var state = _worldState;

        // Only broadcast on actual transitions, not every tick
        if (state.DayNumber == _lastBroadcastDay && state.IsNight == _lastBroadcastNight)
            return;
        _lastBroadcastDay = state.DayNumber;
        _lastBroadcastNight = state.IsNight;

        var packet = new GameStateSyncPacket
        {
            DayNumber = state.DayNumber,
            IsNight = state.IsNight,
            TimeOfDay = state.TimeOfDay,
            GameTime = state.TimeOfDay * 24f
        };

        // Reliable: a lost transition would leave a client a full phase behind
        foreach (var ep in AllEndpoints())
            SendToEndpointReliable(ep, packet);
    }

    private void ProcessHeartbeat()
    {
        if (DateTime.UtcNow - _lastHeartbeat < _heartbeatInterval) return;
        _lastHeartbeat = DateTime.UtcNow;

        var packet = new HeartbeatPacket
        {
            Timestamp = DateTime.UtcNow.ToBinary(),
            PlayerCount = _connectionService.Players.Count
        };
        Broadcast(packet); // unreliable: the next heartbeat covers a lost one
    }

    private void ProcessDisconnects()
    {
        var now = DateTime.UtcNow;
        var disconnected = new List<int>();

        foreach (var player in _connectionService.Players.Values)
        {
            if (now - player.LastActivity > _disconnectTimeout)
            {
                disconnected.Add(player.Id);
            }
        }

        foreach (var id in disconnected)
        {
            var player = _connectionService.Players[id];
            _connectionService.RemoveByPlayer(player);
            if (player.Endpoint != null) { DropReliableChannels(player.Endpoint); _worldCache.OnEndpointGone(player.Endpoint); }
            BroadcastReliable(new PlayerLeftPacket { PlayerId = player.Id, PlayerName = player.Name });
            _logger.LogInformation("Player '{Name}' (ID {Id}) disconnected (timeout)", player.Name, player.Id);
        }
    }

    // ------------------------------------------------------------------
    // Packet dispatch
    // ------------------------------------------------------------------

    private void HandlePacket(IPEndPoint endpoint, byte[] data, bool reliable = false)
    {
        try
        {
            if (!reliable) _packetsReceived++;
            if (data.Length < 1) return;

            // Outer framing (first byte of raw UDP) — not MessageIds
            if (data[0] == (byte)PacketType.ReliableEnvelope)
            {
                HandleReliableEnvelope(endpoint, data);
                return;
            }
            if (data[0] == (byte)PacketType.ReliableAck)
            {
                HandleReliableAck(endpoint, data);
                return;
            }

            if (data.Length < 2) return;
            var reader = new NetDataReader(data);
            // Ironbark v2: MessageId u16 LE
            var messageId = reader.GetUShort();
            var type = (PacketType)messageId;

            switch (type)
            {
                case PacketType.ConnectRequest:
                    HandleConnectRequest(endpoint, reader);
                    return;
                case PacketType.GameStateRequest:
                    HandleGameStateRequest(endpoint);
                    return;
                case PacketType.HeartbeatAck:
                    HandleHeartbeatAck(endpoint);
                    return;

                case PacketType.PositionUpdate:
                    HandlePositionUpdate(endpoint, reader, reliable);
                    return;
                case PacketType.ChatMessage:
                    HandleChatMessage(endpoint, reader);
                    return;
                case PacketType.HealthUpdate:
                    HandleHealthUpdate(endpoint, reader, data, reliable);
                    return;
                case PacketType.DoorState:
                    HandleDoorState(endpoint, reader, data, reliable);
                    return;
                case PacketType.PickupState:
                    HandlePickupState(endpoint, reader, data, reliable);
                    return;
                case PacketType.EnemyUpdate:
                    // Legacy opcode — mod peers use EntityState (0x28) + EntitySpawn (0x96).
                    // Relay only; do not cache as join-time enemy truth.
                    HandleLegacyEnemyUpdateRelay(endpoint, data, reliable);
                    return;
                case PacketType.EntityState:
                case PacketType.EntitySpawn:
                    // Mod enemy truth: raw relay (no server simulation).
                    HandleIronbarkRelay(endpoint, data, reliable, fanOut: true, type.ToString());
                    return;
                case PacketType.DayNightUpdate:
                    HandleDayNightUpdate(endpoint, reader, data, reliable);
                    return;
                case PacketType.EventTrigger:
                    HandleEventTrigger(endpoint, reader, reliable);
                    return;

                case PacketType.TradeInventory:
                    HandleIronbarkRelay(endpoint, data, reliable, fanOut: true, "TradeInventory");
                    return;
                case PacketType.ClientStateBackupChunk:
                    // Host-bound only — do not fan-out (Ironbark Forward=None)
                    HandleIronbarkRelay(endpoint, data, reliable, fanOut: false, "ClientStateBackupChunk");
                    return;
                case PacketType.SaveBeat:
                    HandleIronbarkRelay(endpoint, data, reliable, fanOut: true, "SaveBeat");
                    return;
                case PacketType.LocationEnter:
                case PacketType.LocationExit:
                case PacketType.MapMarker:
                case PacketType.MapDiscover:
                case PacketType.Reputation:
                case PacketType.ReputationBulk:
                case PacketType.HideoutState:
                case PacketType.JournalSync:
                case PacketType.DreamPrepare:
                case PacketType.DreamStart:
                case PacketType.DreamEntered:
                case PacketType.DreamEnd:
                case PacketType.DreamAudio:
                case PacketType.DreamDoor:
                case PacketType.FinalDreamDeath:
                case PacketType.CutsceneSync:
                case PacketType.ChapterTransition:
                case PacketType.ChapterNotify:
                case PacketType.SceneLoad:
                case PacketType.ExamineState:
                case PacketType.ExamineRequest:
                case PacketType.ContainerState:
                case PacketType.ContainerRequest:
                case PacketType.DeathDropSpawn:
                case PacketType.BarricadeState:
                case PacketType.BuildPlaced:
                case PacketType.BuildConstruct:
                case PacketType.VaultState:
                case PacketType.WorldObjectGone:
                case PacketType.InteractionLockSync:
                    HandleIronbarkRelay(endpoint, data, reliable, fanOut: true, type.ToString());
                    return;
                case PacketType.InventoryUpdate:
                    HandleInventoryUpdate(endpoint, reader, data, reliable);
                    return;
                case PacketType.DamageUpdate:
                    HandleDamageUpdate(endpoint, reader, data, reliable);
                    return;
                case PacketType.InteractiveState:
                    HandleInteractiveState(endpoint, reader, data, reliable);
                    return;
                case PacketType.ObjectMove:
                    HandleObjectMove(endpoint, reader, data, reliable);
                    return;
                case PacketType.PlayerAnim:
                    HandlePlayerAnim(endpoint, reader, data, reliable);
                    return;

                case PacketType.WorldRequest:
                    {
                        if (_connectionService.GetByEndpoint(endpoint) is { } s) s.LastActivity = DateTime.UtcNow;
                        var p = new WorldRequestPacket(); p.Deserialize(reader);
                        _worldCache.HandleRequest(endpoint, p);
                        return;
                    }
                case PacketType.WorldOffer:
                    {
                        if (_connectionService.GetByEndpoint(endpoint) is { } s) s.LastActivity = DateTime.UtcNow;
                        var p = new WorldOfferPacket(); p.Deserialize(reader);
                        _worldCache.HandleOffer(endpoint, p);
                        return;
                    }
                case PacketType.WorldChunk:
                    {
                        if (_connectionService.GetByEndpoint(endpoint) is { } s) s.LastActivity = DateTime.UtcNow;
                        var p = new WorldChunkPacket(); p.Deserialize(reader);
                        _worldCache.HandleChunk(endpoint, p);
                        return;
                    }
                case PacketType.WorldEnd:
                    {
                        var p = new WorldEndPacket(); p.Deserialize(reader);
                        _worldCache.HandleEnd(endpoint, p);
                        return;
                    }

                default:
                    // Relay-by-default: any other valid game packet from a
                    // known peer is forwarded to the others rather than dropped.
                    // This keeps the server protocol-complete as the mod adds
                    // packet types, instead of silently swallowing them.
                    if (_connectionService.GetByEndpoint(endpoint) is { } sender)
                    {
                        sender.LastActivity = DateTime.UtcNow;
                        RelayRawToOthers(endpoint, data, reliable);
                        _logger.LogDebug("[Relay] Forwarded unmodeled packet 0x{Type:X4} ({Len} bytes)", messageId, data.Length);
                    }
                    else
                    {
                        _logger.LogWarning("[HandlePacket] Packet 0x{Type:X4} from unknown endpoint {Endpoint}", messageId, endpoint);
                    }
                    return;
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "[HandlePacket] Failed to handle packet from {Endpoint}", endpoint);

            // Resilience: the server parses modeled packets only to log / track
            // minor world state - its real job is relaying. If our own parse
            // drifts from the mod's wire layout (as HealthUpdate once did), still
            // forward the client's ORIGINAL bytes so peers stay in sync instead
            // of silently losing the packet. Handlers relay only AFTER parsing,
            // so a parse failure here means the relay has not happened yet.
            try
            {
                if (data.Length >= 1 && IsRelayableGameType((PacketType)data[0])
                    && _connectionService.GetByEndpoint(endpoint) is { } sender)
                {
                    sender.LastActivity = DateTime.UtcNow;
                    RelayRawToOthers(endpoint, data, reliable);
                }
            }
            catch { /* best effort - never let the fallback throw */ }
        }
    }

    /// <summary>Game-data packet types the server relays to peers (used as a
    /// parse-failure fallback so format drift can't drop traffic).</summary>
    private static bool IsRelayableGameType(PacketType type)
    {
        // Session / framing — never auto-relay as game data
        switch (type)
        {
            case PacketType.ConnectRequest:
            case PacketType.ConnectResponse:
            case PacketType.ReliableEnvelope:
            case PacketType.ReliableAck:
            case PacketType.Heartbeat:
            case PacketType.HeartbeatAck:
                return false;
            default:
                return true; // Ironbark + legacy game packets
        }
    }

    // ------------------------------------------------------------------
    // Reliability (mod v0.6 envelopes) - receive + send
    // ------------------------------------------------------------------
    // Envelope: [0xE0][seq:uint32 LE][inner datagram]; Ack: [0xE1][seq].
    // The server acks incoming envelopes, dedupes per endpoint, unwraps and
    // processes the inner packet exactly once. Its OWN reliable sends carry a
    // per-endpoint sequence and resend until acked - relayed reliable packets
    // are re-originated reliably, so delivery is guaranteed across the relay
    // hop (previously reliable traffic silently degraded to plain UDP here).

    private sealed class ReliableRecvState
    {
        public uint Floor;
        public readonly HashSet<uint> Above = new();
        public DateTime LastSeen = DateTime.UtcNow;
    }

    private sealed class PendingSend
    {
        public byte[] Data = Array.Empty<byte>();
        public byte InnerType;
        public DateTime FirstSend;
        public DateTime LastSend;
        public int Attempts;
    }

    private sealed class ReliableSendState
    {
        public uint NextSeq;
        public readonly Dictionary<uint, PendingSend> Pending = new();
    }

    private readonly object _reliableLock = new();
    private readonly Dictionary<IPEndPoint, ReliableRecvState> _reliableRecv = new();
    private readonly Dictionary<IPEndPoint, ReliableSendState> _reliableSend = new();
    private const uint ReliableRecvWindow = 2048;
    private static readonly TimeSpan ResendInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(12);
    private const int MaxResendAttempts = 20;

    private static uint DecodeSeq(byte[] data, int offset)
        => (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);

    private static void EncodeSeq(byte[] data, int offset, uint seq)
    {
        data[offset] = (byte)seq;
        data[offset + 1] = (byte)(seq >> 8);
        data[offset + 2] = (byte)(seq >> 16);
        data[offset + 3] = (byte)(seq >> 24);
    }

    private void HandleReliableEnvelope(IPEndPoint endpoint, byte[] data)
    {
        if (data.Length < 6) return;
        var seq = DecodeSeq(data, 1);

        // Always ack, even duplicates - the previous ack may have been lost
        var ack = new byte[5];
        ack[0] = (byte)PacketType.ReliableAck;
        EncodeSeq(ack, 1, seq);
        _udpServer?.Send(ack, 5, endpoint);

        bool fresh;
        lock (_reliableLock)
        {
            if (!_reliableRecv.TryGetValue(endpoint, out var state))
            {
                if (_reliableRecv.Count > 256)
                {
                    var cutoff = DateTime.UtcNow.AddSeconds(-60);
                    foreach (var stale in _reliableRecv.Where(kvp => kvp.Value.LastSeen < cutoff)
                                 .Select(kvp => kvp.Key).ToList())
                        _reliableRecv.Remove(stale);
                }
                state = new ReliableRecvState();
                _reliableRecv[endpoint] = state;
            }
            state.LastSeen = DateTime.UtcNow;
            fresh = MarkDelivered(state, seq);
        }
        if (!fresh) return; // duplicate

        var inner = new byte[data.Length - 5];
        Buffer.BlockCopy(data, 5, inner, 0, inner.Length);
        HandlePacket(endpoint, inner, reliable: true);
    }

    private static bool MarkDelivered(ReliableRecvState state, uint seq)
    {
        if (seq <= state.Floor || state.Above.Contains(seq)) return false;
        if (seq == state.Floor + 1)
        {
            state.Floor = seq;
            while (state.Above.Remove(state.Floor + 1)) state.Floor++;
        }
        else
        {
            state.Above.Add(seq);
            if (seq > state.Floor + ReliableRecvWindow)
            {
                state.Floor = seq - ReliableRecvWindow / 2;
                state.Above.RemoveWhere(s => s <= state.Floor);
                while (state.Above.Remove(state.Floor + 1)) state.Floor++;
            }
        }
        return true;
    }

    private void HandleReliableAck(IPEndPoint endpoint, byte[] data)
    {
        if (data.Length < 5) return;
        var seq = DecodeSeq(data, 1);
        lock (_reliableLock)
        {
            if (_reliableSend.TryGetValue(endpoint, out var state))
                state.Pending.Remove(seq);
        }
        if (_connectionService.GetByEndpoint(endpoint) is { } player)
            player.LastActivity = DateTime.UtcNow;
    }

    private void SendToEndpointReliable(IPEndPoint endpoint, Packet packet)
        => SendRawReliable(endpoint, _packetRegistry.Serialize(packet));

    private void SendRawReliable(IPEndPoint endpoint, byte[] inner)
    {
        byte[] buf;
        lock (_reliableLock)
        {
            if (!_reliableSend.TryGetValue(endpoint, out var state))
            {
                state = new ReliableSendState();
                _reliableSend[endpoint] = state;
            }
            var seq = ++state.NextSeq;
            buf = new byte[inner.Length + 5];
            buf[0] = (byte)PacketType.ReliableEnvelope;
            EncodeSeq(buf, 1, seq);
            Buffer.BlockCopy(inner, 0, buf, 5, inner.Length);
            var now = DateTime.UtcNow;
            state.Pending[seq] = new PendingSend
            {
                Data = buf,
                InnerType = inner.Length > 0 ? inner[0] : (byte)0,
                FirstSend = now,
                LastSend = now,
                Attempts = 1
            };
        }
        _udpServer?.Send(buf, buf.Length, endpoint);
    }

    private void ReliableResendTick()
    {
        var now = DateTime.UtcNow;
        var toSend = new List<(IPEndPoint ep, byte[] data)>();
        lock (_reliableLock)
        {
            foreach (var kvp in _reliableSend)
            {
                var ep = kvp.Key;
                List<uint>? dead = null;
                foreach (var p in kvp.Value.Pending)
                {
                    var send = p.Value;
                    if (now - send.LastSend < ResendInterval) continue;
                    if (send.Attempts >= MaxResendAttempts || now - send.FirstSend > GiveUpAfter)
                    {
                        (dead ??= new List<uint>()).Add(p.Key);
                        continue;
                    }
                    send.LastSend = now;
                    send.Attempts++;
                    toSend.Add((ep, send.Data));
                }
                if (dead != null)
                {
                    foreach (var seq in dead)
                    {
                        var p = kvp.Value.Pending[seq];
                        kvp.Value.Pending.Remove(seq);
                        _logger.LogWarning("[Reliable] Gave up on seq={Seq} type=0x{Type:X2} to {Ep} after {N} attempts",
                            seq, p.InnerType, ep, p.Attempts);
                    }
                }
            }
        }
        foreach (var (ep, data) in toSend)
            _udpServer?.Send(data, data.Length, ep);
    }

    private void DropReliableChannels(IPEndPoint endpoint)
    {
        lock (_reliableLock)
        {
            _reliableRecv.Remove(endpoint);
            _reliableSend.Remove(endpoint);
        }
    }

    // ------------------------------------------------------------------
    // Relay + broadcast helpers
    // ------------------------------------------------------------------

    private List<IPEndPoint> AllEndpoints()
    {
        lock (_broadcastLock)
            return _connectionService.Players.Values
                .Where(p => p.Endpoint != null).Select(p => p.Endpoint!).ToList();
    }

    private List<IPEndPoint> OtherEndpoints(IPEndPoint from)
    {
        lock (_broadcastLock)
            return _connectionService.Players.Values
                .Where(p => p.Endpoint != null && !p.Endpoint.Equals(from))
                .Select(p => p.Endpoint!).ToList();
    }

    /// <summary>Relay the original datagram bytes to every peer except the sender.</summary>
    private void RelayRawToOthers(IPEndPoint from, byte[] inner, bool reliable)
    {
        foreach (var ep in OtherEndpoints(from))
        {
            if (reliable) SendRawReliable(ep, inner);
            else _udpServer?.Send(inner, inner.Length, ep);
        }
    }

    /// <summary>Relay a (possibly rewritten) packet to every peer except the sender.</summary>
    private void RelayTypedToOthers(IPEndPoint from, Packet packet, bool reliable)
        => RelayRawToOthers(from, _packetRegistry.Serialize(packet), reliable);

    private void SendToEndpoint(IPEndPoint endpoint, Packet packet)
    {
        var data = _packetRegistry.Serialize(packet);
        _udpServer?.Send(data, data.Length, endpoint);
    }

    private void Broadcast<T>(T packet) where T : Packet
    {
        var data = _packetRegistry.Serialize(packet);
        foreach (var ep in AllEndpoints())
            _udpServer?.Send(data, data.Length, ep);
    }

    private void BroadcastReliable<T>(T packet) where T : Packet
    {
        foreach (var ep in AllEndpoints())
            SendToEndpointReliable(ep, packet);
    }

    // ------------------------------------------------------------------
    // Connect / handshake
    // ------------------------------------------------------------------

    private void HandleConnectRequest(IPEndPoint endpoint, NetDataReader reader)
    {
        var packet = new ConnectRequestPacket();
        packet.Deserialize(reader);

        // A (re)connect restarts the client's reliable sequence stream at 1 -
        // stale dedupe/pending state would corrupt the fresh session
        DropReliableChannels(endpoint);

        var result = _connectionService.TryAuthenticate(endpoint, packet);
        if (!result.Accepted)
        {
            // Unreliable: the client re-sends its ConnectRequest until answered
            SendToEndpoint(endpoint, new ConnectResponsePacket
            {
                Accepted = false,
                Message = result.Message,
                IronbarkVersion = Ironbark.Version,
                Capabilities = Ironbark.Caps.Local
            });
            return;
        }

        var player = result.Player!;

        // Connect response stays unreliable (client retries the handshake)
        SendToEndpoint(endpoint, new ConnectResponsePacket
        {
            ClientId = player.Id,
            Accepted = true,
            Message = "Connected",
            IronbarkVersion = Ironbark.Version,
            Capabilities = Ironbark.Caps.Local
        });
        _logger.LogInformation("[Ironbark] {Banner} player '{Name}' id={Id}", Ironbark.Banner, player.Name, player.Id);

        // Everything below is the join snapshot - reliable so a late joiner
        // never permanently misses a piece of world state
        var playerList = new PlayerListPacket
        {
            Players = _connectionService.Players.Values
                .Select(p => new PlayerListPacket.PlayerInfo { Id = p.Id, Name = p.Name }).ToArray()
        };
        SendToEndpointReliable(endpoint, playerList);

        var joinedPkt = new PlayerJoinedPacket
        {
            PlayerId = player.Id,
            PlayerName = player.Name,
            SpawnX = player.Position.X, SpawnY = player.Position.Y, SpawnZ = player.Position.Z,
            SpawnRx = player.Rotation.X, SpawnRy = player.Rotation.Y, SpawnRz = player.Rotation.Z, SpawnRw = player.Rotation.W
        };
        foreach (var ep in OtherEndpoints(endpoint))
            SendToEndpointReliable(ep, joinedPkt);

        SendSnapshotTo(endpoint);
        _logger.LogInformation("Player '{Name}' connected as ID {Id}", player.Name, player.Id);
    }

    private void HandleGameStateRequest(IPEndPoint endpoint)
    {
        _logger.LogInformation("[GameStateRequest] Received from {Endpoint}", endpoint);
        SendSnapshotTo(endpoint);
    }

    /// <summary>Reliable full-world snapshot (game state + enemies/doors/pickups/damageables).</summary>
    private void SendSnapshotTo(IPEndPoint endpoint)
    {
        SendToEndpointReliable(endpoint, new GameStateSyncPacket
        {
            DayNumber = _worldState.DayNumber,
            IsNight = _worldState.IsNight,
            TimeOfDay = _worldState.TimeOfDay,
            GameTime = _worldState.TimeOfDay * 24f
        });

        // Enemies: mod uses EntityState/EntitySpawn from in-game peers — server does not
        // invent EnemyUpdate join truth (legacy cache cleared; see HandleLegacyEnemyUpdateRelay).

        foreach (var kvp in _worldState.Doors)
            SendToEndpointReliable(endpoint, new DoorStatePacket
            {
                DoorId = kvp.Key, IsOpen = kvp.Value.IsOpen, PlayerId = kvp.Value.ControlledBy
            });

        foreach (var kvp in _worldState.Pickups)
            SendToEndpointReliable(endpoint, new PickupStatePacket
            {
                PickupId = kvp.Key, ItemType = kvp.Value.ItemType, ItemName = kvp.Value.ItemName,
                X = kvp.Value.Position.X, Y = kvp.Value.Position.Y, Z = kvp.Value.Position.Z, Spawned = true,
                Durability = kvp.Value.Durability, Ammo = kvp.Value.Ammo,
                ModifierQuality = kvp.Value.ModifierQuality, Modifiers = kvp.Value.Modifiers
            });

        foreach (var kvp in _worldState.DamageableObjects)
            SendToEndpointReliable(endpoint, new DamageUpdatePacket
            {
                ObjectId = kvp.Key, PlayerId = -1,
                Health = kvp.Value.Health, MaxHealth = kvp.Value.MaxHealth, IsDestroyed = kvp.Value.IsDestroyed
            });
    }

    // ------------------------------------------------------------------
    // Per-packet handlers
    // ------------------------------------------------------------------

    private void HandlePositionUpdate(IPEndPoint endpoint, NetDataReader reader, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new PositionUpdatePacket();
        packet.Deserialize(reader);
        player.Position = new Vec3(packet.X, packet.Y, packet.Z);
        player.Rotation = new Quat(packet.Rx, packet.Ry, packet.Rz, packet.Rw);
        player.LastActivity = DateTime.UtcNow;

        // Relay with the server-side ID (not the client-claimed one)
        var relay = new PositionUpdatePacket
        {
            PlayerId = player.Id,
            X = packet.X, Y = packet.Y, Z = packet.Z,
            Rx = packet.Rx, Ry = packet.Ry, Rz = packet.Rz, Rw = packet.Rw,
            LegsRx = packet.LegsRx, LegsRy = packet.LegsRy, LegsRz = packet.LegsRz, LegsRw = packet.LegsRw
        };
        RelayTypedToOthers(endpoint, relay, reliable);
    }

    private void HandleChatMessage(IPEndPoint endpoint, NetDataReader reader)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new ChatMessagePacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;
        _logger.LogInformation("[Chat] {Name}: {Message}", player.Name, packet.Message);

        // Chat is echoed to everyone including the sender, reliably
        BroadcastReliable(new ChatMessagePacket
        {
            SenderId = packet.SenderId,
            SenderName = player.Name,
            Message = packet.Message,
            Timestamp = packet.Timestamp
        });
    }

    private void HandleHeartbeatAck(IPEndPoint endpoint)
    {
        if (_connectionService.GetByEndpoint(endpoint) is { } player)
            player.LastActivity = DateTime.UtcNow;
    }

    private void HandleHealthUpdate(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new HealthUpdatePacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;

        RelayRawToOthers(endpoint, data, reliable);
        _logger.LogInformation("[HealthUpdate] Player {PlayerId} health: {Health}, dead: {IsDead}", packet.PlayerId, packet.Health, packet.IsDead);
    }

    private void HandleDoorState(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new DoorStatePacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;

        _worldState.Doors[packet.DoorId] = new DoorState
        {
            IsOpen = packet.IsOpen,
            ControlledBy = packet.PlayerId
        };

        RelayRawToOthers(endpoint, data, reliable);
        _logger.LogInformation("[DoorState] Door {DoorId} -> {State} by player {PlayerId}", packet.DoorId, packet.IsOpen ? "open" : "closed", packet.PlayerId);
    }

    private void HandlePickupState(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new PickupStatePacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;

        if (packet.Spawned)
            _worldState.Pickups[packet.PickupId] = new PickupState
            {
                ItemType = packet.ItemType,
                ItemName = packet.ItemName,
                Position = new Vec3(packet.X, packet.Y, packet.Z),
                Durability = packet.Durability,
                Ammo = packet.Ammo,
                ModifierQuality = packet.ModifierQuality,
                Modifiers = packet.Modifiers ?? ""
            };
        else
            _worldState.Pickups.Remove(packet.PickupId);

        RelayRawToOthers(endpoint, data, reliable);
        _logger.LogInformation("[PickupState] Pickup {PickupId} {Action}", packet.PickupId, packet.Spawned ? "spawned" : "removed");
    }

    /// <summary>
    /// Legacy EnemyUpdate: relay only. Never seed join snapshot from this cache —
    /// Ironbark mod authority is EntityState + EntitySpawn from game peers.
    /// </summary>
    private void HandleLegacyEnemyUpdateRelay(IPEndPoint endpoint, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;
        player.LastActivity = DateTime.UtcNow;
        // Drop any stale cache so accidental SendSnapshot never re-injects fiction.
        if (_worldState.Enemies.Count > 0)
            _worldState.Enemies.Clear();
        RelayRawToOthers(endpoint, data, reliable);
        _logger.LogDebug("[EnemyUpdate] legacy relay only (no join cache)");
    }

    private void HandleDayNightUpdate(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new DayNightUpdatePacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;

        // Observe the time-authority client's transitions so the join snapshot
        // stays current, then relay to the other clients. In authoritative
        // mode the server drives time itself and ignores client transitions.
        if (!_config.AuthoritativeWorld)
        {
            _worldState.DayNumber = packet.DayNumber;
            _worldState.IsNight = packet.IsNight;
            _lastBroadcastDay = packet.DayNumber;
            _lastBroadcastNight = packet.IsNight;
        }

        RelayRawToOthers(endpoint, data, reliable);
        _logger.LogInformation("[DayNight] day {Day} night={Night} (from authority player {Id})", packet.DayNumber, packet.IsNight, player.Id);
    }

    private void HandleEventTrigger(IPEndPoint endpoint, NetDataReader reader, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new EventTriggerPacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;

        RelayTypedToOthers(endpoint, packet, reliable);
        _logger.LogInformation("[EventTrigger] Event by player {PlayerId}", player.Id);
    }

    private void HandleIronbarkRelay(IPEndPoint endpoint, byte[] data, bool reliable, bool fanOut, string label)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;
        player.LastActivity = DateTime.UtcNow;
        if (fanOut)
            RelayRawToOthers(endpoint, data, reliable);
        _logger.LogDebug("[Ironbark] {Label} from player {PlayerId} fanOut={FanOut}", label, player.Id, fanOut);
    }

    private void HandleInventoryUpdate(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new InventoryUpdatePacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;

        RelayRawToOthers(endpoint, data, reliable);
        _logger.LogDebug("[InventoryUpdate] Player {PlayerId}: item={ItemId}, qty={Quantity}", packet.PlayerId, packet.ItemId, packet.Quantity);
    }

    private void HandleDamageUpdate(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new DamageUpdatePacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;

        _worldState.DamageableObjects[packet.ObjectId] = new DamageableObjectState
        {
            Health = packet.Health,
            MaxHealth = packet.MaxHealth,
            IsDestroyed = packet.IsDestroyed
        };

        RelayRawToOthers(endpoint, data, reliable);
        _logger.LogInformation("[DamageUpdate] Object {ObjectId} hp={Health}/{MaxHealth} destroyed={IsDestroyed}", packet.ObjectId, packet.Health, packet.MaxHealth, packet.IsDestroyed);
    }

    private void HandlePlayerAnim(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;
        player.LastActivity = DateTime.UtcNow;
        RelayRawToOthers(endpoint, data, reliable);
    }

    private void HandleObjectMove(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;
        player.LastActivity = DateTime.UtcNow;
        RelayRawToOthers(endpoint, data, reliable);
    }

    private void HandleInteractiveState(IPEndPoint endpoint, NetDataReader reader, byte[] data, bool reliable)
    {
        var player = _connectionService.GetByEndpoint(endpoint);
        if (player == null) return;

        var packet = new InteractiveStatePacket();
        packet.Deserialize(reader);
        player.LastActivity = DateTime.UtcNow;

        RelayRawToOthers(endpoint, data, reliable);
        _logger.LogInformation("[InteractiveState] Object {ObjectId} type={ObjectType} active={IsActive} by player {PlayerId}", packet.ObjectId, packet.ObjectType, packet.IsActive, packet.PlayerId);
    }

    public WorldState GetWorldState() => _worldState;
    public void SaveWorld() => _saveService.Save(_worldState);
}
