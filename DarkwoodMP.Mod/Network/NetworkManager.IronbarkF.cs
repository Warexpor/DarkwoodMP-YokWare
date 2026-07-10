using DarkwoodMP.GameLogic;
using DarkwoodMP.Packets;
using UnityEngine;

namespace DarkwoodMP.Network;

/// <summary>Ironbark wave F — residual typed packet receive handlers.</summary>
public partial class NetworkManager
{
    private void RegisterIronbarkFHandlers()
    {
        _packetReceiver.RegisterHandler(PacketType.FlagDelta, HandleFlagDelta);
        _packetReceiver.RegisterHandler(PacketType.DialogState, HandleDialogState);
        _packetReceiver.RegisterHandler(PacketType.NpcConvState, HandleNpcConvState);
        _packetReceiver.RegisterHandler(PacketType.DialogOutcome, HandleDialogOutcome);
        _packetReceiver.RegisterHandler(PacketType.GasTrail, HandleGasTrail);
        _packetReceiver.RegisterHandler(PacketType.BurnLiquid, HandleBurnLiquid);
        _packetReceiver.RegisterHandler(PacketType.PvpDamage, HandlePvpDamage);
        _packetReceiver.RegisterHandler(PacketType.EntityAttack, HandleEntityAttack);
        _packetReceiver.RegisterHandler(PacketType.PvpFx, HandlePvpFx);
        _packetReceiver.RegisterHandler(PacketType.MeleeWorldHit, HandleMeleeWorldHit);
        _packetReceiver.RegisterHandler(PacketType.FiredWeapon, HandleFiredWeapon);
        _packetReceiver.RegisterHandler(PacketType.PlayerDied, HandlePlayerDiedTyped);
        _packetReceiver.RegisterHandler(PacketType.PlayerDeathClock, HandlePlayerDeathClock);
        _packetReceiver.RegisterHandler(PacketType.InfectionSplat, HandleInfectionSplat);
        _packetReceiver.RegisterHandler(PacketType.EntitySound, HandleEntitySound);
        _packetReceiver.RegisterHandler(PacketType.PlayerAudio, HandlePlayerAudio);
        _packetReceiver.RegisterHandler(PacketType.PlayerNoise, HandlePlayerNoise);
        _packetReceiver.RegisterHandler(PacketType.BulletFx, HandleBulletFx);
        _packetReceiver.RegisterHandler(PacketType.ItemDisarm, HandleItemDisarm);
        _packetReceiver.RegisterHandler(PacketType.TrapFire, HandleTrapFire);
        _packetReceiver.RegisterHandler(PacketType.ThrownItem, HandleThrownItem);
        _packetReceiver.RegisterHandler(PacketType.ThrownArmed, HandleThrownArmed);
        _packetReceiver.RegisterHandler(PacketType.ItemLight, HandleItemLight);
        _packetReceiver.RegisterHandler(PacketType.FlarePos, HandleFlarePos);
        _packetReceiver.RegisterHandler(PacketType.WorldLock, HandleWorldLock);
        _packetReceiver.RegisterHandler(PacketType.StationSawFuel, HandleStationSawFuel);
        _packetReceiver.RegisterHandler(PacketType.StationFeeder, HandleStationFeeder);
        _packetReceiver.RegisterHandler(PacketType.StationLure, HandleStationLure);
        _packetReceiver.RegisterHandler(PacketType.OxygenConvert, HandleOxygenConvert);
        _packetReceiver.RegisterHandler(PacketType.GeneratorFuel, HandleGeneratorFuel);
        _packetReceiver.RegisterHandler(PacketType.ClockSync, HandleClockSync);
        _packetReceiver.RegisterHandler(PacketType.GameTimeSync, HandleGameTimeSync);
        _packetReceiver.RegisterHandler(PacketType.WorkbenchLevel, HandleWorkbenchLevel);
        _packetReceiver.RegisterHandler(PacketType.WeatherState, HandleWeatherState);
        _packetReceiver.RegisterHandler(PacketType.ScenarioState, HandleScenarioState);
        _packetReceiver.RegisterHandler(PacketType.ScenarioEvent, HandleScenarioEvent);
        _packetReceiver.RegisterHandler(PacketType.PlayerEffect, HandlePlayerEffect);
        _packetReceiver.RegisterHandler(PacketType.ShadowState, HandleShadowState);
        _packetReceiver.RegisterHandler(PacketType.LocationNpc, HandleLocationNpc);
        _packetReceiver.RegisterHandler(PacketType.GameEventFire, HandleGameEventFire);
        _packetReceiver.RegisterHandler(PacketType.WorldSeed, HandleWorldSeedTyped);
        _packetReceiver.RegisterHandler(PacketType.WorldSeedAuth, HandleWorldSeedAuthTyped);
        _packetReceiver.RegisterHandler(PacketType.SyncCheckDigest, HandleSyncCheckDigest);
        _packetReceiver.RegisterHandler(PacketType.EntityBurning, HandleEntityBurning);
        _packetReceiver.RegisterHandler(PacketType.PlayerBurning, HandlePlayerBurning);
        _packetReceiver.RegisterHandler(PacketType.ExplosionSpawnObject, HandleExplosionSpawnObject);
        _packetReceiver.RegisterHandler(PacketType.EntitySpawn, HandleEntitySpawn);
        _packetReceiver.RegisterHandler(PacketType.LiquidStopBurn, HandleLiquidStopBurn);
    }

