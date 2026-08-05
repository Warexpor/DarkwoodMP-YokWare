using System.Net;
using DarkwoodMP.Packets;
using DarkwoodMP.Server.Config;
using DarkwoodMP.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DarkwoodMP.Server.Services;

public class ConnectionService
{
    private readonly ServerConfig _config;
    private readonly ILogger<ConnectionService> _logger;
    private readonly object _lock = new();
    private readonly Queue<int> _availableIds = new();
    // Concurrent: read by the tick timer thread while the receive thread
    // adds/removes players
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, ServerPlayer> _players = new();

    public ConnectionService(IOptions<ServerConfig> config, ILogger<ConnectionService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public AuthenticateResult TryAuthenticate(IPEndPoint endpoint, ConnectRequestPacket request)
    {
        lock (_lock)
        {
            if (request.IronbarkVersion != Ironbark.Version)
            {
                var msg = $"{Ironbark.Abbrev} mismatch: need {Ironbark.Version}, peer sent {request.IronbarkVersion}";
                _logger.LogWarning("[Ironbark] Rejecting {Address}: {Msg}", endpoint.Address, msg);
                return AuthenticateResult.ProtocolMismatch(msg);
            }

            if (_players.Count >= _config.MaxPlayers)
            {
                _logger.LogWarning("Server full, rejecting {Address}", endpoint.Address);
                return AuthenticateResult.Full;
            }

            if (!string.IsNullOrEmpty(_config.Password) && request.Password != _config.Password)
            {
                _logger.LogWarning("Wrong password from {Address}", endpoint.Address);
                return AuthenticateResult.WrongPassword;
            }

            int id = _availableIds.Count > 0 ? _availableIds.Dequeue() : (_players.Keys.Any() ? _players.Keys.Max() + 1 : 1);
            var player = new ServerPlayer
            {
                Id = id,
                Name = request.Name,
                Endpoint = endpoint,
                ConnectedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            _players[id] = player;
            return AuthenticateResult.Success(player);
        }
    }

    public ServerPlayer? GetByEndpoint(IPEndPoint endpoint)
    {
        lock (_lock)
        {
            foreach (var p in _players.Values)
            {
                if (p.Endpoint?.Equals(endpoint) == true)
                    return p;
            }
            return null;
        }
    }

    public bool RemoveByPlayer(ServerPlayer player)
    {
        lock (_lock)
        {
            int? id = null;
            foreach (var kvp in _players)
            {
                if (kvp.Value == player)
                {
                    id = kvp.Key;
                    break;
                }
            }

            if (id == null) return false;

            _availableIds.Enqueue(id.Value);
            _logger.LogInformation("Removed player '{Name}' (ID {Id})", player.Name, id.Value);
            return _players.TryRemove(id.Value, out _);
        }
    }

    public IReadOnlyDictionary<int, ServerPlayer> Players => _players;
}

public record AuthenticateResult(bool Accepted, ServerPlayer? Player, string Message)
{
    public static AuthenticateResult Success(ServerPlayer player) => new(true, player, "");
    public static AuthenticateResult Full => new(false, null, "Server full");
    public static AuthenticateResult WrongPassword => new(false, null, "Wrong password");
    public static AuthenticateResult InvalidPassword => new(false, null, "Wrong password");
    public static AuthenticateResult BadRequest => new(false, null, "Bad request");
    public static AuthenticateResult ProtocolMismatch(string message) => new(false, null, message);
};
