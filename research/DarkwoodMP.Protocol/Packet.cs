using System;
using LiteNetLib.Utils;

// SHARED SOURCE: compiled into BOTH DarkwoodMP.Mod (net472, LiteNetLib 1.3)
// and DarkwoodMP.Server (net8, LiteNetLib 2.1) via <Compile Include>. This is
// the single wire-format authority - the per-project copies it replaced once
// drifted (HealthUpdatePacket field order, missing EntityState opcode) and
// broke joins. Keep the code C# 10 / nullable-agnostic / explicit-usings.

namespace DarkwoodMP.Packets;

/// <summary>
/// Base class for all network packets. Packet types are registered with the PacketRegistry.
/// </summary>
public abstract class Packet
{
    public abstract PacketType Type { get; }
    /// <summary>Ironbark v2 wire id (u16 LE). Defaults to Type cast; dense remap may override later.</summary>
    public virtual ushort MessageId => (ushort)Type;
    public abstract void Serialize(NetDataWriter writer);
    public abstract void Deserialize(NetDataReader reader);
}

public enum PacketType : byte
{
    // Connection
    ConnectRequest = 0x01,
    ConnectResponse = 0x02,
    PlayerJoined = 0x04,
    PlayerLeft = 0x05,
    GameStateRequest = 0x06,
    PlayerList = 0xC0,

    // Player
    PositionUpdate = 0x10,
    HealthUpdate = 0x12,
    // ActionEvent 0x13 removed in Ironbark v2
    InventoryUpdate = 0x70,

    // World
    EnemyUpdate = 0x22,
    DoorState = 0x92,
    PickupState = 0x60,
    EnvironmentEffect = 0x91,

    // Game
    GameStateSync = 0x20,
    DayNightUpdate = 0x23,
    EventTrigger = 0x21,

    // Chat
    ChatMessage = 0xB0,
    SystemMessage = 0xB1,

    // Server
    ServerCommand = 0xC1,
    Heartbeat = 0xD0,
    HeartbeatAck = 0xD1,

    // Damage / Interactive
    DamageUpdate = 0x24,
    InteractiveState = 0x25,

    // Movable world objects (dragged furniture)
    ObjectMove = 0x26,

    // Player animation clips (held item, attacks, movement pose)
    PlayerAnim = 0x27,

    // Host-authoritative enemy sync: batched entity snapshots (authority -> others)
    EntityState = 0x28,

    // World download (transfer host's/server's generated world to a joining client)
    WorldRequest = 0x30,
    WorldOffer = 0x31,
    WorldChunk = 0x32,
    WorldEnd = 0x33,

    // Ironbark v1 typed domains (wave A/B/C)
    TradeInventory = 0x40,
    ClientStateBackupChunk = 0x41,
    SaveBeat = 0x42,
    LocationEnter = 0x43,
    LocationExit = 0x44,
    MapMarker = 0x45,
    MapDiscover = 0x46,
    Reputation = 0x47,
    ReputationBulk = 0x48,
    HideoutState = 0x49,
    JournalSync = 0x4A,
    // Wave D — story / dream / chapter
    DreamPrepare = 0x4B,
    DreamStart = 0x4C,
    DreamEntered = 0x4D,
    DreamEnd = 0x4E,
    DreamAudio = 0x4F,
    DreamDoor = 0x50,
    FinalDreamDeath = 0x51,
    CutsceneSync = 0x52,
    ChapterTransition = 0x53,
    ChapterNotify = 0x54,
    SceneLoad = 0x55,
    ExamineRequest = 0x56,
    ExamineState = 0x57,
    // Wave E — world economy / interact
    ContainerState = 0x58,
    ContainerRequest = 0x59,
    DeathDropSpawn = 0x5A,
    BarricadeState = 0x5B,
    BuildPlaced = 0x5C,
    BuildConstruct = 0x5D,
    VaultState = 0x5E,
    WorldObjectGone = 0x5F,
    InteractionLockSync = 0x61, // not 0x60 — reserved by PickupState
    // Wave F — residual ActionEvent purge
    FlagDelta = 0x62,
    DialogState = 0x63,
    NpcConvState = 0x64,
    DialogOutcome = 0x65,
    GasTrail = 0x66,
    BurnLiquid = 0x67,
    PvpDamage = 0x68,
    EntityAttack = 0x69,
    PvpFx = 0x6A,
    MeleeWorldHit = 0x6B,
    FiredWeapon = 0x6C,
    PlayerDied = 0x6D,
    PlayerDeathClock = 0x6E,
    InfectionSplat = 0x6F,
    // 0x70 InventoryUpdate (legacy)
    EntitySound = 0x71,
    PlayerAudio = 0x72,
    PlayerNoise = 0x73,
    BulletFx = 0x74,
    ItemDisarm = 0x75,
    TrapFire = 0x76,
    ThrownItem = 0x77,
    ThrownArmed = 0x78,
    ItemLight = 0x79,
    FlarePos = 0x7A,
    WorldLock = 0x7B,
    StationSawFuel = 0x7C,
    StationFeeder = 0x7D,
    StationLure = 0x7E,
    OxygenConvert = 0x7F,
    GeneratorFuel = 0x80,
    ClockSync = 0x81,
    GameTimeSync = 0x82,
    WorkbenchLevel = 0x83,
    WeatherState = 0x84,
    ScenarioState = 0x85,
    ScenarioEvent = 0x86,
    PlayerEffect = 0x87,
    ShadowState = 0x88,
    LocationNpc = 0x89,
    GameEventFire = 0x8A,
    WorldSeed = 0x8B,
    WorldSeedAuth = 0x8C,
    SyncCheckDigest = 0x8D,

    // Ironbark v2 PhysicsState lane (MessageId band 0x90+; not outer framing)
    PhysicsStateBatch = 0x90,
    // 0x91 EnvironmentEffect — legacy reserved (unused emit)
    // 0x92 DoorState

    // Gap-closure wave G — Horde burn / explosion / reliable spawn
    EntityBurning = 0x93,
    PlayerBurning = 0x94,
    ExplosionSpawnObject = 0x95,
    EntitySpawn = 0x96,
    LiquidStopBurn = 0x97,

    // Reliability framing (v0.6): outer UDP datagram prefixes ONLY
    // (not MessageIds — first byte of raw socket payload).
    // Envelope: [0xE0][seq:uint32 LE][inner]; Ack: [0xE1][seq].
    ReliableEnvelope = 0xE0,
    ReliableAck = 0xE1,
}
