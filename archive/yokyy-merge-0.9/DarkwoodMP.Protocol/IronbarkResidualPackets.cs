using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

/// <summary>Ironbark wave F — final ActionEvent string-tag purge.</summary>

public class FlagDeltaPacket : Packet
{
    public override PacketType Type => PacketType.FlagDelta;
    public int PlayerId { get; set; }
    public bool IsInt { get; set; }
    public string FlagName { get; set; } = "";
    public int Value { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(IsInt); w.Put(FlagName ?? ""); w.Put(Value); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); IsInt = r.GetBool(); FlagName = r.GetString(); Value = r.GetInt(); }
}

public class DialogStatePacket : Packet
{
    public override PacketType Type => PacketType.DialogState;
    public int PlayerId { get; set; }
    /// <summary>Encoded dialogue snapshot (legacy dlgstate body).</summary>
    public string Payload { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Payload ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Payload = r.GetString(); }
}

public class NpcConvStatePacket : Packet
{
    public override PacketType Type => PacketType.NpcConvState;
    public int PlayerId { get; set; }
    public bool WantsToTalk { get; set; }
    public int Reputation { get; set; }
    public string NpcName { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(WantsToTalk); w.Put(Reputation); w.Put(NpcName ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); WantsToTalk = r.GetBool(); Reputation = r.GetInt(); NpcName = r.GetString(); }
}

public class DialogOutcomePacket : Packet
{
    public override PacketType Type => PacketType.DialogOutcome;
    public int PlayerId { get; set; }
    public string Payload { get; set; } = ""; // npc|idx|dialogue|target

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Payload ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Payload = r.GetString(); }
}

public class GasTrailPacket : Packet
{
    public override PacketType Type => PacketType.GasTrail;
    public int PlayerId { get; set; }
    public float X, Y, Z, Rx, Ry, Rz, Rw;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(X); w.Put(Y); w.Put(Z); w.Put(Rx); w.Put(Ry); w.Put(Rz); w.Put(Rw); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat();
      Rx = r.GetFloat(); Ry = r.GetFloat(); Rz = r.GetFloat(); Rw = r.GetFloat(); }
}

public class BurnLiquidPacket : Packet
{
    public override PacketType Type => PacketType.BurnLiquid;
    public int PlayerId { get; set; }
    public float X, Z;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(X); w.Put(Z); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); X = r.GetFloat(); Z = r.GetFloat(); }
}

public class PvpDamagePacket : Packet
{
    public override PacketType Type => PacketType.PvpDamage;
    public int PlayerId { get; set; }
    public int TargetPlayerId { get; set; }
    public float Damage { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(TargetPlayerId); w.Put(Damage); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); TargetPlayerId = r.GetInt(); Damage = r.GetFloat(); }
}

public class EntityAttackPacket : Packet
{
    public override PacketType Type => PacketType.EntityAttack;
    public int PlayerId { get; set; }
    public string EntityId { get; set; } = "";
    public float Damage { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(EntityId ?? ""); w.Put(Damage); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); EntityId = r.GetString(); Damage = r.GetFloat(); }
}

public class PvpFxPacket : Packet
{
    public override PacketType Type => PacketType.PvpFx;
    public int PlayerId { get; set; }
    public int TargetPlayerId { get; set; }
    public int EffectType { get; set; }
    public float Duration { get; set; }
    public float Modifier { get; set; }
    public float Interval { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(TargetPlayerId); w.Put(EffectType); w.Put(Duration); w.Put(Modifier); w.Put(Interval); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); TargetPlayerId = r.GetInt(); EffectType = r.GetInt();
      Duration = r.GetFloat(); Modifier = r.GetFloat(); Interval = r.GetFloat(); }
}

public class MeleeWorldHitPacket : Packet
{
    public override PacketType Type => PacketType.MeleeWorldHit;
    public int PlayerId { get; set; }
    public byte HitType { get; set; }
    public float X, Y, Z, Damage;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(HitType); w.Put(X); w.Put(Y); w.Put(Z); w.Put(Damage); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); HitType = r.GetByte(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); Damage = r.GetFloat(); }
}

