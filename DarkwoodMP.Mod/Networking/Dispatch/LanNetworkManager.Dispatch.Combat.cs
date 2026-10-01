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
            On(NetMessageType.PlayerAttack, PlayerAttackMessage.Deserialize, m => CombatHandlers.HandlePlayerAttack(m));
            On(NetMessageType.DamagePlayer, DamagePlayerMessage.Deserialize, m => CombatHandlers.HandleDamagePlayer(m));
            On(NetMessageType.PlayerDied, PlayerDiedMessage.Deserialize, m => CombatHandlers.HandlePlayerDied(m));
            On(NetMessageType.DeathBagSpawn, DeathBagSpawnMessage.Deserialize, m => CombatHandlers.HandleDeathBagSpawn(m));
            On(NetMessageType.DeathBagLooted, DeathBagLootedMessage.Deserialize, m => CombatHandlers.HandleDeathBagLooted(m));
            On(NetMessageType.NightDeathState, NightDeathStateMessage.Deserialize, m => CombatHandlers.HandleNightDeathState(m));
            On(NetMessageType.NightDeathRelease, NightDeathReleaseMessage.Deserialize, m => CombatHandlers.HandleNightDeathRelease(m));
            On(NetMessageType.MorningReward, MorningRewardMessage.Deserialize, m => CombatHandlers.HandleMorningReward(m));
            On(NetMessageType.FriendlyFire, FriendlyFireMessage.Deserialize, m => CombatHandlers.HandleFriendlyFire(m));
            On(NetMessageType.ThrowableSpawn, ThrowableSpawnMessage.Deserialize, m => CombatFxHandlers.HandleThrowableSpawn(m));
            On(NetMessageType.ExplosionTrigger, ExplosionTriggerMessage.Deserialize, m => CombatFxHandlers.HandleExplosionTrigger(m));
            On(NetMessageType.GasTrailSpawn, GasTrailSpawnMessage.Deserialize, m => CombatFxHandlers.HandleGasTrailSpawn(m));
            On(NetMessageType.GasIgnite, GasIgniteMessage.Deserialize, m => CombatFxHandlers.HandleGasIgnite(m));
            On(NetMessageType.ShadowEvent, ShadowEventMessage.Deserialize, m => NightHandlers.HandleShadowEvent(m));
            On(NetMessageType.ShadowSpawn, ShadowSpawnMessage.Deserialize, m => NightHandlers.HandleShadowSpawn(m));
            On(NetMessageType.NightShadowSpawnRequest, NightShadowSpawnRequestMessage.Deserialize, m => NightHandlers.HandleNightShadowSpawnRequest(m));
            On(NetMessageType.EntityBurning, EntityBurningMessage.Deserialize, m => CombatFxHandlers.HandleEntityBurning(m));
            On(NetMessageType.LiquidStopBurning, LiquidStopBurningMessage.Deserialize, m => CombatFxHandlers.HandleLiquidStopBurning(m));
            On(NetMessageType.ExplosionSpawnObject, ExplosionSpawnObjectMessage.Deserialize, m => CombatFxHandlers.HandleExplosionSpawnObject(m));
            On(NetMessageType.PlayerBurning, PlayerBurningMessage.Deserialize, m => CombatFxHandlers.HandlePlayerBurning(m));
            On(NetMessageType.MeleeWorldHit, MeleeWorldHitMessage.Deserialize, m => CombatFxHandlers.HandleMeleeWorldHit(m));
            On(NetMessageType.ShadowArmorState, ShadowArmorStateMessage.Deserialize, m => ShadowArmorHandlers.HandleShadowArmorState(m));
            On(NetMessageType.ShadowStateUpdate, ShadowStateUpdateMessage.Deserialize, m => NightHandlers.HandleShadowStateUpdate(m));
            On(NetMessageType.ThrowableDespawn, ThrowableDespawnMessage.Deserialize, m => WorldObjectSendHandlers.HandleThrowableDespawn(m));
        }
    }
}
