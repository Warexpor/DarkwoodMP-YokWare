using System.Collections.Generic;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: proxy lifecycle + effect/sound siblings. Awake wires them.
    /// </summary>
    internal sealed class WorldProxyNetHandlers
    {
        private readonly WorldProxyLifecycleNetHandlers _lifecycle;
        private readonly WorldProxyEffectNetHandlers _effects;

        internal WorldProxyNetHandlers(
            WorldProxyLifecycleNetHandlers lifecycle,
            WorldProxyEffectNetHandlers effects)
        {
            _lifecycle = lifecycle ?? throw new System.ArgumentNullException(nameof(lifecycle));
            _effects = effects ?? throw new System.ArgumentNullException(nameof(effects));
        }

        internal static bool CanSpawnRemoteProxies() =>
            WorldProxyLifecycleNetHandlers.CanSpawnRemoteProxies();

        internal void EnsureRemoteProxy(int playerId) =>
            _lifecycle.EnsureRemoteProxy(playerId);

        public RemotePlayerProxy GetProxy(int playerId) =>
            _lifecycle.GetProxy(playerId);

        public int RemotePlayerCount => _lifecycle.RemotePlayerCount;

        public IEnumerable<RemotePlayerProxy> GetAllProxies() =>
            _lifecycle.GetAllProxies();

        internal void TeleportRemoteProxyTo(Vector3 position, float rotY = 0f, int playerId = -1) =>
            _lifecycle.TeleportRemoteProxyTo(position, rotY, playerId);

        internal void DestroyRemoteProxy(int playerId) =>
            _lifecycle.DestroyRemoteProxy(playerId);

        internal void ResyncDreamProxiesAfterLocalLoad(string locationName) =>
            _lifecycle.ResyncDreamProxiesAfterLocalLoad(locationName);

        internal void ProxyAggroCheck() =>
            _lifecycle.ProxyAggroCheck();

        internal void HandleProxyFootstep(int playerId, bool running) =>
            _effects.HandleProxyFootstep(playerId, running);

        internal void SendPlayerEffects() =>
            _effects.SendPlayerEffects();

        internal void HandlePlayerEffectSync(PlayerEffectSyncMessage msg) =>
            _effects.HandlePlayerEffectSync(msg);

        internal static void PlayProxyFootstepSound(RemotePlayerProxy proxy, bool running) =>
            WorldProxyEffectNetHandlers.PlayProxyFootstepSound(proxy, running);

        internal static void ForceSpatialProxyOneShot(AudioObject audioObj, string soundId) =>
            WorldProxyEffectNetHandlers.ForceSpatialProxyOneShot(audioObj, soundId);

        internal void HandlePlayerSound(PlayerSoundMessage msg) =>
            _effects.HandlePlayerSound(msg);

        internal void HandlePlayerScare(PlayerScareMessage msg) =>
            _effects.HandlePlayerScare(msg);
    }
}