public class FiredWeaponPacket : Packet
{
    public override PacketType Type => PacketType.FiredWeapon;
    public int PlayerId { get; set; }
    public string ItemType { get; set; } = "";
    public float AimY { get; set; }
    public float X, Y, Z;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ItemType ?? ""); w.Put(AimY); w.Put(X); w.Put(Y); w.Put(Z); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ItemType = r.GetString(); AimY = r.GetFloat(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); }
}

public class PlayerDiedPacket : Packet
{
    public override PacketType Type => PacketType.PlayerDied;
    public int PlayerId { get; set; }
    public bool IsNight { get; set; }
    public float X, Y, Z;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(IsNight); w.Put(X); w.Put(Y); w.Put(Z); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); IsNight = r.GetBool(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); }
}

public class PlayerDeathClockPacket : Packet
{
    public override PacketType Type => PacketType.PlayerDeathClock;
    public int PlayerId { get; set; }
    public int Day { get; set; }
    public float GameTime { get; set; }
    public int CurrentTime { get; set; }
    public bool LocalNightDeath { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Day); w.Put(GameTime); w.Put(CurrentTime); w.Put(LocalNightDeath); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Day = r.GetInt(); GameTime = r.GetFloat(); CurrentTime = r.GetInt(); LocalNightDeath = r.GetBool(); }
}

public class InfectionSplatPacket : Packet
{
    public override PacketType Type => PacketType.InfectionSplat;
    public int PlayerId { get; set; }
    public bool Spawn { get; set; } // false = gone
    public float X, Y, Z;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Spawn); w.Put(X); w.Put(Y); w.Put(Z); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Spawn = r.GetBool(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); }
}

public class EntitySoundPacket : Packet
{
    public override PacketType Type => PacketType.EntitySound;
    public int PlayerId { get; set; }
    public short EntityId { get; set; }
    public byte SoundType { get; set; }
    public string LoopName { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(EntityId); w.Put(SoundType); w.Put(LoopName ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); EntityId = r.GetShort(); SoundType = r.GetByte(); LoopName = r.GetString(); }
}

public class PlayerAudioPacket : Packet
{
    public override PacketType Type => PacketType.PlayerAudio;
    public int PlayerId { get; set; }
    public string AudioId { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(AudioId ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); AudioId = r.GetString(); }
}

public class PlayerNoisePacket : Packet
{
    public override PacketType Type => PacketType.PlayerNoise;
    public int PlayerId { get; set; }
    public string Kind { get; set; } = "";
    public float Range { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Kind ?? ""); w.Put(Range); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Kind = r.GetString(); Range = r.GetFloat(); }
}

public class BulletFxPacket : Packet
{
    public override PacketType Type => PacketType.BulletFx;
    public int PlayerId { get; set; }
    public string Pool { get; set; } = "";
    public string Prefab { get; set; } = "";
    public float X, Y, Z, Rx, Ry, Rz;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Pool ?? ""); w.Put(Prefab ?? "");
      w.Put(X); w.Put(Y); w.Put(Z); w.Put(Rx); w.Put(Ry); w.Put(Rz); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Pool = r.GetString(); Prefab = r.GetString();
      X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat();
      Rx = r.GetFloat(); Ry = r.GetFloat(); Rz = r.GetFloat(); }
}

public class ItemDisarmPacket : Packet
{
    public override PacketType Type => PacketType.ItemDisarm;
    public int PlayerId { get; set; }
    public string ItemId { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ItemId ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ItemId = r.GetString(); }
}

public class TrapFirePacket : Packet
{
    public override PacketType Type => PacketType.TrapFire;
    public int PlayerId { get; set; }
    public string ItemId { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ItemId ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ItemId = r.GetString(); }
}

public class ThrownItemPacket : Packet
{
    public override PacketType Type => PacketType.ThrownItem;
    public int PlayerId { get; set; }
    public string SyncId { get; set; } = "";
    public float X, Y, Z;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(SyncId ?? ""); w.Put(X); w.Put(Y); w.Put(Z); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); SyncId = r.GetString(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); }
}

