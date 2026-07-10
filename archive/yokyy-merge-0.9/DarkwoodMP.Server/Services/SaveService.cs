using DarkwoodMP.Server.Config;
using DarkwoodMP.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DarkwoodMP.Server.Services;

public class SaveService
{
    private readonly ILogger<SaveService> _logger;
    private readonly string _savePath;

    public SaveService(ILogger<SaveService> logger, IOptions<ServerConfig> config)
    {
        _logger = logger;
        _savePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "world.json");
    }

    public void Save(WorldState state)
    {
        try
        {
            state.SaveToFile(_savePath);
            _logger.LogInformation("World saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save world");
        }
    }
}
