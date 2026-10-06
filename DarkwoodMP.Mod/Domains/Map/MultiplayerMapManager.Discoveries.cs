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
        /// <summary>A forced rebuild of the element name index at most this often.</summary>
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
        private static Map _discoveryLookupMap;
        private static int _discoveryLookupSig = -1;
        private static float _nextDiscoveryFlushAt;
        private static float _lastDiscoveryRescanAt = -999f;
        private static readonly List<MapElement> _mapElements = new List<MapElement>(512); // process-scoped: scratch
        private static readonly HashSet<MapElement> _mapElementSeen = new HashSet<MapElement>(); // process-scoped: scratch

        internal static void ClearPendingDiscoveries()
        {
            _pendingDiscoveries.Clear();
            _discoveryLookup.Clear();
            _discoveryLookupMap = null;
            _discoveryLookupSig = -1;
            _nextDiscoveryFlushAt = 0f;
            _lastDiscoveryRescanAt = -999f;
        }

        /// <summary>
        /// Every MapElement vanilla's map knows: each map type's elements (what the map draws, also
        /// restored from the save by id) plus elementsToInitialize (every element that ran Start,
        /// named or placed into a type at the next Map.initialize). An element in neither list can
        /// never show on the map, so this is the whole set a discovery can matter for — read from
        /// vanilla's lists instead of a MapElement scene scan (~40 ms). Shared scratch: use it
        /// before the next call.
        /// </summary>
        internal static List<MapElement> CollectMapElements()
        {
            _mapElements.Clear();
            _mapElementSeen.Clear();
            Map map = Map.Instance;
            if (map == null)
                return _mapElements;
            if (map.types != null)
            {
                for (int t = 0; t < map.types.Count; t++)
                {
                    Map.Type type = map.types[t];
                    if (type != null)
                        AddMapElements(type.elements);
                }
            }
            AddMapElements(map.elementsToInitialize);
            _mapElementSeen.Clear();
            return _mapElements;
        }

        private static void AddMapElements(List<MapElement> from)
        {
            if (from == null) return;
            for (int i = 0; i < from.Count; i++)
            {
                MapElement e = from[i];
                if (e != null && _mapElementSeen.Add(e))
                    _mapElements.Add(e);
            }
        }

        /// <summary>Changes when vanilla adds or drops map elements (list sizes), without walking them.</summary>
        private static int MapElementListSignature(Map map)
        {
            int sig = map.elementsToInitialize != null ? map.elementsToInitialize.Count : 0;
            if (map.types != null)
            {
                for (int t = 0; t < map.types.Count; t++)
                {
                    Map.Type type = map.types[t];
                    if (type != null && type.elements != null)
                        sig = sig * 31 + type.elements.Count;
                }
            }
            return sig & int.MaxValue; // never -1 (the forced-rebuild value)
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
        /// made Map.showElement(string) miss World-type pins. Rate-limited; one index rebuild per
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

            // At most one index rebuild for the whole batch (names can be set since the last build).
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
                // Name index from before Map.initialize named the elements; a bulk of misses
                // shares one rebuild instead of one each.
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

        /// <summary>
        /// Force a name index rebuild unless that happened within the interval (names can change
        /// in place: Map.initialize names elements without changing the list sizes).
        /// </summary>
        private static bool TryRescanMapElements(float now)
        {
            if (now - _lastDiscoveryRescanAt < DiscoveryRescanInterval)
                return false;
            _lastDiscoveryRescanAt = now;
            _discoveryLookupSig = -1;
            return true;
        }

        private static MapElement FindMapElementByName(string elementName, Vector3? at)
        {
            Map map = Map.Instance;
            if (map == null) return null;

            // Name index rebuilt only when vanilla's element lists change (or a rescan forces it).
            // A name can repeat.
            int sig = MapElementListSignature(map);
            if (!ReferenceEquals(map, _discoveryLookupMap) || sig != _discoveryLookupSig)
            {
                _discoveryLookup.Clear();
                List<MapElement> all = CollectMapElements();
                for (int i = 0; i < all.Count; i++)
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
                _discoveryLookupMap = map;
                _discoveryLookupSig = sig;
            }
            if (_discoveryLookup.Count == 0) return null;

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