public class ThrownArmedPacket : Packet
{
    public override PacketType Type => PacketType.ThrownArmed;
    public int PlayerId { get; set; }
    public string SyncId { get; set; } = "";
    public string ItemType { get; set; } = "";
    public bool Flaming { get; set; }
    public float Tx, Ty, Tz, Ox, Oy, Oz;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(SyncId ?? ""); w.Put(ItemType ?? ""); w.Put(Flaming);
      w.Put(Tx); w.Put(Ty); w.Put(Tz); w.Put(Ox); w.Put(Oy); w.Put(Oz); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); SyncId = r.GetString(); ItemType = r.GetString(); Flaming = r.GetBool();
      Tx = r.GetFloat(); Ty = r.GetFloat(); Tz = r.GetFloat();
      Ox = r.GetFloat(); Oy = r.GetFloat(); Oz = r.GetFloat(); }
}

public class ItemLightPacket : Packet
{
    public override PacketType Type => PacketType.ItemLight;
    public int PlayerId { get; set; }
    public string ItemType { get; set; } = "";
    public bool Active { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ItemType ?? ""); w.Put(Active); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ItemType = r.GetString(); Active = r.GetBool(); }
}

public class FlarePosPacket : Packet
{
    public override PacketType Type => PacketType.FlarePos;
    public int PlayerId { get; set; }
    public int InstanceId { get; set; }
    public float X, Y, Z, Life;

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(InstanceId); w.Put(X); w.Put(Y); w.Put(Z); w.Put(Life); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); InstanceId = r.GetInt(); X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); Life = r.GetFloat(); }
}

public class WorldLockPacket : Packet
{
    public const byte KindUnlock = 0;
    public const byte KindDoorLock = 1;
    public const byte KindPadUnlock = 2;

    public override PacketType Type => PacketType.WorldLock;
    public int PlayerId { get; set; }
    public byte Kind { get; set; }
    public string ObjectId { get; set; } = "";
    public string KeyType { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Kind); w.Put(ObjectId ?? ""); w.Put(KeyType ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Kind = r.GetByte(); ObjectId = r.GetString(); KeyType = r.GetString(); }
}

public class StationSawFuelPacket : Packet
{
    public override PacketType Type => PacketType.StationSawFuel;
    public int PlayerId { get; set; }
    public string ObjectId { get; set; } = "";
    public float Fuel { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ObjectId ?? ""); w.Put(Fuel); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ObjectId = r.GetString(); Fuel = r.GetFloat(); }
}

public class StationFeederPacket : Packet
{
    public override PacketType Type => PacketType.StationFeeder;
    public int PlayerId { get; set; }
    public string ObjectId { get; set; } = "";
    public string Payload { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ObjectId ?? ""); w.Put(Payload ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ObjectId = r.GetString(); Payload = r.GetString(); }
}

public class StationLurePacket : Packet
{
    public override PacketType Type => PacketType.StationLure;
    public int PlayerId { get; set; }
    public string ObjectId { get; set; } = "";
    public float Health { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ObjectId ?? ""); w.Put(Health); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ObjectId = r.GetString(); Health = r.GetFloat(); }
}

public class OxygenConvertPacket : Packet
{
    public override PacketType Type => PacketType.OxygenConvert;
    public int PlayerId { get; set; }

    public override void Serialize(NetDataWriter w) => w.Put(PlayerId);
    public override void Deserialize(NetDataReader r) => PlayerId = r.GetInt();
}

public class GeneratorFuelPacket : Packet
{
    public override PacketType Type => PacketType.GeneratorFuel;
    public int PlayerId { get; set; }
    public string ObjectId { get; set; } = "";
    public float Fuel { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ObjectId ?? ""); w.Put(Fuel); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ObjectId = r.GetString(); Fuel = r.GetFloat(); }
}

