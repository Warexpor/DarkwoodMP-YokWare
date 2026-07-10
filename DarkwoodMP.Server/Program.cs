using DarkwoodMP.Server.Services;
using DarkwoodMP.Server.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DarkwoodMP.Server;

public class Program
{
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console()
            .WriteTo.File("logs/darkwoodmp-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("DarkwoodMP Server starting...");

            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((ctx, services) =>
                {
                    var config = LoadConfig(ctx.Configuration);
                    services.Configure<ServerConfig>(s =>
                    {
                        s.Port = config.Port;
                        s.MaxPlayers = config.MaxPlayers;
                        s.Password = config.Password;
                        s.SaveInterval = config.SaveInterval;
                        s.PositionSyncRate = config.PositionSyncRate;
                        s.EnemySyncRate = config.EnemySyncRate;
                        s.TickRate = config.TickRate;
                        s.AuthoritativeWorld = config.AuthoritativeWorld;
                    });
                    services.AddSingleton<ConnectionService>();
                    services.AddSingleton<PlayerService>();
                    services.AddSingleton<WorldService>();
                    services.AddSingleton<SaveService>();
                    services.AddSingleton<ChatService>();
                    services.AddSingleton<PacketRegistryService>();
                    services.AddSingleton<WorldCacheService>();
                    services.AddHostedService<ServerHostService>();
                })
                .Build();

            await host.StartAsync();

            // Wait for shutdown signal
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            await host.WaitForShutdownAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DarkwoodMP Server terminated unexpectedly");
        }
        finally
        {
            Log.Information("DarkwoodMP Server shut down");
            Log.CloseAndFlush();
        }
    }

    private static ServerConfig LoadConfig(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var config = new ServerConfig();
        config.Port = int.TryParse(configuration["Port"], out var port) ? port : 7777;
        config.MaxPlayers = int.TryParse(configuration["MaxPlayers"], out var max) ? max : 8;
        config.Password = configuration["Password"] ?? string.Empty;
        config.SaveInterval = int.TryParse(configuration["SaveInterval"], out var saveInterval) ? saveInterval : 60;
        config.PositionSyncRate = int.TryParse(configuration["PositionSyncRate"], out var posRate) ? posRate : 10;
        config.EnemySyncRate = int.TryParse(configuration["EnemySyncRate"], out var enemyRate) ? enemyRate : 5;
        config.TickRate = int.TryParse(configuration["TickRate"], out var tick) ? tick : 30;
        config.AuthoritativeWorld = bool.TryParse(configuration["AuthoritativeWorld"], out var authWorld) && authWorld;
        return config;
    }
}
