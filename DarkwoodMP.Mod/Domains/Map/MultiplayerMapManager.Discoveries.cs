using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// MapElement discovery apply that does not rely on Map.getElement(string).
    /// Vanilla showElement(string) only searches getCurrentType() (WorldGrid / OutsideLocation),
    /// so World pins (Silent Forest / Old Woods hideouts, map-item reveals) are dropped while
    /// a peer is in a bunker / village / doctor house — late-join MapStateSync and live msg 69.
    /// </summary>
    internal static partial class MultiplayerMapManager
    {
        private const int MaxPendingDiscoveries = 512;
        /// <summary>Pending flush runs at most this often.</summary>
        private const float DiscoveryFlushInterval = 1f;
        /// <summary>A fresh MapElement scene scan (FindObjectsOfType) at most this often.</summary>
        private const float DiscoveryRescanInterval = 1f;
        /// <summary>A name whose MapElement never appears is dropped after this long.</summary>
        private const float PendingDiscoveryMaxAge = 300f;

        private struct PendingDiscovery
        {
            public string Name;
            public float QueuedAt;
        }

        private static readonly List<PendingDiscovery> _pendingDiscoveries = new List<PendingDiscovery>(64);
        private static readonly Dictionary<string, MapElement> _discoveryLookup =
            new Dictionary<string, MapElement>(256);
        private static MapElement[] _discoveryLookupSource;
        private static float _nextDiscoveryFlushAt;
        private static float _lastDiscoveryRescanAt = -999f;

        internal static void ClearPendingDiscoveries()
        {
            _pendingDiscoveries.Clear();
            _discoveryLookup.Clear();
            _discoveryLookupSource = null;
        }

        internal static void QueuePendingDiscovery(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return;
            for (int i = 0; i < _pendingDiscoveries.Count; i++)
            {
                if (_pendingDiscoveries[i].Name == elementName)
                    return;
            }
            if (_pendingDiscoveries.Count >= MaxPendingDiscoveries)
                _pendingDiscoveries.RemoveAt(0);
            _pendingDiscoveries.Add(new PendingDiscovery { Name = elementName, QueuedAt = Time.unscaledTime });
            ModRuntime.LegacyInfo($"[MapDiscovery] queued '{elementName}' until MapElement ready");
        }

        /// <summary>
        /// Flush discoveries that arrived before MapElements spawned or while OutsideLocation
        /// made Map.showElement(string) miss World-type pins. Rate-limited; one scene scan per
        /// flush resolves every pending name, and names that never resolve age out.
        /// </summary>
        public static void TryFlushPendingDiscoveries()
        {
            if (_pendingDiscoveries.Count == 0) return;
            float now = Time.unscaledTime;
            if (now < _nextDiscoveryFlushAt) return;
            _nextDiscoveryFlushAt = now + DiscoveryFlushInterval;
            if (!LanNetworkManager.ClientCanApplyWorldBulk() && ModRuntime.Network != null
                && ModRuntime.Network.Role == NetworkRole.Client)
                return;

            // At most one fresh scan for the whole batch (the cache can be an empty early scan).
            TryRescanMapElements(now);

            for (int i = _pendingDiscoveries.Count - 1; i >= 0; i--)
            {
                PendingDiscovery p = _pendingDiscoveries[i];
                if (TryApplyRemoteDiscovery(p.Name, allowRescan: false))
                {
                    _pendingDiscoveries.RemoveAt(i);
                    continue;
                }
                if (now - p.QueuedAt > PendingDiscoveryMaxAge)
                {
                    _pendingDiscoveries.RemoveAt(i);
                    ModRuntime.LegacyInfo($"[MapDiscovery] dropped '{p.Name}' — no MapElement after {PendingDiscoveryMaxAge:F0}s");
                }
            }
        }

        /// <summary>
        /// Resolve by scene MapElement name (all map types), then showElement(MapElement).
        /// Returns false when the element is not in the scene yet (caller should queue).
        /// </summary>
        internal static bool TryApplyRemoteDiscovery(string elementName)
        {
            return TryApplyRemoteDiscovery(elementName, allowRescan: true);
        }

        private static bool TryApplyRemoteDiscovery(string elementName, bool allowRescan)
        {
            if (string.IsNullOrEmpty(elementName)) return true;

            MapElement el = FindMapElementByName(elementName);
            if (el == null)
            {
                // Stale empty SceneScanCache (TTL 3s) while MapElements still spawning; a bulk
                // of misses shares one rescan instead of one FindObjectsOfType each.
                if (!allowRescan || !TryRescanMapElements(Time.unscaledTime))
                    return false;
                el = FindMapElementByName(elementName);
                if (el == null)
                    return false;
            }

            if (el.isOnMap)
                return true;

            // Always under apply guard: pending flush runs outside ProcessInboundMessage
            // and must not re-broadcast via MapElementDiscoverPatch.
            using (new NetworkApplyGuard())
            {
                Map map = Map.Instance;
                if (map != null)
                    map.showElement(el);
                else
                    el.isOnMap = true;
            }

            ModRuntime.LegacyInfo($"[MapDiscovery] remote discovered '{elementName}' — showing locally");
            return true;
        }

        /// <summary>Invalidate the MapElement scan cache unless that happened within the interval.</summary>
        private static bool TryRescanMapElements(float now)
        {
            if (now - _lastDiscoveryRescanAt < DiscoveryRescanInterval)
                return false;
            _lastDiscoveryRescanAt = now;
            WorldQueryHelper.InvalidateSceneScanCache<MapElement>();
            return true;
        }

        private static MapElement FindMapElementByName(string elementName)
        {
            MapElement[] all = WorldQueryHelper.GetCachedSceneComponents<MapElement>();
            if (all == null || all.Length == 0) return null;

            // Name index rebuilt only when the cached scan array changes.
            if (!ReferenceEquals(all, _discoveryLookupSource))
            {
                _discoveryLookup.Clear();
                for (int i = 0; i < all.Length; i++)
                {
                    MapElement e = all[i];
                    if (e == null || string.IsNullOrEmpty(e.elementName)) continue;
                    if (!_discoveryLookup.ContainsKey(e.elementName))
                        _discoveryLookup[e.elementName] = e;
                }
                _discoveryLookupSource = all;
            }

            if (_discoveryLookup.TryGetValue(elementName, out MapElement el) && el != null)
                return el;
            if (_discoveryLookup.TryGetValue(elementName + "_done", out MapElement done) && done != null)
                return done;
            return null;
        }
    }
}
