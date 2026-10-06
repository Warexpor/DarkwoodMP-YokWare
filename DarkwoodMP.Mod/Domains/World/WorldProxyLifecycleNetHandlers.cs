using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Remote proxy spawn / teleport / destroy / dream resync / aggro.</summary>
    internal sealed class WorldProxyLifecycleNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal WorldProxyLifecycleNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>
        /// Local Player must be active in a loaded chapter. Title + LoadScene have an
        /// An inactive or null Player would cause cloning to spam logs and freeze both installs.
        /// </summary>
        internal static bool CanSpawnRemoteProxies()
        {
            try
            {
                if (GameScreen.AtTitle || Core.loadingGame)
                    return false;
                Player p = Player.Instance;
                if (p == null || p.gameObject == null || !p.gameObject.activeInHierarchy)
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void EnsureRemoteProxy(int playerId)
        {
            if (playerId <= 0)
                return;
            if (_net.RemoteProxies.TryGetValue(playerId, out var existing))
            {
                // Unity fake-null: destroyed GO still in dict → LocationExit NRE.
                if (existing != null)
                    return;
                _net.RemoteProxies.Remove(playerId);
            }
            // Silent skip; do not log every network tick during join load.
            if (!LanNetworkManager.CanSpawnRemoteProxies())
                return;

            RemotePlayerProxy.Spawn(ModRuntime.Log, out RemotePlayerProxy proxy);
            if (proxy != null)
            {
                proxy.PlayerId = playerId;
                _net.RemoteProxies[playerId] = proxy;
                proxy.OnFootstep += (pId, running) => _net.WorldProxyEffectHandlers.HandleProxyFootstep(pId, running);
                PlayerLightFxAmbientNetHandlers.RemoveClonedEmitters(proxy.transform);
                // Do not snap to the local Player; that stacks bodies on join until the first
                // PlayerState. Spawn parks far below; ApplyNetworkState moves on first packet.
                ModRuntime.LegacyInfo($"[Proxy] Created proxy for player {playerId}");

                // A rebuilt proxy (fake-null replace, migration) keeps its RemotePlayerState: forget
                // what the old body showed and show the last known light on the new one.
                PlayerLightStateMessage pendingLight = default;
                bool havePending = _net.PlayerLightFxApplyHandlers.PendingPlayerLights.TryGetValue(playerId, out pendingLight);
                if (_net.RemotePlayers.TryGetValue(playerId, out RemotePlayerState lightState) && lightState != null)
                {
                    lightState.ForgetAppliedLight();
                    if (!havePending && lightState.LastLight.HasValue)
                    {
                        pendingLight = lightState.LastLight.Value;
                        havePending = true;
                    }
                }
                if (havePending)
                {
                    _net.PlayerLightFxApplyHandlers.PendingPlayerLights.Remove(playerId);
                    // Re-enter apply with a temporary receive id so GetProxy path works.
                    int prevRecv = _net.CurrentReceivePlayerId;
                    _net.AssignCurrentReceivePlayerId(playerId);
                    try { _net.PlayerLightFxApplyHandlers.HandlePlayerLightState(pendingLight); }
                    finally { _net.AssignCurrentReceivePlayerId(prevRecv); }
                    ModLog.Event(LogCat.World,
                        $"[Light] applied pending state for p{playerId} after proxy create");
                }

                // Anim library may have arrived before the proxy existed (equip race / late join).
                _net.PlayerFXHandlers?.FlushPendingAnimLibrary(playerId);
            }
        }

        public RemotePlayerProxy GetProxy(int playerId)
        {
            _net.RemoteProxies.TryGetValue(playerId, out var proxy);
            return proxy;
        }

        public int RemotePlayerCount
        {
            get
            {
                int proxies = _net.RemoteProxies.Count;
                int ready = 0;
                foreach (int id in _net.HandshakedPeers)
                {
                    if (id > 0 && id != _net.LocalPlayerId)
                        ready++;
                }
                return NightDeathPolicy.SessionRemoteCount(proxies, ready);
            }
        }

        public IEnumerable<RemotePlayerProxy> GetAllProxies()
        {
            return _net.RemoteProxies.Values;
        }

        /// <summary>
        /// Teleports a remote proxy to an exact world position instantly (no lerp).
        /// Used when entering/exiting dreams so the proxy appears immediately at the
        /// dream spawn instead of slowly lerping across the map.
        /// </summary>
        internal void TeleportRemoteProxyTo(Vector3 position, float rotY = 0f, int playerId = -1)
        {
            // Default to all proxies if no player specified
            if (playerId < 0)
            {
                foreach (var kvp in _net.RemoteProxies)
                {
                    _net.TeleportRemoteProxyTo(position, rotY, kvp.Key);
                }
                return;
            }

            if (!_net.RemoteProxies.TryGetValue(playerId, out var proxy) || proxy == null)
            {
                if (proxy == null && _net.RemoteProxies.ContainsKey(playerId))
                    _net.RemoteProxies.Remove(playerId);
                _net.EnsureRemoteProxy(playerId);
                if (!_net.RemoteProxies.TryGetValue(playerId, out proxy) || proxy == null)
                    return;
            }

            var rb = proxy.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.position = position;
                rb.velocity = Vector3.zero;
            }
            proxy.transform.eulerAngles = new Vector3(90f, rotY, 0f);

            proxy.ApplyNetworkState(new PlayerStateNet
            {
                Position = position,
                TorsoFacingY = (short)rotY,
                Locomotion = SecondPlayerAnimController.LocomotionState.Idle,
                FlipX = false,
                LegFacingY = (short)rotY,
                ReverseLegs = false,
                TorsoClip = "Idle",
                LegsClip = ""
            });
        }

        internal void DestroyRemoteProxy(int playerId)
        {
            if (!_net.RemoteProxies.TryGetValue(playerId, out var proxy))
                return;

            // Before Destroy: vanilla stopAttacking so chase/superTarget do not hold a
            // destroyed Transform (N-peer leave mid-chase → null chase / (0,0,0) wander).
            try
            {
                if (proxy != null)
                    ClearAiTargetsOnProxy(proxy.transform);
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Session, "ClearAiTargetsOnProxy p" + playerId + ": " + ex.Message);
            }

            _net.RemoteProxies.Remove(playerId);
            try
            {
                if (proxy != null && proxy.gameObject != null)
                    UnityEngine.Object.Destroy(proxy.gameObject);
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Session, "DestroyRemoteProxy p" + playerId + ": " + ex.Message);
            }
            ModRuntime.LegacyInfo($"[Proxy] Destroyed proxy for player {playerId}");
        }

        /// <summary>
        /// After local dream pad load: place remotes who are actually in the shared dream
        /// (<see cref="Sync.DreamSyncManager.IsRemoteInDream"/>). Dreams are party-once /
        /// shared-session but peers enter individually — stamping every RemoteProxy yanked
        /// overworld peers onto the dream pad and polluted RemoteOutsideLocation.
        /// </summary>
        internal void ResyncDreamProxiesAfterLocalLoad(string locationName)
        {
            if (string.IsNullOrEmpty(locationName) || !_net.IsConnected) return;
            try
            {
                var ol = Singleton<OutsideLocations>.Instance;
                string canon = Sync.DreamSyncManager.CanonicalDreamLocationName(locationName);
                Location loc = LocationEnterExitNetHandlers.ResolveOutsideLocation(ol, canon);
                if (loc == null && Dreams.Instance != null)
                    loc = Dreams.Instance.dreamLocation;
                if (loc == null) return;

                LocationEnterExitNetHandlers.EnsureEntered(loc);
                foreach (var kvp in new List<KeyValuePair<int, RemotePlayerProxy>>(_net.RemoteProxies))
                {
                    if (kvp.Key == _net.LocalPlayerId) continue;
                    // N-peer: only participants. IsRemoteInDream covers entry-deadline window.
                    if (!Sync.DreamSyncManager.IsRemoteInDream(kvp.Key))
                        continue;
                    _net.RemoteOutsideLocation[kvp.Key] = canon;
                    _net.LocationEnterExitHandlers.PlaceRemoteProxyInOutsideLocation(kvp.Key, loc, preferLastKnown: true);
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Session, "ResyncDreamProxiesAfterLocalLoad: " + ex.Message);
            }
        }


        private static void ClearAiTargetsOnProxy(UnityEngine.Transform proxyT)
        {
            if (proxyT == null) return;
            Character[] all;
            int nAll = CharacterTracker.CopyAll(out all);
            for (int ci = 0; ci < nAll; ci++)
            {
                Character c = all[ci];
                if (c == null) continue;
                if (c.target == proxyT || c.superTarget == proxyT)
                    c.stopAttacking(proxyT);
            }
        }

        private static int _maintenanceTicks; // process-scoped: cleanup cadence counter

        /// <summary>
        /// Host tick (every 0.5 s): periodic cleanup of the melee-hit dedup table. This used to also
        /// "aggro" every hostile creature that could see a stand-in up close onto that stand-in,
        /// whatever it was already chasing (a dog after the host was pulled onto a client within
        /// its near view every half second). Creatures now notice stand-ins in their own sight check
        /// and pick between players through <see cref="PlayerTargetArbiter"/>.
        /// </summary>
        internal void ProxyMaintenanceTick()
        {
            if (_net.RemoteProxies.Count == 0)
                return;
            if (++_maintenanceTicks % 5 == 0)
                MeleeSensorDeduplicatePatch.CleanupStaleEntries();
        }
    }
}
