using System;
using System.Collections.Generic;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to world object-send / misc-send NetHandlers.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal WorldObjectSendNetHandlers WorldObjectSendHandlers { get; private set; }
        internal WorldSendNetHandlers WorldSendHandlers { get; private set; }

        private void HandleTrapBulk(TrapBulkMessage msg)
        {
            WorldObjectSendHandlers.HandleTrapBulk(msg);
        }
        public void SendThrowableDespawn(ThrowableDespawnMessage msg)
        {
            WorldObjectSendHandlers.SendThrowableDespawn(msg);
        }
        public void SendEntityDespawn(short entityId)
        {
            WorldObjectSendHandlers.SendEntityDespawn(entityId);
        }
        private void HandleEntityDespawn(EntityDespawnMessage msg)
        {
            WorldObjectSendHandlers.HandleEntityDespawn(msg);
        }
        private void HandleThrowableDespawn(ThrowableDespawnMessage msg)
        {
            WorldObjectSendHandlers.HandleThrowableDespawn(msg);
        }
        public void SendItemSpawn(ItemSpawnMessage msg)
        {
            WorldObjectSendHandlers.SendItemSpawn(msg);
        }
        public void SendShadowEvent(ShadowEventMessage msg)
        {
            WorldSendHandlers.SendShadowEvent(msg);
        }
        public void SendShadowSpawn(ShadowSpawnMessage msg)
        {
            WorldSendHandlers.SendShadowSpawn(msg);
        }
        public void SendShadowStateUpdate(ShadowStateUpdateMessage msg)
        {
            WorldSendHandlers.SendShadowStateUpdate(msg);
        }
        private void BroadcastShadowStates()
        {
            WorldSendHandlers.BroadcastShadowStates();
        }
        internal void SendShadowsTo(int targetPlayerId)
        {
            WorldSendHandlers.SendShadowsTo(targetPlayerId);
        }
        public void SendScenarioSync(ScenarioSyncMessage msg)
        {
            WorldSendHandlers.SendScenarioSync(msg);
        }
        public void SendScenarioEventFired(int nightId, int eventIndex)
        {
            WorldSendHandlers.SendScenarioEventFired(nightId, eventIndex);
        }
        public void SendEntityBurning(short entityId, bool isBurning, float burnTime = 0, float modifier = 0, float interval = 0)
        {
            WorldSendHandlers.SendEntityBurning(entityId, isBurning, burnTime, modifier, interval);
        }
        public void SendLiquidStopBurning(Vector3 pos)
        {
            WorldSendHandlers.SendLiquidStopBurning(pos);
        }
        public void SendPlayerBurning(bool isBurning, float burnTime = 0)
        {
            WorldSendHandlers.SendPlayerBurning(isBurning, burnTime);
        }
        public void SendExplosionSpawnObject(string prefabName, Vector3 pos, Vector3 rot)
        {
            WorldSendHandlers.SendExplosionSpawnObject(prefabName, pos, rot);
        }
        public void SendConstructible(ConstructibleMessage msg)
        {
            WorldSendHandlers.SendConstructible(msg);
        }
        public void SendInteractiveItemSwitch(InteractiveItemSwitchMessage msg)
        {
            WorldSendHandlers.SendInteractiveItemSwitch(msg);
        }
        public void SendPadlockUnlock(PadlockUnlockMessage msg)
        {
            WorldSendHandlers.SendPadlockUnlock(msg);
        }
        public void SendLockedUnlock(LockedUnlockMessage msg)
        {
            WorldSendHandlers.SendLockedUnlock(msg);
        }
        public void SendGameEventsFired(GameEventsFiredMessage msg)
        {
            WorldSendHandlers.SendGameEventsFired(msg);
        }
        public void SendHideoutUpgrade(HideoutUpgradeMessage msg)
        {
            WorldSendHandlers.SendHideoutUpgrade(msg);
        }
        public void SendGeneratorState(GeneratorState gs)
        {
            WorldSendHandlers.SendGeneratorState(gs);
        }
        public void SendLightState(LightStateMessage ls)
        {
            WorldSendHandlers.SendLightState(ls);
        }
        public void SendWorldObjectRemoved(WorldObjectRemovedMessage msg)
        {
            WorldSendHandlers.SendWorldObjectRemoved(msg);
        }
        public void SendPlayerLightState(PlayerLightStateMessage msg, DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            WorldSendHandlers.SendPlayerLightState(msg, method);
        }
        public void SyncCurrentLightState()
        {
            WorldSendHandlers.SyncCurrentLightState();
        }
        public void SendThrowableSpawn(ThrowableSpawnMessage msg)
        {
            WorldObjectSendHandlers.SendThrowableSpawn(msg);
        }
        public void SendExplosionTrigger(ExplosionTriggerMessage msg)
        {
            WorldSendHandlers.SendExplosionTrigger(msg);
        }
        public void SendPlayerAudio(PlayerAudioMessage msg)
        {
            WorldSendHandlers.SendPlayerAudio(msg);
        }
        public void SendGasTrailSpawn(GasTrailSpawnMessage msg)
        {
            WorldSendHandlers.SendGasTrailSpawn(msg);
        }
        public void SendMeleeWorldHit(MeleeWorldHitMessage msg)
        {
            WorldSendHandlers.SendMeleeWorldHit(msg);
        }
        public void SendGasIgnite(GasIgniteMessage msg)
        {
            WorldSendHandlers.SendGasIgnite(msg);
        }
        public void SendDroppedItemSpawn(DroppedItemSpawnMessage msg)
        {
            WorldObjectSendHandlers.SendDroppedItemSpawn(msg);
        }
        public void SendDroppedItemPickup(DroppedItemPickupMessage msg)
        {
            WorldObjectSendHandlers.SendDroppedItemPickup(msg);
        }
        public static bool IsDropGuidConsumed(string guid)
        {
            return WorldObjectSendNetHandlers.IsDropGuidConsumed(guid);
        }
        private void SyncExistingDroppedItems(int targetPlayerId)
        {
            WorldObjectSendHandlers.SyncExistingDroppedItems(targetPlayerId);
        }
        public void SendSawState(SawStateMessage msg)
        {
            WorldSendHandlers.SendSawState(msg);
        }
        public void SendPlayerAnimation(PlayerAnimationMessage msg)
        {
            WorldSendHandlers.SendPlayerAnimation(msg);
        }
        public void SendPlayerAnimLibrary(PlayerAnimLibraryMessage msg)
        {
            WorldSendHandlers.SendPlayerAnimLibrary(msg);
        }
        public void SendBulletImpact(BulletImpactMessage msg)
        {
            WorldSendHandlers.SendBulletImpact(msg);
        }
        public void SendPlayerFiredWeapon(PlayerFiredWeaponMessage msg)
        {
            WorldSendHandlers.SendPlayerFiredWeapon(msg);
        }
        public void SendDamagePlayer(int victimPlayerId, DamagePlayerMessage msg)
        {
            WorldSendHandlers.SendDamagePlayer(victimPlayerId, msg);
        }

        public void SendDoorState(DoorState door)
        {
            WorldObjectSendHandlers.SendDoorState(door);
        }

        public void SendTrapState(TrapState ts)
        {
            WorldObjectSendHandlers.SendTrapState(ts);
        }

        internal void SendTrapBulkTo(int playerId)
        {
            WorldObjectSendHandlers.SendTrapBulkTo(playerId);
        }
    }
}
