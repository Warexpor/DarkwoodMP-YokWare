using System.Collections.Generic;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to world physics / weather-time / late-join / proxy NetHandlers.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal WorldPhysicsNetHandlers WorldPhysicsHandlers { get; private set; }
        internal WorldWeatherTimeNetHandlers WorldWeatherTimeHandlers { get; private set; }
        internal WorldLateJoinNetHandlers WorldLateJoinHandlers { get; private set; }
        internal WorldProxyLifecycleNetHandlers WorldProxyLifecycleHandlers { get; private set; }
        internal WorldProxyEffectNetHandlers WorldProxyEffectHandlers { get; private set; }
        internal WorldProxyNetHandlers WorldProxyHandlers { get; private set; }

        internal void SendWeatherSync() => SendWeatherSyncTo(-1);

        internal void SendWeatherSyncTo(int targetPlayerId)
        {
            WorldWeatherTimeHandlers.SendWeatherSyncTo(targetPlayerId);
        }
        private void HandleWeatherSync(WeatherSyncMessage msg)
        {
            WorldWeatherTimeHandlers.HandleWeatherSync(msg);
        }
        private void SendWorldSnapshot()
        {
            WorldPhysicsHandlers.SendWorldSnapshot();
        }
        private void HandlePhysicsState(PhysicsStateMessage state)
        {
            WorldPhysicsHandlers.HandlePhysicsState(state);
        }
        private void HandleItemSpawn(ItemSpawnMessage msg)
        {
            WorldPhysicsHandlers.HandleItemSpawn(msg);
        }
        private void HandleLightState(LightStateMessage ls)
        {
            WorldPhysicsHandlers.HandleLightState(ls);
        }
        internal static bool CanSpawnRemoteProxies()
        {
            return WorldProxyNetHandlers.CanSpawnRemoteProxies();
        }
        internal void EnsureRemoteProxy(int playerId)
        {
            WorldProxyHandlers.EnsureRemoteProxy(playerId);
        }
        public RemotePlayerProxy GetProxy(int playerId)
        {
            return WorldProxyHandlers.GetProxy(playerId);
        }
        public IEnumerable<RemotePlayerProxy> GetAllProxies()
        {
            return WorldProxyHandlers.GetAllProxies();
        }
        public void TeleportRemoteProxyTo(Vector3 position, float rotY = 0f, int playerId = -1)
        {
            WorldProxyHandlers.TeleportRemoteProxyTo(position, rotY, playerId);
        }
        private void DestroyRemoteProxy(int playerId)
        {
            WorldProxyHandlers.DestroyRemoteProxy(playerId);
        }
        private void HandleProxyFootstep(int playerId, bool running)
        {
            WorldProxyHandlers.HandleProxyFootstep(playerId, running);
        }
        private void SendPlayerEffects()
        {
            WorldProxyHandlers.SendPlayerEffects();
        }
        internal void HandlePlayerEffectSync(PlayerEffectSyncMessage msg)
        {
            WorldProxyHandlers.HandlePlayerEffectSync(msg);
        }
        private static void PlayProxyFootstepSound(RemotePlayerProxy proxy, bool running)
        {
            WorldProxyNetHandlers.PlayProxyFootstepSound(proxy, running);
        }
        private static void ForceSpatialProxyOneShot(AudioObject audioObj, string soundId)
        {
            WorldProxyNetHandlers.ForceSpatialProxyOneShot(audioObj, soundId);
        }
        private void HandlePlayerSound(PlayerSoundMessage msg)
        {
            WorldProxyHandlers.HandlePlayerSound(msg);
        }
        private void HandlePlayerScare(PlayerScareMessage msg)
        {
            WorldProxyHandlers.HandlePlayerScare(msg);
        }
        private void SendTimeSync() => SendTimeSyncTo(-1);

        internal void SendTimeSyncTo(int targetPlayerId)
        {
            WorldWeatherTimeHandlers.SendTimeSyncTo(targetPlayerId);
        }
        private void HandleTimeSync(TimeSyncMessage msg)
        {
            WorldWeatherTimeHandlers.HandleTimeSync(msg);
        }
        public void ResyncDreamProxiesAfterLocalLoad(string locationName)
        {
            WorldProxyHandlers.ResyncDreamProxiesAfterLocalLoad(locationName);
        }
        private static void ApplyClientPersonalNewDay(int prevDay, int newDay)
        {
            WorldWeatherTimeNetHandlers.ApplyClientPersonalNewDay(prevDay, newDay);
        }
        private static void CleanupClientMorningTrader()
        {
            WorldWeatherTimeNetHandlers.CleanupClientMorningTrader();
        }
        public int RemotePlayerCount
        {
            get { return WorldProxyHandlers.RemotePlayerCount; }
        }

        public IEnumerable<RemotePlayerProxy> EnumerateRemoteProxies()
        {
            return WorldProxyHandlers.GetAllProxies();
        }

        private void SyncExistingWorldLightsTo(int targetPlayerId)
        {
            WorldLateJoinHandlers.SyncExistingWorldLightsTo(targetPlayerId);
        }

        private void SyncExistingGeneratorsTo(int targetPlayerId)
        {
            WorldLateJoinHandlers.SyncExistingGeneratorsTo(targetPlayerId);
        }

        internal void ResyncWorldLightsForPeer(int targetPlayerId)
        {
            WorldLateJoinHandlers.ResyncWorldLightsForPeer(targetPlayerId);
        }
    }
}