public class ClockSyncPacket : Packet
{
    public override PacketType Type => PacketType.ClockSync;
    public int PlayerId { get; set; }
    public int Day { get; set; }
    public float GameTime { get; set; }
    public int CurrentTime { get; set; }
    public bool AfterNight { get; set; }
    public bool HasAfterNight { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Day); w.Put(GameTime); w.Put(CurrentTime); w.Put(AfterNight); w.Put(HasAfterNight); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Day = r.GetInt(); GameTime = r.GetFloat(); CurrentTime = r.GetInt();
      AfterNight = r.GetBool(); HasAfterNight = r.GetBool(); }
}

public class GameTimeSyncPacket : Packet
{
    public override PacketType Type => PacketType.GameTimeSync;
    public int PlayerId { get; set; }
    public float GameTime { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(GameTime); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); GameTime = r.GetFloat(); }
}

public class WorkbenchLevelPacket : Packet
{
    public override PacketType Type => PacketType.WorkbenchLevel;
    public int PlayerId { get; set; }
    public int Level { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Level); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Level = r.GetInt(); }
}

public class WeatherStatePacket : Packet
{
    public override PacketType Type => PacketType.WeatherState;
    public int PlayerId { get; set; }
    /// <summary>Legacy weather body without "weather:" prefix.</summary>
    public string Payload { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Payload ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Payload = r.GetString(); }
}

public class ScenarioStatePacket : Packet
{
    public override PacketType Type => PacketType.ScenarioState;
    public int PlayerId { get; set; }
    public string ScenarioName { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(ScenarioName ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); ScenarioName = r.GetString(); }
}

public class ScenarioEventPacket : Packet
{
    public override PacketType Type => PacketType.ScenarioEvent;
    public int PlayerId { get; set; }
    public string NightName { get; set; } = "";
    public int EventIndex { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(NightName ?? ""); w.Put(EventIndex); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); NightName = r.GetString(); EventIndex = r.GetInt(); }
}

public class PlayerEffectPacket : Packet
{
    public override PacketType Type => PacketType.PlayerEffect;
    public int PlayerId { get; set; }
    public int TargetPlayerId { get; set; }
    public int Flags { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(TargetPlayerId); w.Put(Flags); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); TargetPlayerId = r.GetInt(); Flags = r.GetInt(); }
}

public class ShadowStatePacket : Packet
{
    public override PacketType Type => PacketType.ShadowState;
    public int PlayerId { get; set; }
    public string InstanceId { get; set; } = "";
    public int ShadowType { get; set; }
    public float X, Y, Z, Ry;
    public bool Dead { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(InstanceId ?? ""); w.Put(ShadowType); w.Put(X); w.Put(Y); w.Put(Z); w.Put(Ry); w.Put(Dead); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); InstanceId = r.GetString(); ShadowType = r.GetInt();
      X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat(); Ry = r.GetFloat(); Dead = r.GetBool(); }
}

public class LocationNpcPacket : Packet
{
    public override PacketType Type => PacketType.LocationNpc;
    public int PlayerId { get; set; }
    public string LocationId { get; set; } = "";
    public string NpcToken { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(LocationId ?? ""); w.Put(NpcToken ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); LocationId = r.GetString(); NpcToken = r.GetString(); }
}

public class GameEventFirePacket : Packet
{
    public override PacketType Type => PacketType.GameEventFire;
    public int PlayerId { get; set; }
    public string EventId { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(EventId ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); EventId = r.GetString(); }
}

public class WorldSeedPacket : Packet
{
    public override PacketType Type => PacketType.WorldSeed;
    public int PlayerId { get; set; }
    public int Seed { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Seed); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Seed = r.GetInt(); }
}

public class WorldSeedAuthPacket : Packet
{
    public override PacketType Type => PacketType.WorldSeedAuth;
    public int PlayerId { get; set; }
    public int Seed { get; set; }

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Seed); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Seed = r.GetInt(); }
}

public class SyncCheckDigestPacket : Packet
{
    public override PacketType Type => PacketType.SyncCheckDigest;
    public int PlayerId { get; set; }
    public string Digest { get; set; } = "";

    public override void Serialize(NetDataWriter w)
    { w.Put(PlayerId); w.Put(Digest ?? ""); }
    public override void Deserialize(NetDataReader r)
    { PlayerId = r.GetInt(); Digest = r.GetString(); }
}