    private void HandleFlagDelta(Packet packet)
    {
        if (packet is not FlagDeltaPacket p || p.PlayerId == LocalPlayerId) return;
        _storySync?.ApplyRemoteFlag(p.FlagName, p.IsInt, p.Value != 0, p.Value);
    }

    private void HandleDialogState(Packet packet)
    {
        if (packet is not DialogStatePacket p || p.PlayerId == LocalPlayerId) return;
        _dialogueSync?.ApplyRemote(p.Payload);
    }

    private void HandleNpcConvState(Packet packet)
    {
        if (packet is not NpcConvStatePacket p || p.PlayerId == LocalPlayerId) return;
        _dialogueSync?.ApplyRemoteNpcState(
            (p.WantsToTalk ? "1" : "0") + ":" + p.Reputation + ":" + p.NpcName);
    }

    private void HandleDialogOutcome(Packet packet)
    {
        if (packet is not DialogOutcomePacket p || p.PlayerId == LocalPlayerId) return;
        if (!IsTimeAuthority) return;
        Patches.DialogOutcome_Patch.ApplyRemoteOutcome(p.Payload);
    }

    private void HandleGasTrail(Packet packet)
    {
        if (packet is not GasTrailPacket p || p.PlayerId == LocalPlayerId) return;
        _buildSync?.OnRemoteGasTrail(new Vector3(p.X, p.Y, p.Z), new Quaternion(p.Rx, p.Ry, p.Rz, p.Rw));
    }

    private void HandleBurnLiquid(Packet packet)
    {
        if (packet is not BurnLiquidPacket p || p.PlayerId == LocalPlayerId) return;
        _buildSync?.OnRemoteBurnLiquid(new Vector3(p.X, 0f, p.Z));
    }

    private void HandlePvpDamage(Packet packet)
    {
        if (packet is not PvpDamagePacket p || p.PlayerId == LocalPlayerId) return;
        if (p.TargetPlayerId != LocalPlayerId) return;
        _damageSync?.ApplyRemotePlayerHit(p.PlayerId, p.Damage);
    }

    private void HandleEntityAttack(Packet packet)
    {
        if (packet is not EntityAttackPacket p || p.PlayerId == LocalPlayerId) return;
        if (!IsTimeAuthority) return;
        if (short.TryParse(p.EntityId, out var atkId))
            _enemySync?.ApplyRemoteAttack(atkId, p.Damage, p.Damage >= 80f, Vector3.zero);
    }

    private void HandlePvpFx(Packet packet)
    {
        if (packet is not PvpFxPacket p || p.PlayerId == LocalPlayerId) return;
        if (p.TargetPlayerId != LocalPlayerId) return;
        _damageSync?.ApplyRemoteEffect(p.EffectType, p.Duration, p.Modifier, p.Interval);
    }

    private void HandleMeleeWorldHit(Packet packet)
    {
        if (packet is not MeleeWorldHitPacket p || p.PlayerId == LocalPlayerId) return;
        if (!IsTimeAuthority) return;
        Patches.WorldMelee_Patch.ApplyRemote(p.HitType, new Vector3(p.X, p.Y, p.Z), p.Damage);
    }

    private void HandleFiredWeapon(Packet packet)
    {
        if (packet is not FiredWeaponPacket p || p.PlayerId == LocalPlayerId) return;
        _rangedSync?.OnRemoteFired(p.PlayerId, p.ItemType, p.AimY);
    }

