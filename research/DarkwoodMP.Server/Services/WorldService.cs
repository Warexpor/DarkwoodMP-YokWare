using DarkwoodMP.Packets;
using DarkwoodMP.Server.Models;
using Microsoft.Extensions.Logging;

namespace DarkwoodMP.Server.Services;

public class WorldService
{
    private readonly ILogger<WorldService> _logger;
    private int _tickCount;

    public WorldService(ILogger<WorldService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<Packet> SyncTick(WorldState state, int tickRate, int enemySyncRate)
    {
        var tickCount = Interlocked.Increment(ref _tickCount);
        var packets = new List<Packet>();

        // Enemies: not server-simulated. Mod peers broadcast EntityState/EntitySpawn;
        // dedicated server is a pure relay for those MessageIds (no EnemyUpdate invent).

        // Rate-limited door sync
        if (tickCount % enemySyncRate == 0)
        {
            foreach (var kvp in state.Doors)
            {
                packets.Add(new DoorStatePacket
                {
                    DoorId = kvp.Key,
                    IsOpen = kvp.Value.IsOpen,
                    PlayerId = kvp.Value.ControlledBy
                });
            }
        }

        // Rate-limited pickup sync
        if (tickCount % enemySyncRate == 0)
        {
            foreach (var kvp in state.Pickups)
            {
                packets.Add(new PickupStatePacket
                {
                    PickupId = kvp.Key,
                    ItemType = kvp.Value.ItemType,
                    ItemName = kvp.Value.ItemName,
                    X = kvp.Value.Position.X,
                    Y = kvp.Value.Position.Y,
                    Z = kvp.Value.Position.Z,
                    Spawned = true
                });
            }
        }

        // Rate-limited damageable object sync
        if (tickCount % enemySyncRate == 0)
        {
            foreach (var kvp in state.DamageableObjects)
            {
                packets.Add(new DamageUpdatePacket
                {
                    ObjectId = kvp.Key,
                    PlayerId = -1,
                    Health = kvp.Value.Health,
                    MaxHealth = kvp.Value.MaxHealth,
                    IsDestroyed = kvp.Value.IsDestroyed
                });
            }
        }

        return packets;
    }
}
