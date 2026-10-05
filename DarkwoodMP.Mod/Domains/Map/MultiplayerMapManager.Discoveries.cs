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
            public Vector3? At;
            public float QueuedAt;
        }

        private static readonly List<PendingDiscovery> _pendingDiscoveries = new List<PendingDiscovery>(64);
        private static readonly Dictionary<string, List<MapElement>> _discoveryLookup =
            new Dictionary<string, List<MapElement>>(256);
        private static MapElement[] _discoveryLookupSource;
        private static float _nextDiscoveryFlushAt;
        private static float _lastDiscoveryRescanAt = -999f;

        internal static void ClearPendingDiscoveries()
        {
            _pendingDiscoveries.Clear();
            _discoveryLookup.Clear();
            _discoveryLookupSource = null;
            _nextDiscoveryFlushAt = 0f;
            _lastDiscoveryRescanAt = -999f;
        }

        internal static void QueuePendingDiscovery(string elementName, Vector3? at = null)
        {
            if (string.IsNullOrEmpty(elementName)) return;
            for (int i = 0; i < _pendingDiscoveries.Count; i++)
            {
                PendingDiscovery q = _pendingDiscoveries[i];
                if (q.Name == elementName && q.At.HasValue == at.HasValue
                    && (!at.HasValue || (q.At.Value - at.Value).sqrMagnitude < 1f))
                    return;
            }
            if (_pendingDiscoveries.Count >= MaxPendingDiscoveries)
                _pendingDiscoveries.RemoveAt(0);
            _pendingDiscoveries.Add(new PendingDiscovery { Name = elementName, At = at, QueuedAt = Time.unscaledTime });
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
                if (TryApplyRemoteDiscovery(p.Name, p.At, allowRescan: false))
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
        internal static bool TryApplyRemoteDiscovery(string elementName, Vector3? at = null)
        {
            return TryApplyRemoteDiscovery(elementName, at, allowRescan: true);
        }

        private static bool TryApplyRemoteDiscovery(string elementName, Vector3? at, bool allowRescan)
        {
            if (string.IsNullOrEmpty(elementName)) return true;

            MapElement el = FindMapElementByName(elementName, at);
            if (el == null)
            {
                // Stale empty SceneScanCache (TTL 3s) while MapElements still spawning; a bulk
                // of misses shares one rescan instead of one FindObjectsOfType each.
                if (!allowRescan || !TryRescanMapElements(Time.unscaledTime))
                    return false;
                el = FindMapElementByName(elementName, at);
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

        private static MapElement FindMapElementByName(string elementName, Vector3? at)
        {
            MapElement[] all = WorldQueryHelper.GetCachedSceneComponents<MapElement>();
            if (all == null || all.Length == 0) return null;

            // Name index rebuilt only when the cached scan array changes. A name can repeat.
            if (!ReferenceEquals(all, _discoveryLookupSource))
            {
                _discoveryLookup.Clear();
                for (int i = 0; i < all.Length; i++)
                {
                    MapElement e = all[i];
                    if (e == null || string.IsNullOrEmpty(e.elementName)) continue;
                    if (!_discoveryLookup.TryGetValue(e.elementName, out List<MapElement> list))
                    {
                        list = new List<MapElement>(1);
                        _discoveryLookup[e.elementName] = list;
                    }
                    list.Add(e);
                }
                _discoveryLookupSource = all;
            }

            MapElement el = Pick(elementName, at);
            return el != null ? el : Pick(elementName + "_done", at);
        }

        /// <summary>The one at <paramref name="at"/> when known; else one not yet on the map; else any.</summary>
        private static MapElement Pick(string name, Vector3? at)
        {
            if (!_discoveryLookup.TryGetValue(name, out List<MapElement> list) || list.Count == 0)
                return null;
            MapElement best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                MapElement e = list[i];
                if (e == null) continue;
                float d;
                if (at.HasValue)
                {
                    Vector3 p = e.transform.position;
                    float dx = p.x - at.Value.x, dz = p.z - at.Value.z;
                    d = dx * dx + dz * dz;
                }
                else
                    d = e.isOnMap ? 1f : 0f;
                if (d < bestD)
                {
                    bestD = d;
                    best = e;
                }
            }
            return best;
        }
    }
}
