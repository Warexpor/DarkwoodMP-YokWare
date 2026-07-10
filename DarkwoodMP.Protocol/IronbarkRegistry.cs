using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

/// <summary>
/// Ironbark v2 single registry: MessageId → reliability + forward + factory.
/// Shared by mod PacketReceiver and dedicated server.
/// </summary>
public static class IronbarkRegistry
{
    public sealed class Entry
    {
        public ushort MessageId;
        public string Name = "";
        public IronbarkReliability Reliability;
        public IronbarkForward Forward;
        public Func<Packet>? Factory;
    }

    private static readonly Dictionary<ushort, Entry> _byId = new Dictionary<ushort, Entry>();
    private static bool _init;

    public static void EnsureInit()
    {
        if (_init) return;
        _init = true;
        // Register all known packet types via factories
        void R<T>(PacketType t) where T : Packet, new()
        {
            var sample = new T();
            var id = sample.MessageId;
            _byId[id] = new Entry
            {
                MessageId = id,
                Name = typeof(T).Name,
                Reliability = IronbarkMeta.Reliability(t),
                Forward = IronbarkMeta.Forward(t),
                Factory = () => new T()
            };
        }

        R<ConnectRequestPacket>(PacketType.ConnectRequest);
        R<ConnectResponsePacket>(PacketType.ConnectResponse);
        R<PlayerJoinedPacket>(PacketType.PlayerJoined);
        R<PlayerLeftPacket>(PacketType.PlayerLeft);
        R<GameStateRequestPacket>(PacketType.GameStateRequest);
        R<PlayerListPacket>(PacketType.PlayerList);
        R<PositionUpdatePacket>(PacketType.PositionUpdate);
        R<HealthUpdatePacket>(PacketType.HealthUpdate);
        R<InventoryUpdatePacket>(PacketType.InventoryUpdate);
        R<EnemyUpdatePacket>(PacketType.EnemyUpdate);
        R<DoorStatePacket>(PacketType.DoorState);
        R<PickupStatePacket>(PacketType.PickupState);
        R<EnvironmentEffectPacket>(PacketType.EnvironmentEffect);
        R<GameStateSyncPacket>(PacketType.GameStateSync);
        R<DayNightUpdatePacket>(PacketType.DayNightUpdate);
        R<EventTriggerPacket>(PacketType.EventTrigger);
        R<ChatMessagePacket>(PacketType.ChatMessage);
        R<SystemMessagePacket>(PacketType.SystemMessage);
        R<ServerCommandPacket>(PacketType.ServerCommand);
        R<HeartbeatPacket>(PacketType.Heartbeat);
        R<HeartbeatAckPacket>(PacketType.HeartbeatAck);
        R<DamageUpdatePacket>(PacketType.DamageUpdate);
        R<InteractiveStatePacket>(PacketType.InteractiveState);
        R<ObjectMovePacket>(PacketType.ObjectMove);
        R<PlayerAnimPacket>(PacketType.PlayerAnim);
        R<EntityStatePacket>(PacketType.EntityState);
        R<WorldRequestPacket>(PacketType.WorldRequest);
        R<WorldOfferPacket>(PacketType.WorldOffer);
        R<WorldChunkPacket>(PacketType.WorldChunk);
        R<WorldEndPacket>(PacketType.WorldEnd);
        R<TradeInventoryPacket>(PacketType.TradeInventory);
        R<ClientStateBackupChunkPacket>(PacketType.ClientStateBackupChunk);
        R<SaveBeatPacket>(PacketType.SaveBeat);
        R<LocationEnterPacket>(PacketType.LocationEnter);
        R<LocationExitPacket>(PacketType.LocationExit);
        R<MapMarkerPacket>(PacketType.MapMarker);
        R<MapDiscoverPacket>(PacketType.MapDiscover);
        R<ReputationPacket>(PacketType.Reputation);
        R<ReputationBulkPacket>(PacketType.ReputationBulk);
        R<HideoutStatePacket>(PacketType.HideoutState);
        R<JournalSyncPacket>(PacketType.JournalSync);
        R<DreamPreparePacket>(PacketType.DreamPrepare);
        R<DreamStartPacket>(PacketType.DreamStart);
        R<DreamEnteredPacket>(PacketType.DreamEntered);
        R<DreamEndPacket>(PacketType.DreamEnd);
        R<DreamAudioPacket>(PacketType.DreamAudio);
        R<DreamDoorPacket>(PacketType.DreamDoor);
        R<FinalDreamDeathPacket>(PacketType.FinalDreamDeath);
        R<CutsceneSyncPacket>(PacketType.CutsceneSync);
        R<ChapterTransitionPacket>(PacketType.ChapterTransition);
        R<ChapterNotifyPacket>(PacketType.ChapterNotify);
        R<SceneLoadPacket>(PacketType.SceneLoad);
        R<ExamineRequestPacket>(PacketType.ExamineRequest);
        R<ExamineStatePacket>(PacketType.ExamineState);
        R<ContainerStatePacket>(PacketType.ContainerState);
        R<ContainerRequestPacket>(PacketType.ContainerRequest);
        R<DeathDropSpawnPacket>(PacketType.DeathDropSpawn);
        R<BarricadeStatePacket>(PacketType.BarricadeState);
        R<BuildPlacedPacket>(PacketType.BuildPlaced);
        R<BuildConstructPacket>(PacketType.BuildConstruct);
        R<VaultStatePacket>(PacketType.VaultState);
        R<WorldObjectGonePacket>(PacketType.WorldObjectGone);
        R<InteractionLockSyncPacket>(PacketType.InteractionLockSync);
        R<FlagDeltaPacket>(PacketType.FlagDelta);
        R<DialogStatePacket>(PacketType.DialogState);
        R<NpcConvStatePacket>(PacketType.NpcConvState);
        R<DialogOutcomePacket>(PacketType.DialogOutcome);
        R<GasTrailPacket>(PacketType.GasTrail);
        R<BurnLiquidPacket>(PacketType.BurnLiquid);
        R<PvpDamagePacket>(PacketType.PvpDamage);
        R<EntityAttackPacket>(PacketType.EntityAttack);
        R<PvpFxPacket>(PacketType.PvpFx);
        R<MeleeWorldHitPacket>(PacketType.MeleeWorldHit);
        R<FiredWeaponPacket>(PacketType.FiredWeapon);
        R<PlayerDiedPacket>(PacketType.PlayerDied);
        R<PlayerDeathClockPacket>(PacketType.PlayerDeathClock);
        R<InfectionSplatPacket>(PacketType.InfectionSplat);
        R<EntitySoundPacket>(PacketType.EntitySound);
        R<PlayerAudioPacket>(PacketType.PlayerAudio);
        R<PlayerNoisePacket>(PacketType.PlayerNoise);
        R<BulletFxPacket>(PacketType.BulletFx);
        R<ItemDisarmPacket>(PacketType.ItemDisarm);
        R<TrapFirePacket>(PacketType.TrapFire);
        R<ThrownItemPacket>(PacketType.ThrownItem);
        R<ThrownArmedPacket>(PacketType.ThrownArmed);
        R<ItemLightPacket>(PacketType.ItemLight);
        R<FlarePosPacket>(PacketType.FlarePos);
        R<WorldLockPacket>(PacketType.WorldLock);
        R<StationSawFuelPacket>(PacketType.StationSawFuel);
        R<StationFeederPacket>(PacketType.StationFeeder);
        R<StationLurePacket>(PacketType.StationLure);
        R<OxygenConvertPacket>(PacketType.OxygenConvert);
        R<GeneratorFuelPacket>(PacketType.GeneratorFuel);
        R<ClockSyncPacket>(PacketType.ClockSync);
        R<GameTimeSyncPacket>(PacketType.GameTimeSync);
        R<WorkbenchLevelPacket>(PacketType.WorkbenchLevel);
        R<WeatherStatePacket>(PacketType.WeatherState);
        R<ScenarioStatePacket>(PacketType.ScenarioState);
        R<ScenarioEventPacket>(PacketType.ScenarioEvent);
        R<PlayerEffectPacket>(PacketType.PlayerEffect);
        R<ShadowStatePacket>(PacketType.ShadowState);
        R<LocationNpcPacket>(PacketType.LocationNpc);
        R<GameEventFirePacket>(PacketType.GameEventFire);
        R<WorldSeedPacket>(PacketType.WorldSeed);
        R<WorldSeedAuthPacket>(PacketType.WorldSeedAuth);
        R<SyncCheckDigestPacket>(PacketType.SyncCheckDigest);
        R<PhysicsStateBatchPacket>(PacketType.PhysicsStateBatch);
        R<EntityBurningPacket>(PacketType.EntityBurning);
        R<PlayerBurningPacket>(PacketType.PlayerBurning);
        R<ExplosionSpawnObjectPacket>(PacketType.ExplosionSpawnObject);
        R<EntitySpawnPacket>(PacketType.EntitySpawn);
        R<LiquidStopBurnPacket>(PacketType.LiquidStopBurn);
    }

