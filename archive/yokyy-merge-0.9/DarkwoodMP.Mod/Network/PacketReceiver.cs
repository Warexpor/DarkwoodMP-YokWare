using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using DarkwoodMP.Packets;

namespace DarkwoodMP.Network;

/// <summary>
/// Central packet dispatcher - routes incoming packets to registered handlers
/// </summary>
public class PacketReceiver
{
    private readonly Dictionary<PacketType, Func<NetDataReader, Packet>> _deserializers = new();
    private readonly Dictionary<PacketType, Action<Packet>> _handlers = new();

    public PacketReceiver()
    {
        // Register default deserializers
        _deserializers[PacketType.ConnectRequest] = r =>
        {
            var p = new ConnectRequestPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.ConnectResponse] = r =>
        {
            var p = new ConnectResponsePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.PositionUpdate] = r =>
        {
            var p = new PositionUpdatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.HealthUpdate] = r =>
        {
            var p = new HealthUpdatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.EnemyUpdate] = r =>
        {
            var p = new EnemyUpdatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.DoorState] = r =>
        {
            var p = new DoorStatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.PickupState] = r =>
        {
            var p = new PickupStatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.EnvironmentEffect] = r =>
        {
            var p = new EnvironmentEffectPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.GameStateSync] = r =>
        {
            var p = new GameStateSyncPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.DayNightUpdate] = r =>
        {
            var p = new DayNightUpdatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.EventTrigger] = r =>
        {
            var p = new EventTriggerPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.ChatMessage] = r =>
        {
            var p = new ChatMessagePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.SystemMessage] = r =>
        {
            var p = new SystemMessagePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.PlayerList] = r =>
        {
            var p = new PlayerListPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.Heartbeat] = r =>
        {
            var p = new HeartbeatPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.HeartbeatAck] = r =>
        {
            var p = new HeartbeatAckPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.GameStateRequest] = r =>
        {
            var p = new GameStateRequestPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.DamageUpdate] = r =>
        {
            var p = new DamageUpdatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.InteractiveState] = r =>
        {
            var p = new InteractiveStatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.ObjectMove] = r =>
        {
            var p = new ObjectMovePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.PlayerAnim] = r =>
        {
            var p = new PlayerAnimPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.EntityState] = r =>
        {
            var p = new EntityStatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.WorldRequest] = r =>
        {
            var p = new WorldRequestPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.WorldOffer] = r =>
        {
            var p = new WorldOfferPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.WorldChunk] = r =>
        {
            var p = new WorldChunkPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.WorldEnd] = r =>
        {
            var p = new WorldEndPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.TradeInventory] = r =>
        {
            var p = new TradeInventoryPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.ClientStateBackupChunk] = r =>
        {
            var p = new ClientStateBackupChunkPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.SaveBeat] = r =>
        {
            var p = new SaveBeatPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.LocationEnter] = r =>
        {
            var p = new LocationEnterPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.LocationExit] = r =>
        {
            var p = new LocationExitPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.MapMarker] = r =>
        {
            var p = new MapMarkerPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.MapDiscover] = r =>
        {
            var p = new MapDiscoverPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.Reputation] = r =>
        {
            var p = new ReputationPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.ReputationBulk] = r =>
        {
            var p = new ReputationBulkPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.HideoutState] = r =>
        {
            var p = new HideoutStatePacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.JournalSync] = r =>
        {
            var p = new JournalSyncPacket(); p.Deserialize(r); return p;
        };
        _deserializers[PacketType.DreamPrepare] = r => { var p = new DreamPreparePacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.DreamStart] = r => { var p = new DreamStartPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.DreamEntered] = r => { var p = new DreamEnteredPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.DreamEnd] = r => { var p = new DreamEndPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.DreamAudio] = r => { var p = new DreamAudioPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.DreamDoor] = r => { var p = new DreamDoorPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.FinalDreamDeath] = r => { var p = new FinalDreamDeathPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.CutsceneSync] = r => { var p = new CutsceneSyncPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.ChapterTransition] = r => { var p = new ChapterTransitionPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.ChapterNotify] = r => { var p = new ChapterNotifyPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.SceneLoad] = r => { var p = new SceneLoadPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.ExamineRequest] = r => { var p = new ExamineRequestPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.ExamineState] = r => { var p = new ExamineStatePacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.ContainerState] = r => { var p = new ContainerStatePacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.ContainerRequest] = r => { var p = new ContainerRequestPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.DeathDropSpawn] = r => { var p = new DeathDropSpawnPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.BarricadeState] = r => { var p = new BarricadeStatePacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.BuildPlaced] = r => { var p = new BuildPlacedPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.BuildConstruct] = r => { var p = new BuildConstructPacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.VaultState] = r => { var p = new VaultStatePacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.WorldObjectGone] = r => { var p = new WorldObjectGonePacket(); p.Deserialize(r); return p; };
        _deserializers[PacketType.InteractionLockSync] = r => { var p = new InteractionLockSyncPacket(); p.Deserialize(r); return p; };
        // Wave F residuals
        void Reg<T>(PacketType t) where T : Packet, new() =>
            _deserializers[t] = r => { var p = new T(); p.Deserialize(r); return p; };
        Reg<FlagDeltaPacket>(PacketType.FlagDelta);
        Reg<DialogStatePacket>(PacketType.DialogState);
        Reg<NpcConvStatePacket>(PacketType.NpcConvState);
        Reg<DialogOutcomePacket>(PacketType.DialogOutcome);
        Reg<GasTrailPacket>(PacketType.GasTrail);
        Reg<BurnLiquidPacket>(PacketType.BurnLiquid);
        Reg<PvpDamagePacket>(PacketType.PvpDamage);
        Reg<EntityAttackPacket>(PacketType.EntityAttack);
        Reg<PvpFxPacket>(PacketType.PvpFx);
        Reg<MeleeWorldHitPacket>(PacketType.MeleeWorldHit);
        Reg<FiredWeaponPacket>(PacketType.FiredWeapon);
        Reg<PlayerDiedPacket>(PacketType.PlayerDied);
        Reg<PlayerDeathClockPacket>(PacketType.PlayerDeathClock);
        Reg<InfectionSplatPacket>(PacketType.InfectionSplat);
        Reg<EntitySoundPacket>(PacketType.EntitySound);
        Reg<PlayerAudioPacket>(PacketType.PlayerAudio);
        Reg<PlayerNoisePacket>(PacketType.PlayerNoise);
        Reg<BulletFxPacket>(PacketType.BulletFx);
        Reg<ItemDisarmPacket>(PacketType.ItemDisarm);
        Reg<TrapFirePacket>(PacketType.TrapFire);
        Reg<ThrownItemPacket>(PacketType.ThrownItem);
        Reg<ThrownArmedPacket>(PacketType.ThrownArmed);
        Reg<ItemLightPacket>(PacketType.ItemLight);
        Reg<FlarePosPacket>(PacketType.FlarePos);
        Reg<WorldLockPacket>(PacketType.WorldLock);
        Reg<StationSawFuelPacket>(PacketType.StationSawFuel);
        Reg<StationFeederPacket>(PacketType.StationFeeder);
        Reg<StationLurePacket>(PacketType.StationLure);
        Reg<OxygenConvertPacket>(PacketType.OxygenConvert);
        Reg<GeneratorFuelPacket>(PacketType.GeneratorFuel);
        Reg<ClockSyncPacket>(PacketType.ClockSync);
        Reg<GameTimeSyncPacket>(PacketType.GameTimeSync);
        Reg<WorkbenchLevelPacket>(PacketType.WorkbenchLevel);
        Reg<WeatherStatePacket>(PacketType.WeatherState);
        Reg<ScenarioStatePacket>(PacketType.ScenarioState);
        Reg<ScenarioEventPacket>(PacketType.ScenarioEvent);
        Reg<PlayerEffectPacket>(PacketType.PlayerEffect);
        Reg<ShadowStatePacket>(PacketType.ShadowState);
        Reg<LocationNpcPacket>(PacketType.LocationNpc);
        Reg<GameEventFirePacket>(PacketType.GameEventFire);
        Reg<WorldSeedPacket>(PacketType.WorldSeed);
        Reg<WorldSeedAuthPacket>(PacketType.WorldSeedAuth);
        Reg<SyncCheckDigestPacket>(PacketType.SyncCheckDigest);
        Reg<PhysicsStateBatchPacket>(PacketType.PhysicsStateBatch);
        Reg<EntityBurningPacket>(PacketType.EntityBurning);
        Reg<PlayerBurningPacket>(PacketType.PlayerBurning);
        Reg<ExplosionSpawnObjectPacket>(PacketType.ExplosionSpawnObject);
        Reg<EntitySpawnPacket>(PacketType.EntitySpawn);
        Reg<LiquidStopBurnPacket>(PacketType.LiquidStopBurn);
    }

    public void RegisterHandler(PacketType type, Action<Packet> handler)
    {
        _handlers[type] = handler;
    }

    public void HandleData(byte[] data)
    {
        try
        {
            if (data == null || data.Length < 2) return;
            var reader = new NetDataReader(data);
            // Ironbark v2: MessageId u16 LE
            var messageId = reader.GetUShort();
            var type = (PacketType)messageId;

            if (_deserializers.TryGetValue(type, out var factory))
            {
                var packet = factory(reader);
                if (_handlers.TryGetValue(type, out var handler))
                    handler(packet);
            }
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PacketReceiver] Error handling packet: {ex.Message}");
        }
    }
}
