using DarkwoodMP.Packets;
using LiteNetLib.Utils;

namespace DarkwoodMP.Server.Services;

public class PacketRegistryService
{
    private readonly Dictionary<byte, Func<NetDataReader, Packet>> _deserializers = new();

    public PacketRegistryService()
    {
        Register<ConnectRequestPacket>((byte)PacketType.ConnectRequest);
        Register<ConnectResponsePacket>((byte)PacketType.ConnectResponse);
        Register<PlayerJoinedPacket>((byte)PacketType.PlayerJoined);
        Register<PlayerLeftPacket>((byte)PacketType.PlayerLeft);
        Register<PositionUpdatePacket>((byte)PacketType.PositionUpdate);
        Register<HealthUpdatePacket>((byte)PacketType.HealthUpdate);
        Register<InventoryUpdatePacket>((byte)PacketType.InventoryUpdate);

        Register<EnemyUpdatePacket>((byte)PacketType.EnemyUpdate);
        Register<DoorStatePacket>((byte)PacketType.DoorState);
        Register<PickupStatePacket>((byte)PacketType.PickupState);
        Register<EnvironmentEffectPacket>((byte)PacketType.EnvironmentEffect);
        Register<GameStateSyncPacket>((byte)PacketType.GameStateSync);
        Register<DayNightUpdatePacket>((byte)PacketType.DayNightUpdate);
        Register<EventTriggerPacket>((byte)PacketType.EventTrigger);
        Register<DamageUpdatePacket>((byte)PacketType.DamageUpdate);
        Register<InteractiveStatePacket>((byte)PacketType.InteractiveState);
        Register<ObjectMovePacket>((byte)PacketType.ObjectMove);
        Register<PlayerAnimPacket>((byte)PacketType.PlayerAnim);
        Register<ChatMessagePacket>((byte)PacketType.ChatMessage);
        Register<SystemMessagePacket>((byte)PacketType.SystemMessage);
        Register<PlayerListPacket>((byte)PacketType.PlayerList);
        Register<ServerCommandPacket>((byte)PacketType.ServerCommand);
        Register<HeartbeatPacket>((byte)PacketType.Heartbeat);
        Register<HeartbeatAckPacket>((byte)PacketType.HeartbeatAck);
        Register<GameStateRequestPacket>((byte)PacketType.GameStateRequest);
        Register<WorldRequestPacket>((byte)PacketType.WorldRequest);
        Register<WorldOfferPacket>((byte)PacketType.WorldOffer);
        Register<WorldChunkPacket>((byte)PacketType.WorldChunk);
        Register<WorldEndPacket>((byte)PacketType.WorldEnd);
        Register<TradeInventoryPacket>((byte)PacketType.TradeInventory);
        Register<ClientStateBackupChunkPacket>((byte)PacketType.ClientStateBackupChunk);
        Register<SaveBeatPacket>((byte)PacketType.SaveBeat);
        Register<LocationEnterPacket>((byte)PacketType.LocationEnter);
        Register<LocationExitPacket>((byte)PacketType.LocationExit);
        Register<MapMarkerPacket>((byte)PacketType.MapMarker);
        Register<MapDiscoverPacket>((byte)PacketType.MapDiscover);
        Register<ReputationPacket>((byte)PacketType.Reputation);
        Register<ReputationBulkPacket>((byte)PacketType.ReputationBulk);
        Register<HideoutStatePacket>((byte)PacketType.HideoutState);
        Register<JournalSyncPacket>((byte)PacketType.JournalSync);
        Register<DreamPreparePacket>((byte)PacketType.DreamPrepare);
        Register<DreamStartPacket>((byte)PacketType.DreamStart);
        Register<DreamEnteredPacket>((byte)PacketType.DreamEntered);
        Register<DreamEndPacket>((byte)PacketType.DreamEnd);
        Register<DreamAudioPacket>((byte)PacketType.DreamAudio);
        Register<DreamDoorPacket>((byte)PacketType.DreamDoor);
        Register<FinalDreamDeathPacket>((byte)PacketType.FinalDreamDeath);
        Register<CutsceneSyncPacket>((byte)PacketType.CutsceneSync);
        Register<ChapterTransitionPacket>((byte)PacketType.ChapterTransition);
        Register<ChapterNotifyPacket>((byte)PacketType.ChapterNotify);
        Register<SceneLoadPacket>((byte)PacketType.SceneLoad);
        Register<ExamineRequestPacket>((byte)PacketType.ExamineRequest);
        Register<ExamineStatePacket>((byte)PacketType.ExamineState);
        Register<ContainerStatePacket>((byte)PacketType.ContainerState);
        Register<ContainerRequestPacket>((byte)PacketType.ContainerRequest);
        Register<DeathDropSpawnPacket>((byte)PacketType.DeathDropSpawn);
        Register<BarricadeStatePacket>((byte)PacketType.BarricadeState);
        Register<BuildPlacedPacket>((byte)PacketType.BuildPlaced);
        Register<BuildConstructPacket>((byte)PacketType.BuildConstruct);
        Register<VaultStatePacket>((byte)PacketType.VaultState);
        Register<WorldObjectGonePacket>((byte)PacketType.WorldObjectGone);
        Register<InteractionLockSyncPacket>((byte)PacketType.InteractionLockSync);
        Register<FlagDeltaPacket>((byte)PacketType.FlagDelta);
        Register<DialogStatePacket>((byte)PacketType.DialogState);
        Register<NpcConvStatePacket>((byte)PacketType.NpcConvState);
        Register<DialogOutcomePacket>((byte)PacketType.DialogOutcome);
        Register<GasTrailPacket>((byte)PacketType.GasTrail);
        Register<BurnLiquidPacket>((byte)PacketType.BurnLiquid);
        Register<PvpDamagePacket>((byte)PacketType.PvpDamage);
        Register<EntityAttackPacket>((byte)PacketType.EntityAttack);
        Register<PvpFxPacket>((byte)PacketType.PvpFx);
        Register<MeleeWorldHitPacket>((byte)PacketType.MeleeWorldHit);
        Register<FiredWeaponPacket>((byte)PacketType.FiredWeapon);
        Register<PlayerDiedPacket>((byte)PacketType.PlayerDied);
        Register<PlayerDeathClockPacket>((byte)PacketType.PlayerDeathClock);
        Register<InfectionSplatPacket>((byte)PacketType.InfectionSplat);
        Register<EntitySoundPacket>((byte)PacketType.EntitySound);
        Register<PlayerAudioPacket>((byte)PacketType.PlayerAudio);
        Register<PlayerNoisePacket>((byte)PacketType.PlayerNoise);
        Register<BulletFxPacket>((byte)PacketType.BulletFx);
        Register<ItemDisarmPacket>((byte)PacketType.ItemDisarm);
        Register<TrapFirePacket>((byte)PacketType.TrapFire);
        Register<ThrownItemPacket>((byte)PacketType.ThrownItem);
        Register<ThrownArmedPacket>((byte)PacketType.ThrownArmed);
        Register<ItemLightPacket>((byte)PacketType.ItemLight);
        Register<FlarePosPacket>((byte)PacketType.FlarePos);
        Register<WorldLockPacket>((byte)PacketType.WorldLock);
        Register<StationSawFuelPacket>((byte)PacketType.StationSawFuel);
        Register<StationFeederPacket>((byte)PacketType.StationFeeder);
        Register<StationLurePacket>((byte)PacketType.StationLure);
        Register<OxygenConvertPacket>((byte)PacketType.OxygenConvert);
        Register<GeneratorFuelPacket>((byte)PacketType.GeneratorFuel);
        Register<ClockSyncPacket>((byte)PacketType.ClockSync);
        Register<GameTimeSyncPacket>((byte)PacketType.GameTimeSync);
        Register<WorkbenchLevelPacket>((byte)PacketType.WorkbenchLevel);
        Register<WeatherStatePacket>((byte)PacketType.WeatherState);
        Register<ScenarioStatePacket>((byte)PacketType.ScenarioState);
        Register<ScenarioEventPacket>((byte)PacketType.ScenarioEvent);
        Register<PlayerEffectPacket>((byte)PacketType.PlayerEffect);
        Register<ShadowStatePacket>((byte)PacketType.ShadowState);
        Register<LocationNpcPacket>((byte)PacketType.LocationNpc);
        Register<GameEventFirePacket>((byte)PacketType.GameEventFire);
        Register<WorldSeedPacket>((byte)PacketType.WorldSeed);
        Register<WorldSeedAuthPacket>((byte)PacketType.WorldSeedAuth);
        Register<SyncCheckDigestPacket>((byte)PacketType.SyncCheckDigest);
        Register<PhysicsStateBatchPacket>((byte)PacketType.PhysicsStateBatch);
        Register<EntityBurningPacket>((byte)PacketType.EntityBurning);
        Register<PlayerBurningPacket>((byte)PacketType.PlayerBurning);
        Register<ExplosionSpawnObjectPacket>((byte)PacketType.ExplosionSpawnObject);
        Register<EntitySpawnPacket>((byte)PacketType.EntitySpawn);
        Register<LiquidStopBurnPacket>((byte)PacketType.LiquidStopBurn);
    }

    private void Register<T>(byte typeId) where T : Packet, new()
    {
        _deserializers[typeId] = reader =>
        {
            var packet = new T();
            packet.Deserialize(reader);
            return packet;
        };
    }

    public byte[] Serialize<T>(T packet) where T : Packet
    {
        // Ironbark v2: MessageId u16 LE + payload
        var writer = new NetDataWriter();
        writer.Put(packet.MessageId);
        packet.Serialize(writer);
        var data = new byte[writer.Length];
        Array.Copy(writer.Data, data, writer.Length);
        return data;
    }

    public Packet? Deserialize(PacketType type, NetDataReader reader)
    {
        if (_deserializers.TryGetValue((byte)type, out var factory))
            return factory(reader);
        return null;
    }

    public void Handle(Packet packet, LiteNetLib.NetPeer peer)
    {
        // Placeholder for peer-based handling
    }
}