    private void HandlePlayerDiedTyped(Packet packet)
    {
        if (packet is not PlayerDiedPacket p || p.PlayerId == LocalPlayerId) return;
        var deathPos = new Vector3(p.X, p.Y, p.Z);
        var isNight = p.IsNight;
        if (isNight)
            DeathStateTracker.OnRemoteNightDeath(p.PlayerId, deathPos);
        else
            DeathStateTracker.OnRemoteDayDeath(p.PlayerId);

        var proxyGo = GetRemotePlayer(p.PlayerId);
        var proxy = proxyGo != null ? proxyGo.GetComponent<RemotePlayerProxy>() : null;
        proxy?.ApplyDeathState(deathPos);

        if (DreamSession.IsActive || FinalDreamsceneManager.IsActive)
            FinalDreamsceneManager.OnRemoteDeathInDream(p.PlayerId);

        if (isNight)
            Patches.Death_Patch.TryBroadcastMorningIfAllDead();
    }

    private void HandlePlayerDeathClock(Packet packet)
    {
        if (packet is not PlayerDeathClockPacket p || p.PlayerId == LocalPlayerId) return;
        if (!DeathStateTracker.ShouldAdoptRemoteDeathClock(p.LocalNightDeath))
        {
            ModLogger.Msg("[DeathState] Ignoring remote death clock — night death hold still active");
            return;
        }
        _chatManager?.AddSystemMessage($"{GetPlayerName(p.PlayerId)} woke up at their hideout - time moves on.");
        _worldSync?.OnRemoteDeathTime(p.Day, p.GameTime, p.CurrentTime);
        NightSpectator.Exit();
        DeathStateTracker.Reset();
        if (_remotePlayers.TryGetValue(p.PlayerId, out var deadGo))
            deadGo.GetComponent<RemotePlayerProxy>()?.ClearDeathState();
    }

    private void HandleInfectionSplat(Packet packet)
    {
        if (packet is not InfectionSplatPacket p || p.PlayerId == LocalPlayerId) return;
        var pos = new Vector3(p.X, p.Y, p.Z);
        if (p.Spawn) Patches.Infection_Patch.ApplyRemoteSpawn(pos);
        else Patches.Infection_Patch.ApplyRemoteGone(pos);
    }

    private void HandleEntitySound(Packet packet)
    {
        if (packet is not EntitySoundPacket p || p.PlayerId == LocalPlayerId) return;
        Patches.EntitySound_Patch.ApplyRemote(p.EntityId, p.SoundType, p.LoopName);
    }

    private void HandlePlayerAudio(Packet packet)
    {
        if (packet is not PlayerAudioPacket p || p.PlayerId == LocalPlayerId) return;
        var proxy = GetRemotePlayer(p.PlayerId);
        if (proxy == null || string.IsNullOrEmpty(p.AudioId)) return;
        var localT = _playerSync?.LocalPlayerTransform;
        if (localT != null && (localT.position - proxy.transform.position).sqrMagnitude > 500f * 500f) return;
        RemoteApply.Active = true;
        try
        {
            var audioObj = AudioController.Play(p.AudioId, proxy.transform.position, proxy.transform);
            if (audioObj != null && audioObj.primaryAudioSource != null)
            {
                var src = audioObj.primaryAudioSource;
                src.spatialBlend = 1f;
                src.minDistance = Mathf.Max(src.minDistance, 30f);
                src.maxDistance = Mathf.Max(src.maxDistance, 200f);
                src.rolloffMode = AudioRolloffMode.Linear;
            }
        }
        finally { RemoteApply.Active = false; }
    }

    private void HandlePlayerNoise(Packet packet)
    {
        if (packet is not PlayerNoisePacket p || p.PlayerId == LocalPlayerId) return;
        Patches.PlayerNoise_Patch.ApplyRemoteNoise(p.PlayerId, p.Kind, p.Range);
    }

    private void HandleBulletFx(Packet packet)
    {
        if (packet is not BulletFxPacket p || p.PlayerId == LocalPlayerId) return;
        _rangedSync?.OnRemoteBulletFx(p.Pool, p.Prefab,
            new Vector3(p.X, p.Y, p.Z), new Vector3(p.Rx, p.Ry, p.Rz));
    }

    private void HandleItemDisarm(Packet packet)
    {
        if (packet is not ItemDisarmPacket p || p.PlayerId == LocalPlayerId) return;
        _itemSync?.OnRemoteDisarm(p.ItemId);
    }

