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
        /// <summary>Inbound dispatch slice: Combat.</summary>
        private bool TryDispatchCombat(NetMessageType type, byte[] payload)
        {
            switch (type)
            {
                        case NetMessageType.PlayerAttack:
                            CombatHandlers.HandlePlayerAttack(PlayerAttackMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DamagePlayer:
                            CombatHandlers.HandleDamagePlayer(DamagePlayerMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerDied:
                            CombatHandlers.HandlePlayerDied(PlayerDiedMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DeathBagSpawn:
                            CombatHandlers.HandleDeathBagSpawn(DeathBagSpawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.DeathBagLooted:
                            CombatHandlers.HandleDeathBagLooted(DeathBagLootedMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.NightDeathState:
                            CombatHandlers.HandleNightDeathState(NightDeathStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.NightDeathRelease:
                            CombatHandlers.HandleNightDeathRelease(NightDeathReleaseMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.MorningReward:
                            CombatHandlers.HandleMorningReward(MorningRewardMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.FriendlyFire:
                            CombatHandlers.HandleFriendlyFire(FriendlyFireMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ThrowableSpawn:
                            CombatFxHandlers.HandleThrowableSpawn(ThrowableSpawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ExplosionTrigger:
                            CombatFxHandlers.HandleExplosionTrigger(ExplosionTriggerMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.GasTrailSpawn:
                            CombatFxHandlers.HandleGasTrailSpawn(GasTrailSpawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.GasIgnite:
                            CombatFxHandlers.HandleGasIgnite(GasIgniteMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ShadowEvent:
                            NightHandlers.HandleShadowEvent(
                                ShadowEventMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ShadowSpawn:
                            NightHandlers.HandleShadowSpawn(
                                ShadowSpawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.NightShadowSpawnRequest:
                            NightHandlers.HandleNightShadowSpawnRequest(
                                NightShadowSpawnRequestMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.EntityBurning:
                            CombatFxHandlers.HandleEntityBurning(EntityBurningMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.LiquidStopBurning:
                            CombatFxHandlers.HandleLiquidStopBurning(LiquidStopBurningMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ExplosionSpawnObject:
                            CombatFxHandlers.HandleExplosionSpawnObject(ExplosionSpawnObjectMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.PlayerBurning:
                            CombatFxHandlers.HandlePlayerBurning(PlayerBurningMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.MeleeWorldHit:
                            CombatFxHandlers.HandleMeleeWorldHit(MeleeWorldHitMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ShadowArmorState:
                            ShadowArmorHandlers.HandleShadowArmorState(
                                ShadowArmorStateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ShadowStateUpdate:
                            NightHandlers.HandleShadowStateUpdate(
                                ShadowStateUpdateMessage.Deserialize(new NetReader(payload)));
                            return true;
                        case NetMessageType.ThrowableDespawn:
                            WorldObjectSendHandlers.HandleThrowableDespawn(ThrowableDespawnMessage.Deserialize(new NetReader(payload)));
                            return true;
                default:
                    return false;
            }
        }
    }
}
