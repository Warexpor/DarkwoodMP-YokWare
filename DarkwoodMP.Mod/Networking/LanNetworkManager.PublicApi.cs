using System.Collections.Generic;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Public/internal one-line forwards onto domain NetHandlers.
    /// Keeps patch call sites stable after Domains Handle* façades were deleted.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>Player id of the peer whose message is currently being handled.</summary>
        internal int CurrentReceivePlayerId => _currentReceivePlayerId;

        internal void ResetCombatSessionState()
        {
            CombatDeathBagHandlers.Reset();
            CombatAttackHandlers.Reset();
        }

        internal void RegisterDeathBag(string bagId, DeathDrop drop) =>
            CombatDeathBagHandlers.RegisterDeathBag(bagId, drop);

        internal void RegisterDeathBagLooted(string bagId) =>
            CombatDeathBagHandlers.RegisterDeathBagLooted(bagId);

        internal bool IsDeathBagLooted(string bagId) =>
            CombatDeathBagHandlers.IsDeathBagLooted(bagId);

        internal void UnregisterDeathBag(string bagId) =>
            CombatDeathBagHandlers.UnregisterDeathBag(bagId);

        internal DeathDrop FindDeathBagById(string bagId) =>
            CombatDeathBagHandlers.FindDeathBagById(bagId);

        internal int SanitizePeerDamage(int reported, string context) =>
            CombatAttackHandlers.SanitizePeerDamage(reported, context);

        internal void SendGasStateTo(int targetPlayerId) =>
            CombatFxGasBurnHandlers.SendGasStateTo(targetPlayerId);

        /// <summary>Host late-join: living Infection splats via EntitySpawn (86).</summary>
        internal void SendInfectionStatesTo(int targetPlayerId) =>
            DWMPHorde.Patches.InfectionSyncHelpers.SendInfectionStatesTo(this, targetPlayerId);

        public bool IsRemotePlayerHasLightProtection(int playerId) =>
            PlayerPresenceHandlers.IsRemotePlayerHasLightProtection(playerId);

        public static void NotifyBodyPushStarted(GameObject go)
        {
            var net = ModRuntime.Network;
            if (net == null) return;
            net.PlayerInteractHandlers.NotifyBodyPushStarted(go);
        }

        public static void NotifyBodyPushStopped(string objectName)
        {
            var net = ModRuntime.Network;
            if (net == null) return;
            net.PlayerInteractHandlers.NotifyBodyPushStopped(objectName);
        }

        internal void ClearSpawnedDragProxyItems() =>
            PlayerInteractHandlers.ClearSpawnedDragProxyItems();

        internal void DestroyRemoteItemLight(int playerId) =>
            PlayerHeldLightApplyHandlers.DestroyRemoteItemLight(playerId);

        internal void DestroyRemoteFlareLight(int playerId) =>
            PlayerHeldLightApplyHandlers.DestroyRemoteFlareLight(playerId);

        internal void PackContinuousLights(ref PlayerStateMessage msg, Player local) =>
            PlayerHeldLightPackHandlers.PackContinuousLights(ref msg, local);

        internal void ResetLocalLightSendCache() =>
            PlayerHeldLightPackHandlers.ResetLocalLightSendCache();

        internal static bool TryGetLocalHeldFlareLight(Player local, out Light2D light, out Flare flare) =>
            PlayerHeldLightPackNetHandlers.TryGetLocalHeldFlareLight(local, out light, out flare);

        internal static bool TryGetLocalHeldMatchLight(Player local, out Light2D light) =>
            PlayerHeldLightPackNetHandlers.TryGetLocalHeldMatchLight(local, out light);

        public static bool IsMatchLightItem(Player local) =>
            PlayerHeldLightPackNetHandlers.IsMatchLightItem(local);

        internal void RemoveRemoteDragIds(string objectName) =>
            PlayerInteractHandlers.RemoveRemoteDragIds(objectName);

        internal void ReleaseRemoteDragKinematic(string objectName) =>
            PlayerInteractHandlers.ReleaseRemoteDragKinematic(objectName);

        public IEnumerable<KeyValuePair<int, int>> EnumerateRemoteTrapOccupancy() =>
            PlayerPresenceHandlers.EnumerateRemoteTrapOccupancy();

        public void SendEntityDespawn(short entityId) =>
            WorldObjectSendHandlers.SendEntityDespawn(entityId);

        public void SendItemSpawn(ItemSpawnMessage msg) =>
            WorldObjectSendHandlers.SendItemSpawn(msg);

        public void SendShadowEvent(ShadowEventMessage msg) =>
            WorldSendHandlers.SendShadowEvent(msg);

        public void SendShadowSpawn(ShadowSpawnMessage msg) =>
            WorldSendHandlers.SendShadowSpawn(msg);

        public void SendShadowStateUpdate(ShadowStateUpdateMessage msg) =>
            WorldSendHandlers.SendShadowStateUpdate(msg);

        internal void SendShadowsTo(int targetPlayerId) =>
            WorldSendHandlers.SendShadowsTo(targetPlayerId);

        public void SendScenarioSync(ScenarioSyncMessage msg) =>
            WorldSendHandlers.SendScenarioSync(msg);

        public void SendScenarioEventFired(int nightId, int eventIndex, string anchors) =>
            WorldSendHandlers.SendScenarioEventFired(nightId, eventIndex, anchors);

        public void SendEntityBurning(short entityId, bool isBurning, float burnTime = 0, float modifier = 0, float interval = 0) =>
            WorldSendHandlers.SendEntityBurning(entityId, isBurning, burnTime, modifier, interval);

        public void SendLiquidStopBurning(Vector3 pos) =>
            WorldSendHandlers.SendLiquidStopBurning(pos);

        public void SendPlayerBurning(bool isBurning, float burnTime = 0, bool special = false) =>
            WorldSendHandlers.SendPlayerBurning(isBurning, burnTime, special);

        public void SendExplosionSpawnObject(string prefabName, Vector3 pos, Vector3 rot) =>
            WorldSendHandlers.SendExplosionSpawnObject(prefabName, pos, rot);

        public void SendConstructible(ConstructibleMessage msg) =>
            WorldSendHandlers.SendConstructible(msg);

        public void SendInteractiveItemSwitch(InteractiveItemSwitchMessage msg) =>
            WorldSendHandlers.SendInteractiveItemSwitch(msg);

        public void SendPadlockUnlock(PadlockUnlockMessage msg) =>
            WorldSendHandlers.SendPadlockUnlock(msg);

        public void SendLockedUnlock(LockedUnlockMessage msg) =>
            WorldSendHandlers.SendLockedUnlock(msg);

        public void SendGameEventsFired(GameEventsFiredMessage msg) =>
            WorldSendHandlers.SendGameEventsFired(msg);

        public void SendHideoutUpgrade(HideoutUpgradeMessage msg) =>
            WorldSendHandlers.SendHideoutUpgrade(msg);

        public void SendGeneratorState(GeneratorState gs) =>
            WorldSendHandlers.SendGeneratorState(gs);

        public void SendLightState(LightStateMessage ls) =>
            WorldSendHandlers.SendLightState(ls);

        public void SendWorldObjectRemoved(WorldObjectRemovedMessage msg) =>
            WorldSendHandlers.SendWorldObjectRemoved(msg);

        public void SendPlayerLightState(PlayerLightStateMessage msg, DeliveryMethod method = DeliveryMethod.Unreliable) =>
            WorldSendHandlers.SendPlayerLightState(msg, method);

        public void SyncCurrentLightState() =>
            WorldSendHandlers.SyncCurrentLightState();

        public void SyncCurrentAnimLibrary() =>
            WorldSendHandlers.SyncCurrentAnimLibrary();

        public void SendThrowableSpawn(ThrowableSpawnMessage msg) =>
            WorldObjectSendHandlers.SendThrowableSpawn(msg);

        public void SendExplosionTrigger(ExplosionTriggerMessage msg) =>
            WorldSendHandlers.SendExplosionTrigger(msg);

        public void SendPlayerAudio(PlayerAudioMessage msg) =>
            WorldSendHandlers.SendPlayerAudio(msg);

        public void SendGasTrailSpawn(GasTrailSpawnMessage msg) =>
            WorldSendHandlers.SendGasTrailSpawn(msg);

        public void SendMeleeWorldHit(MeleeWorldHitMessage msg) =>
            WorldSendHandlers.SendMeleeWorldHit(msg);

        public void SendGasIgnite(GasIgniteMessage msg) =>
            WorldSendHandlers.SendGasIgnite(msg);

        public void SendDroppedItemSpawn(DroppedItemSpawnMessage msg) =>
            WorldObjectSendHandlers.SendDroppedItemSpawn(msg);

        public void SendDroppedItemPickup(DroppedItemPickupMessage msg) =>
            WorldObjectSendHandlers.SendDroppedItemPickup(msg);

        public static bool IsDropGuidConsumed(string guid) =>
            WorldObjectSendNetHandlers.IsDropGuidConsumed(guid);

        public void SendSawState(SawStateMessage msg) =>
            WorldSendHandlers.SendSawState(msg);

        public void SendPlayerAnimation(PlayerAnimationMessage msg) =>
            WorldSendHandlers.SendPlayerAnimation(msg);

        public void SendPlayerAnimLibrary(PlayerAnimLibraryMessage msg) =>
            WorldSendHandlers.SendPlayerAnimLibrary(msg);

        public void SendBulletImpact(BulletImpactMessage msg) =>
            WorldSendHandlers.SendBulletImpact(msg);

        public void SendPlayerFiredWeapon(PlayerFiredWeaponMessage msg) =>
            WorldSendHandlers.SendPlayerFiredWeapon(msg);

        public void SendDamagePlayer(int victimPlayerId, DamagePlayerMessage msg) =>
            WorldSendHandlers.SendDamagePlayer(victimPlayerId, msg);

        public void SendDoorState(DoorState door) =>
            WorldObjectSendHandlers.SendDoorState(door);

        public void SendTrapState(TrapState ts) =>
            WorldObjectSendHandlers.SendTrapState(ts);

        internal void SendTrapBulkTo(int playerId) =>
            WorldObjectSendHandlers.SendTrapBulkTo(playerId);

        internal void SendWeatherSync() => SendWeatherSyncTo(-1);

        internal void SendWeatherSyncWithStrike(byte strike) =>
            WorldWeatherTimeHandlers.SendWeatherSyncWithStrike(strike);

        internal void SendWeatherSyncTo(int targetPlayerId) =>
            WorldWeatherTimeHandlers.SendWeatherSyncTo(targetPlayerId);

        internal static bool CanSpawnRemoteProxies() =>
            WorldProxyLifecycleNetHandlers.CanSpawnRemoteProxies();

        internal void EnsureRemoteProxy(int playerId) =>
            WorldProxyLifecycleHandlers.EnsureRemoteProxy(playerId);

        public RemotePlayerProxy GetProxy(int playerId) =>
            WorldProxyLifecycleHandlers.GetProxy(playerId);

        public IEnumerable<RemotePlayerProxy> GetAllProxies() =>
            WorldProxyLifecycleHandlers.GetAllProxies();

        public void TeleportRemoteProxyTo(Vector3 position, float rotY = 0f, int playerId = -1) =>
            WorldProxyLifecycleHandlers.TeleportRemoteProxyTo(position, rotY, playerId);

        internal void SendTimeSyncTo(int targetPlayerId) =>
            WorldWeatherTimeHandlers.SendTimeSyncTo(targetPlayerId);

        public void ResyncDreamProxiesAfterLocalLoad(string locationName) =>
            WorldProxyLifecycleHandlers.ResyncDreamProxiesAfterLocalLoad(locationName);

        public int RemotePlayerCount => WorldProxyLifecycleHandlers.RemotePlayerCount;

        public IEnumerable<RemotePlayerProxy> EnumerateRemoteProxies() =>
            WorldProxyLifecycleHandlers.GetAllProxies();

        internal void ResyncWorldLightsForPeer(int targetPlayerId) =>
            WorldLateJoinHandlers.ResyncWorldLightsForPeer(targetPlayerId);

        /// <summary>
        /// Host: after a peer first enters (or soft-reconnects into) an outside pad,
        /// re-push barricade / opened-door / unlocked-padlock / fired-GE /
        /// destroyed-item / NPC visual state that late-join bulk may have missed
        /// while the pad was not spawned yet.
        /// </summary>
        internal void ResyncOutsideLocationPadForPeer(int targetPlayerId, Location loc) =>
            WorldLateJoinHandlers.ResyncOutsideLocationPadForPeer(targetPlayerId, loc);
    }
}