    private void HandleTrapFire(Packet packet)
    {
        if (packet is not TrapFirePacket p || p.PlayerId == LocalPlayerId) return;
        _itemSync?.OnRemoteTrapFire(p.ItemId);
    }

    private void HandleThrownItem(Packet packet)
    {
        if (packet is not ThrownItemPacket p || p.PlayerId == LocalPlayerId) return;
        _itemSync?.OnRemoteThrow(p.SyncId, new Vector3(p.X, p.Y, p.Z), p.PlayerId);
    }

    private void HandleThrownArmed(Packet packet)
    {
        if (packet is not ThrownArmedPacket p || p.PlayerId == LocalPlayerId) return;
        _itemSync?.OnRemoteThrow2(p.SyncId, p.ItemType, p.Flaming,
            new Vector3(p.Tx, p.Ty, p.Tz), p.PlayerId, new Vector3(p.Ox, p.Oy, p.Oz));
    }

    private void HandleItemLight(Packet packet)
    {
        if (packet is not ItemLightPacket p || p.PlayerId == LocalPlayerId) return;
        _heldLightSync?.OnRemoteItemLight(p.PlayerId, p.ItemType, p.Active);
    }

    private void HandleFlarePos(Packet packet)
    {
        if (packet is not FlarePosPacket p || p.PlayerId == LocalPlayerId) return;
        _heldLightSync?.OnRemoteFlarePos(p.PlayerId, p.InstanceId, new Vector3(p.X, p.Y, p.Z), p.Life);
    }

    private void HandleWorldLock(Packet packet)
    {
        if (packet is not WorldLockPacket p || p.PlayerId == LocalPlayerId) return;
        switch (p.Kind)
        {
            case WorldLockPacket.KindDoorLock:
                _lockSync?.OnRemoteDoorLock(p.ObjectId, p.KeyType);
                break;
            case WorldLockPacket.KindPadUnlock:
                _lockSync?.OnRemotePadlockUnlock(p.ObjectId);
                break;
            default:
                _lockSync?.OnRemoteUnlock(p.ObjectId);
                break;
        }
    }

    private void HandleStationSawFuel(Packet packet)
    {
        if (packet is not StationSawFuelPacket p || p.PlayerId == LocalPlayerId) return;
        _stationSync?.OnRemoteSawFuel(p.ObjectId, p.Fuel);
    }

    private void HandleStationFeeder(Packet packet)
    {
        if (packet is not StationFeederPacket p || p.PlayerId == LocalPlayerId) return;
        _stationSync?.OnRemoteFeeder(p.ObjectId);
    }

    private void HandleStationLure(Packet packet)
    {
        if (packet is not StationLurePacket p || p.PlayerId == LocalPlayerId) return;
        _stationSync?.OnRemoteLure(p.ObjectId, (int)p.Health);
    }

    private void HandleOxygenConvert(Packet packet)
    {
        if (packet is not OxygenConvertPacket p || p.PlayerId == LocalPlayerId) return;
        Patches.Compressor_Patch.ApplyRemoteConvert();
    }

    private void HandleGeneratorFuel(Packet packet)
    {
        if (packet is not GeneratorFuelPacket p || p.PlayerId == LocalPlayerId) return;
        _interactiveSync?.OnRemoteGeneratorFuel(p.ObjectId, p.Fuel);
    }

    private void HandleClockSync(Packet packet)
    {
        if (packet is not ClockSyncPacket p || p.PlayerId == LocalPlayerId) return;
        bool? after = p.HasAfterNight ? (bool?)p.AfterNight : null;
        _worldSync?.OnRemoteClock(p.Day, p.GameTime, p.CurrentTime, after);
    }

    private void HandleGameTimeSync(Packet packet)
    {
        if (packet is not GameTimeSyncPacket p || p.PlayerId == LocalPlayerId) return;
        _worldSync?.OnRemoteGameTime(p.GameTime);
    }

    private void HandleWorkbenchLevel(Packet packet)
    {
        if (packet is not WorkbenchLevelPacket p || p.PlayerId == LocalPlayerId) return;
        _worldSync?.OnRemoteWorkbenchLevel(p.Level);
    }

    private void HandleWeatherState(Packet packet)
    {
        if (packet is not WeatherStatePacket p || p.PlayerId == LocalPlayerId) return;
        var body = p.Payload ?? "";
        if (body.StartsWith("weather:")) body = body.Substring("weather:".Length);
        _weatherSync?.OnRemoteWeather(body);
    }

