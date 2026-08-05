namespace DarkwoodMP.Server.Models;

public class WorldState
{
    public int DayNumber { get; set; } = 1;
    public bool IsNight { get; set; }
    public float TimeOfDay { get; set; }
    public float TotalPlaytime { get; set; }
    public DateTime LastSave { get; set; } = DateTime.UtcNow;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public Dictionary<string, EnemyState> Enemies { get; set; } = new();
    public Dictionary<string, DoorState> Doors { get; set; } = new();
    public Dictionary<string, PickupState> Pickups { get; set; } = new();
    public Dictionary<string, DamageableObjectState> DamageableObjects { get; set; } = new();

    public void SaveToFile(string path)
    {
        var obj = new
        {
            DayNumber,
            IsNight,
            TimeOfDay,
            TotalPlaytime,
            LastSave = LastSave.ToString("o"),
            StartedAt = StartedAt.ToString("o"),
            Enemies = Enemies.Select(e => new { e.Key, EnemyType = e.Value.EnemyType, Position = e.Value.Position.ToArray(), State = e.Value.State, e.Value.Health, e.Value.IsAlive }),
            Doors = Doors.Select(d => new { d.Key, d.Value.IsOpen, d.Value.ControlledBy }),
            Pickups = Pickups.Select(p => new { p.Key, p.Value.ItemType, p.Value.ItemName, Position = p.Value.Position.ToArray(),
                p.Value.Durability, p.Value.Ammo, p.Value.ModifierQuality, p.Value.Modifiers }),
            DamageableObjects = DamageableObjects.Select(o => new { o.Key, o.Value.ObjectType, o.Value.Health, o.Value.MaxHealth, o.Value.IsDestroyed })
        };
        var json = System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        LastSave = DateTime.UtcNow;
    }

    public static WorldState LoadFromFile(string path)
    {
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        var obj = System.Text.Json.JsonSerializer.Deserialize<WorldStateData>(json);
        if (obj == null) return new();
        var ws = new WorldState
        {
            DayNumber = obj.DayNumber,
            IsNight = obj.IsNight,
            TimeOfDay = obj.TimeOfDay,
            TotalPlaytime = obj.TotalPlaytime,
            StartedAt = DateTime.Parse(obj.StartedAt),
            LastSave = DateTime.UtcNow,
            Enemies = new Dictionary<string, EnemyState>(),
            Doors = new Dictionary<string, DoorState>(),
            Pickups = new Dictionary<string, PickupState>(),
            DamageableObjects = new Dictionary<string, DamageableObjectState>()
        };

        if (obj.Enemies != null)
        {
            foreach (var e in obj.Enemies)
            {
                ws.Enemies[e.Key] = new EnemyState
                {
                    EnemyType = e.EnemyType,
                    Position = new Vec3(e.Position[0], e.Position[1], e.Position[2]),
                    State = e.State,
                    Health = e.Health,
                    IsAlive = e.IsAlive
                };
            }
        }

        if (obj.Doors != null)
        {
            foreach (var d in obj.Doors)
            {
                ws.Doors[d.Key] = new DoorState
                {
                    IsOpen = d.IsOpen,
                    ControlledBy = d.ControlledBy
                };
            }
        }

        if (obj.Pickups != null)
        {
            foreach (var p in obj.Pickups)
            {
                ws.Pickups[p.Key] = new PickupState
                {
                    ItemType = p.ItemType,
                    ItemName = p.ItemName,
                    Position = new Vec3(p.Position[0], p.Position[1], p.Position[2]),
                    Durability = p.Durability,
                    Ammo = p.Ammo,
                    ModifierQuality = p.ModifierQuality,
                    Modifiers = p.Modifiers ?? ""
                };
            }
        }

        if (obj.DamageableObjects != null)
        {
            foreach (var o in obj.DamageableObjects)
            {
                ws.DamageableObjects[o.Key] = new DamageableObjectState
                {
                    ObjectType = o.ObjectType,
                    Health = o.Health,
                    MaxHealth = o.MaxHealth,
                    IsDestroyed = o.IsDestroyed
                };
            }
        }

        return ws;
    }
}

public class WorldStateData
{
    public int DayNumber { get; set; }
    public bool IsNight { get; set; }
    public float TimeOfDay { get; set; }
    public float TotalPlaytime { get; set; }
    public string StartedAt { get; set; } = "";
    public string LastSave { get; set; } = "";
    public List<EnemyStateData>? Enemies { get; set; }
    public List<DoorStateData>? Doors { get; set; }
    public List<PickupStateData>? Pickups { get; set; }
    public List<DamageableObjectStateData>? DamageableObjects { get; set; }
}

public record EnemyStateData(string Key, string EnemyType, float[] Position, string State, float Health, bool IsAlive);
public record DoorStateData(string Key, bool IsOpen, int ControlledBy);
public record PickupStateData(string Key, string ItemType, string ItemName, float[] Position,
    float Durability = -1f, int Ammo = 0, int ModifierQuality = 0, string Modifiers = "");
public record DamageableObjectStateData(string Key, string ObjectType, float Health, float MaxHealth, bool IsDestroyed);

public class EnemyState
{
    public string EnemyType { get; set; } = "";
    public Vec3 Position { get; set; } = Vec3.Zero;
    public Quat Rotation { get; set; } = Quat.Identity;
    public string State { get; set; } = "";
    public float Health { get; set; }
    public bool IsAlive { get; set; }
    public float[] ToArray() => Position.ToArray();
}

public class DoorState
{
    public bool IsOpen { get; set; }
    public int ControlledBy { get; set; }
}

public class PickupState
{
    public string ItemType { get; set; } = "";
    public string ItemName { get; set; } = "";
    public Vec3 Position { get; set; } = Vec3.Zero;
    // Per-instance item state, preserved so a late-join snapshot re-send keeps a
    // dropped weapon's wear/ammo/mods. Durability < 0 = "no state".
    public float Durability { get; set; } = -1f;
    public int Ammo { get; set; }
    public int ModifierQuality { get; set; }
    public string Modifiers { get; set; } = "";
}

public class DamageableObjectState
{
    public string ObjectType { get; set; } = "";
    public float Health { get; set; }
    public float MaxHealth { get; set; }
    public bool IsDestroyed { get; set; }
}
