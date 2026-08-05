using Microsoft.Extensions.Logging;

namespace DarkwoodMP.Server.Services;

public class ChatService
{
    private readonly ILogger<ChatService> _logger;

    public ChatService(ILogger<ChatService> logger)
    {
        _logger = logger;
    }

    public void SendMessage(string fromName, int fromId, string message, Action<string, int> broadcast)
    {
        var formatted = $"[{fromName}]: {message}";
        broadcast(formatted, fromId);
        _logger.LogInformation("Chat: {Formatted}", formatted);
    }
}
