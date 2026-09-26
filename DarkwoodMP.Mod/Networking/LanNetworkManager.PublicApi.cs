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

        internal void ResetCombatSessionState() => CombatHandlers.Reset();

        internal void RegisterDeathBag(string bagId, DeathDrop drop) =>
            CombatHandlers.RegisterDeathBag(bagId, drop);

        internal void RegisterDeathBagLooted(string bagId) =>
            CombatHandlers.RegisterDeathBagLooted(bagId);

        internal bool IsDeathBagLooted(string bagId) =>
            CombatHandlers.IsDeathBagLooted(bagId);

        internal void UnregisterDeathBag(string bagId) =>
            CombatHandlers.UnregisterDeathBag(bagId);

        internal DeathDrop FindDeathBagById(string bagId) =>
            CombatHandlers.FindDeathBagById(bagId);

        internal int SanitizePeerDamage(int reported, string context) =>
            CombatHandlers.SanitizePeerDamage(reported, context);

        internal void SendGasStateTo(int targetPlayerId) =>
            CombatFxHandlers.SendGasStateTo(targetPlayerId);

        /// <summary>Host late-join: living Infection splats via EntitySpawn (86).</summary>
        internal void SendInfectionStatesTo(int targetPlayerId) =>
            DWMPHorde.Patches.InfectionSyncHelpers.SendInfectionStatesTo(this, targetPlayerId);

        public bool HasAnyTrappedPlayer => PlayerPresenceHandlers.HasAnyTrappedPlayer;

        public bool IsRemotePlayerHasLightProtection(int playerId) =>
            PlayerPresenceHandlers.IsRemotePlayerHasLightProtection(playerId);

        public bool IsTrapOccupied(GameObject trapGo) =>
            PlayerPresenceHandlers.IsTrapOccupied(trapGo);

        public bool IsRemotePlayerTrappedNear(Vector3 trapPos) =>
            PlayerPresenceHandlers.IsRemotePlayerTrappedNear(trapPos);

        public static void NotifyBodyPushStarted(GameObject go)
        {
            var net = Instance;
            if (net == null) return;
            net.PlayerInteractHandlers.NotifyBodyPushStarted(go);
        }

        public static void NotifyBodyPushStopped(string objectName)
        {
            var net = Instance;
            if (net == null) return;
            net.PlayerInteractHandlers.NotifyBodyPushStopped(objectName);
        }

        internal void ClearSpawnedDragProxyItems() =>
            PlayerInteractHandlers.ClearSpawnedDragProxyItems();

        internal void DestroyRemoteItemLight(int playerId) =>
            PlayerHeldLightHandlers.DestroyRemoteItemLight(playerId);

        internal void DestroyRemoteFlareLight(int playerId) =>
            PlayerHeldLightHandlers.DestroyRemoteFlareLight(playerId);

        internal void PackContinuousLights(ref PlayerStateMessage msg, Player local) =>
            PlayerHeldLightHandlers.PackContinuousLights(ref msg, local);

        internal void ResetLocalLightSendCache() =>
            PlayerHeldLightHandlers.ResetLocalLightSendCache();

        internal static bool TryGetLocalHeldFlareLight(Player local, out Light2D light, out Flare flare) =>
            PlayerHeldLightNetHandlers.TryGetLocalHeldFlareLight(local, out light, out flare);

        internal static bool TryGetLocalHeldMatchLight(Player local, out Light2D light) =>
            PlayerHeldLightNetHandlers.TryGetLocalHeldMatchLight(local, out light);

        public static bool IsMatchLightItem(Player local) =>
            PlayerHeldLightNetHandlers.IsMatchLightItem(local);

        internal void RemoveRemoteDragIds(string objectName) =>
            PlayerInteractHandlers.RemoveRemoteDragIds(objectName);

        internal void ReleaseRemoteDragKinematic(string objectName) =>
            PlayerInteractHandlers.ReleaseRemoteDragKinematic(objectName);

        public IEnumerable<KeyValuePair<int, int>> EnumerateRemoteTrapOccupancy() =>
            PlayerPresenceHandlers.EnumerateRemoteTrapOccupancy();

        public void SendThrowableDespawn(ThrowableDespawnMessage msg) =>
            WorldObjectSendHandlers.SendThrowableDespawn(msg);

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

        public void SendScenarioEventFired(int nightId, int eventIndex) =>
            WorldSendHandlers.SendScenarioEventFired(nightId, eventIndex);

        public void SendEntityBurning(short entityId, bool isBurning, float burnTime = 0, float modifier = 0, float interval = 0) =>
            WorldSendHandlers.SendEntityBurning(entityId, isBurning, burnTime, modifier, interval);

        public void SendLiquidStopBurning(Vector3 pos) =>
            WorldSendHandlers.SendLiquidStopBurning(pos);

        public void SendPlayerBurning(bool isBurning, float burnTime = 0) =>
            WorldSendHandlers.SendPlayerBurning(isBurning, burnTime);

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
            WorldProxyNetHandlers.CanSpawnRemoteProxies();

        internal void EnsureRemoteProxy(int playerId) =>
            WorldProxyHandlers.EnsureRemoteProxy(playerId);

        public RemotePlayerProxy GetProxy(int playerId) =>
            WorldProxyHandlers.GetProxy(playerId);

        public IEnumerable<RemotePlayerProxy> GetAllProxies() =>
            WorldProxyHandlers.GetAllProxies();

        public void TeleportRemoteProxyTo(Vector3 position, float rotY = 0f, int playerId = -1) =>
            WorldProxyHandlers.TeleportRemoteProxyTo(position, rotY, playerId);

        internal void SendTimeSyncTo(int targetPlayerId) =>
            WorldWeatherTimeHandlers.SendTimeSyncTo(targetPlayerId);

        public void ResyncDreamProxiesAfterLocalLoad(string locationName) =>
            WorldProxyHandlers.ResyncDreamProxiesAfterLocalLoad(locationName);

        public int RemotePlayerCount => WorldProxyHandlers.RemotePlayerCount;

        public IEnumerable<RemotePlayerProxy> EnumerateRemoteProxies() =>
            WorldProxyHandlers.GetAllProxies();

        internal void ResyncWorldLightsForPeer(int targetPlayerId) =>
            WorldLateJoinHandlers.ResyncWorldLightsForPeer(targetPlayerId);
    }
}
