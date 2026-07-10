namespace DarkwoodMP.Packets;

/// <summary>
/// Ironbark Protocol (IBP) — united Yokyy house + Horde discipline.
/// Product version (mod display) is independent; this int is the wire contract.
/// </summary>
public static class Ironbark
{
    public const string Name = "Ironbark";
    public const string Abbrev = "IBP";

    /// <summary>Wire protocol version. Strict equality on connect. Bump only on breaking changes.</summary>
    public const int Version = 2;

    public static string Banner => $"{Name} v{Version}";

    /// <summary>Local capability bits advertised on connect (v2).</summary>
    public static class Caps
    {
        public const uint None = 0;
        public const uint SpectateFull = 1u << 0;
        public const uint ClientBackup = 1u << 1;
        /// <summary>Free-body PhysicsState product depth — not claimed until emitters exist.</summary>
        public const uint PhysicsState = 1u << 2;
        /// <summary>What this build supports (honest: no PhysicsState free-body claim).</summary>
        public const uint Local =
            SpectateFull | ClientBackup;
    }
}

/// <summary>Delivery class for hop reliability (mod NetworkLayer / server).</summary>
public enum IronbarkReliability : byte
{
    Unreliable = 0,
    Reliable = 1,
    /// <summary>Ack/resend until peer drop — never give up on budget.</summary>
    Critical = 2,
}

/// <summary>Host fan-out policy (Horde Forwardable semantics).</summary>
public enum IronbarkForward : byte
{
    /// <summary>Host/auth only — do not rebroadcast to other peers.</summary>
    None = 0,
    /// <summary>Re-originate payload to all peers except sender.</summary>
    Direct = 1,
    /// <summary>Fan-out; preserve originating player identity.</summary>
    Player = 2,
}

/// <summary>
/// Ironbark message metadata for PacketType opcodes.
/// Unknown types: Reliable + Direct (safe default for shared world).
/// </summary>
public static class IronbarkMeta
{
    public static IronbarkReliability Reliability(PacketType type)
    {
        switch (type)
        {
            case PacketType.PositionUpdate:
            case PacketType.PlayerAnim:
            case PacketType.EntityState:
            case PacketType.ObjectMove:
            case PacketType.Heartbeat:
            case PacketType.HeartbeatAck:
            case PacketType.DreamAudio:
            case PacketType.EntitySound:
            case PacketType.PlayerAudio:
            case PacketType.PlayerNoise:
            case PacketType.BulletFx:
            case PacketType.FlarePos:
            case PacketType.FiredWeapon:
            case PacketType.ShadowState:
            case PacketType.ClockSync:
            case PacketType.SyncCheckDigest:
            case PacketType.PlayerEffect:
            case PacketType.ItemLight:
                return IronbarkReliability.Unreliable;

            case PacketType.WorldRequest:
            case PacketType.WorldOffer:
            case PacketType.WorldChunk:
            case PacketType.WorldEnd:
            case PacketType.ConnectRequest:
            case PacketType.ConnectResponse:
            case PacketType.GameStateSync:
            case PacketType.PlayerList:
            case PacketType.TradeInventory:
            case PacketType.ClientStateBackupChunk:
            case PacketType.SaveBeat:
            case PacketType.ChapterTransition:
            case PacketType.SceneLoad:
                return IronbarkReliability.Critical;

            case PacketType.PhysicsStateBatch:
                return IronbarkReliability.Unreliable;

            case PacketType.EntitySpawn:
            case PacketType.EntityBurning:
            case PacketType.PlayerBurning:
            case PacketType.ExplosionSpawnObject:
            case PacketType.LiquidStopBurn:
                return IronbarkReliability.Reliable;

            default:
                return IronbarkReliability.Reliable;
        }
    }

    public static bool IsCritical(PacketType type) =>
        Reliability(type) == IronbarkReliability.Critical;

    public static bool IsCritical(byte typeByte) => IsCritical((PacketType)typeByte);

    public static IronbarkForward Forward(PacketType type)
    {
        switch (type)
        {
            case PacketType.ConnectRequest:
            case PacketType.ConnectResponse:
            case PacketType.GameStateRequest:
            case PacketType.GameStateSync:
            case PacketType.PlayerList:
            case PacketType.WorldRequest:
            case PacketType.WorldOffer:
            case PacketType.WorldChunk:
            case PacketType.WorldEnd:
            case PacketType.ClientStateBackupChunk: // client → host file store only
            case PacketType.Heartbeat:
            case PacketType.HeartbeatAck:
            case PacketType.ServerCommand:
                return IronbarkForward.None;

            case PacketType.SaveBeat:
            case PacketType.ChapterTransition:
            case PacketType.SceneLoad:
                return IronbarkForward.Direct;

            case PacketType.PositionUpdate:
            case PacketType.PlayerAnim:
            case PacketType.HealthUpdate:
            case PacketType.EntityState:
            case PacketType.ObjectMove:
            case PacketType.DreamEntered:
            case PacketType.FinalDreamDeath:
            case PacketType.DreamPrepare:
            case PacketType.DreamStart:
            case PacketType.DreamEnd:
            case PacketType.VaultState:
            case PacketType.InteractionLockSync:
                return IronbarkForward.Player;

            default:
                return IronbarkForward.Direct;
        }
    }

    public static bool ShouldFanOut(PacketType type) =>
        Forward(type) != IronbarkForward.None;

    /// <summary>Obsolete ActionEvent critical-tag table (v2 typed only) — always false.</summary>
    public static bool IsCriticalActionName(string actionName) => false;
}
