using System.Net;
using DarkwoodMP.Server.Models;
using Microsoft.Extensions.Logging;

namespace DarkwoodMP.Server.Services;

public class PlayerService
{
    private readonly ILogger<PlayerService> _logger;
    private readonly ConnectionService _connection;

    public PlayerService(ILogger<PlayerService> logger, ConnectionService connection)
    {
        _logger = logger;
        _connection = connection;
    }

    public IReadOnlyDictionary<int, ServerPlayer> Players => _connection.Players;

    public ServerPlayer? GetByEndpoint(IPEndPoint endpoint)
    {
        return _connection.GetByEndpoint(endpoint);
    }

    public ServerPlayer? GetById(int id)
    {
        _connection.Players.TryGetValue(id, out var player);
        return player;
    }
}
