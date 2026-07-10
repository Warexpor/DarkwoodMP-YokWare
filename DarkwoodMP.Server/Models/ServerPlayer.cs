using LiteNetLib;
using System.Net;

namespace DarkwoodMP.Server.Models;

public record Vec3(float X, float Y, float Z)
{
    public float[] ToArray() => [X, Y, Z];
    public static Vec3 Zero => new(0, 0, 0);
}

public record Quat(float X, float Y, float Z, float W)
{
    public static Quat Identity => new(0, 0, 0, 1);
}

public class ServerPlayer
{
    public int Id { get; set; }
    public string Name { get; set; } = "Player";
    public IPEndPoint? Endpoint { get; set; }
    public Vec3 Position { get; set; } = Vec3.Zero;
    public Quat Rotation { get; set; } = Quat.Identity;
    public float Health { get; set; } = 100f;
    public bool IsAlive { get; set; } = true;
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}
