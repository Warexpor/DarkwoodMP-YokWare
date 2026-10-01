using System;
using System.IO;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public sealed partial class LanNetworkManager
    {
        /// <summary>Inbound handlers: Combat.</summary>
        private void RegisterCombatHandlers()
        {
            On(NetMessageType.PlayerAttack, PlayerAttackMessage.Deserialize, m => CombatAttackHandlers.HandlePlayerAttack(m));
            On(NetMessageType.DamagePlayer, DamagePlayerMessage.Deserialize, m => CombatAttackHandlers.HandleDamagePlayer(m));
            On(NetMessageType.PlayerDied, PlayerDiedMessage.Deserialize, m => CombatDeathStateHandlers.HandlePlayerDied(m));
            On(NetMessageType.DeathBagSpawn, DeathBagSpawnMessage.Deserialize, m => CombatDeathBagHandlers.HandleDeathBagSpawn(m));
            On(NetMessageType.DeathBagLooted, DeathBagLootedMessage.Deserialize, m => CombatDeathBagHandlers.HandleDeathBagLooted(m));
            On(NetMessageType.NightDeathState, NightDeathStateMessage.Deserialize, m => CombatDeathStateHandlers.HandleNightDeathState(m));
            On(NetMessageType.NightDeathRelease, NightDeathReleaseMessage.Deserialize, m => CombatDeathStateHandlers.HandleNightDeathRelease(m));
            On(NetMessageType.MorningReward, MorningRewardMessage.Deserialize, m => CombatDeathStateHandlers.HandleMorningReward(m));
            On(NetMessageType.FriendlyFire, FriendlyFireMessage.Deserialize, m => CombatAttackHandlers.HandleFriendlyFire(m));
            On(NetMessageType.ThrowableSpawn, ThrowableSpawnMessage.Deserialize, m => CombatFxImpactHandlers.HandleThrowableSpawn(m));
            On(NetMessageType.ExplosionTrigger, ExplosionTriggerMessage.Deserialize, m => CombatFxImpactHandlers.HandleExplosionTrigger(m));
            On(NetMessageType.GasTrailSpawn, GasTrailSpawnMessage.Deserialize, m => CombatFxGasBurnHandlers.HandleGasTrailSpawn(m));
            On(NetMessageType.GasIgnite, GasIgniteMessage.Deserialize, m => CombatFxGasBurnHandlers.HandleGasIgnite(m));
            On(NetMessageType.ShadowEvent, ShadowEventMessage.Deserialize, m => NightHandlers.HandleShadowEvent(m));
            On(NetMessageType.ShadowSpawn, ShadowSpawnMessage.Deserialize, m => NightHandlers.HandleShadowSpawn(m));
            On(NetMessageType.NightShadowSpawnRequest, NightShadowSpawnRequestMessage.Deserialize, m => NightHandlers.HandleNightShadowSpawnRequest(m));
            On(NetMessageType.EntityBurning, EntityBurningMessage.Deserialize, m => CombatFxGasBurnHandlers.HandleEntityBurning(m));
            On(NetMessageType.LiquidStopBurning, LiquidStopBurningMessage.Deserialize, m => CombatFxGasBurnHandlers.HandleLiquidStopBurning(m));
            On(NetMessageType.ExplosionSpawnObject, ExplosionSpawnObjectMessage.Deserialize, m => CombatFxImpactHandlers.HandleExplosionSpawnObject(m));
            On(NetMessageType.PlayerBurning, PlayerBurningMessage.Deserialize, m => CombatFxGasBurnHandlers.HandlePlayerBurning(m));
            On(NetMessageType.MeleeWorldHit, MeleeWorldHitMessage.Deserialize, m => CombatFxImpactHandlers.HandleMeleeWorldHit(m));
            On(NetMessageType.ShadowArmorState, ShadowArmorStateMessage.Deserialize, m => ShadowArmorHandlers.HandleShadowArmorState(m));
            On(NetMessageType.ShadowStateUpdate, ShadowStateUpdateMessage.Deserialize, m => NightHandlers.HandleShadowStateUpdate(m));
            On(NetMessageType.ThrowableDespawn, ThrowableDespawnMessage.Deserialize, m => WorldObjectSendHandlers.HandleThrowableDespawn(m));
        }
    }
}