    public static bool TryGet(ushort messageId, out Entry? entry)
    {
        EnsureInit();
        if (_byId.TryGetValue(messageId, out var e))
        {
            entry = e;
            return true;
        }
        entry = null;
        return false;
    }

    public static Entry Get(PacketType type)
    {
        EnsureInit();
        if (_byId.TryGetValue((ushort)type, out var e)) return e;
        return new Entry
        {
            MessageId = (ushort)type,
            Name = type.ToString(),
            Reliability = IronbarkMeta.Reliability(type),
            Forward = IronbarkMeta.Forward(type),
            Factory = null
        };
    }

    public static bool IsCritical(ushort messageId)
    {
        if (TryGet(messageId, out var e) && e != null)
            return e.Reliability == IronbarkReliability.Critical;
        return IronbarkMeta.IsCritical((PacketType)(byte)messageId);
    }

    public static bool ShouldFanOut(ushort messageId)
    {
        if (TryGet(messageId, out var e) && e != null)
            return e.Forward != IronbarkForward.None;
        return IronbarkMeta.ShouldFanOut((PacketType)(byte)messageId);
    }

    public static Packet? CreateAndDeserialize(ushort messageId, NetDataReader reader)
    {
        EnsureInit();
        if (!_byId.TryGetValue(messageId, out var e) || e.Factory == null)
            return null;
        var p = e.Factory();
        p.Deserialize(reader);
        return p;
    }

    public static byte[] Encode(Packet packet)
    {
        var w = new NetDataWriter();
        w.Put(packet.MessageId);
        packet.Serialize(w);
        var data = new byte[w.Length];
        Buffer.BlockCopy(w.Data, 0, data, 0, w.Length);
        return data;
    }
}