    private void HandleScenarioState(Packet packet)
    {
        if (packet is not ScenarioStatePacket p || p.PlayerId == LocalPlayerId) return;
        if (IsTimeAuthority) return;
        Patches.Scenario_Patch.ApplyRemoteScenario(p.ScenarioName);
    }

    private void HandleScenarioEvent(Packet packet)
    {
        if (packet is not ScenarioEventPacket p || p.PlayerId == LocalPlayerId) return;
        Patches.ScenarioEvent_Patch.ApplyRemote(p.NightName, p.EventIndex);
    }

    private void HandlePlayerEffect(Packet packet)
    {
        if (packet is not PlayerEffectPacket p || p.PlayerId == LocalPlayerId) return;
        Patches.PlayerEffect_Patch.ApplyRemote(p.TargetPlayerId, p.Flags);
    }

    private void HandleShadowState(Packet packet)
    {
        if (packet is not ShadowStatePacket p || p.PlayerId == LocalPlayerId) return;
        _shadowSync?.OnRemoteShadow(p.PlayerId, p.InstanceId, p.ShadowType,
            new Vector3(p.X, p.Y, p.Z), p.Ry, p.Dead);
    }

    private void HandleLocationNpc(Packet packet)
    {
        if (packet is not LocationNpcPacket p || p.PlayerId == LocalPlayerId) return;
        _stationSync?.OnRemoteLocationNpc(p.LocationId, p.NpcToken);
    }

    private void HandleGameEventFire(Packet packet)
    {
        if (packet is not GameEventFirePacket p || p.PlayerId == LocalPlayerId) return;
        _eventStateSync?.OnRemoteEventFired(p.EventId);
    }

    private void HandleWorldSeedTyped(Packet packet)
    {
        if (packet is not WorldSeedPacket p) return;
        CheckWorldSeed(p.PlayerId, p.Seed);
    }

    private void HandleWorldSeedAuthTyped(Packet packet)
    {
        if (packet is not WorldSeedAuthPacket p) return;
        AdoptAuthoritySeed(p.PlayerId, p.Seed);
    }

    private void HandleSyncCheckDigest(Packet packet)
    {
        if (packet is not SyncCheckDigestPacket p || p.PlayerId == LocalPlayerId) return;
        _syncCheck?.OnRemoteDigest(p.PlayerId, p.Digest);
    }

    /// <summary>Ironbark v2 PhysicsState lane — reserved (no free-body emit; Caps omit PhysicsState).</summary>
    private void HandlePhysicsStateBatch(Packet packet)
    {
        if (packet is not PhysicsStateBatchPacket p || p.PlayerId == LocalPlayerId) return;
        if (p.Count > 0 && NetworkLayer.VerboseLogging)
            ModLogger.Msg($"[PhysicsState] recv batch count={p.Count} from p{p.PlayerId} (reserved lane)");
    }

    private void HandleEntityBurning(Packet packet)
    {
        if (packet is not EntityBurningPacket p || p.PlayerId == LocalPlayerId) return;
        Patches.Burn_Patch.ApplyRemoteEntityBurn(p.EntityId, p.IsBurning, p.BurnTime);
    }

    private void HandlePlayerBurning(Packet packet)
    {
        if (packet is not PlayerBurningPacket p || p.PlayerId == LocalPlayerId) return;
        Patches.Burn_Patch.ApplyRemotePlayerBurn(p.PlayerId, p.IsBurning, p.BurnTime);
    }

    private void HandleExplosionSpawnObject(Packet packet)
    {
        if (packet is not ExplosionSpawnObjectPacket p || p.PlayerId == LocalPlayerId) return;
        Patches.ExplosionSpawn_Patch.ApplyRemote(p.PrefabName,
            new Vector3(p.X, p.Y, p.Z), new Vector3(p.Rx, p.Ry, p.Rz));
    }

    private void HandleEntitySpawn(Packet packet)
    {
        if (packet is not EntitySpawnPacket p || p.PlayerId == LocalPlayerId) return;
        _enemySync?.OnRemoteEntitySpawn(p.EntityId, p.EntityType, p.PrefabPath,
            new Vector3(p.X, p.Y, p.Z), p.RotY);
    }

    private void HandleLiquidStopBurn(Packet packet)
    {
        if (packet is not LiquidStopBurnPacket p || p.PlayerId == LocalPlayerId) return;
        Patches.Gasoline_Patch.ApplyRemoteStopBurn(new Vector3(p.X, p.Y, p.Z));
    }
}
