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
        private static readonly List<string> _pendingDiscoveries = new List<string>(64);

        internal static void ClearPendingDiscoveries()
        {
            _pendingDiscoveries.Clear();
        }

        internal static void QueuePendingDiscovery(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return;
            for (int i = 0; i < _pendingDiscoveries.Count; i++)
            {
                if (_pendingDiscoveries[i] == elementName)
                    return;
            }
            if (_pendingDiscoveries.Count >= MaxPendingDiscoveries)
                _pendingDiscoveries.RemoveAt(0);
            _pendingDiscoveries.Add(elementName);
            ModRuntime.LegacyInfo($"[MapDiscovery] queued '{elementName}' until MapElement ready");
        }

        /// <summary>
        /// Flush discoveries that arrived before MapElements spawned or while OutsideLocation
        /// made Map.showElement(string) miss World-type pins.
        /// </summary>
        public static void TryFlushPendingDiscoveries()
        {
            if (_pendingDiscoveries.Count == 0) return;
            if (!LanNetworkManager.ClientCanApplyWorldBulk() && ModRuntime.Network != null
                && ModRuntime.Network.Role == NetworkRole.Client)
                return;

            for (int i = _pendingDiscoveries.Count - 1; i >= 0; i--)
            {
                string name = _pendingDiscoveries[i];
                if (TryApplyRemoteDiscovery(name))
                    _pendingDiscoveries.RemoveAt(i);
            }
        }

        /// <summary>
        /// Resolve by scene MapElement name (all map types), then showElement(MapElement).
        /// Returns false when the element is not in the scene yet (caller should queue).
        /// </summary>
        internal static bool TryApplyRemoteDiscovery(string elementName)
        {
            if (string.IsNullOrEmpty(elementName)) return true;

            MapElement el = FindMapElementByName(elementName);
            if (el == null)
            {
                // Stale empty SceneScanCache (TTL 3s) while MapElements still spawning.
                WorldQueryHelper.InvalidateSceneScanCache<MapElement>();
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

        private static MapElement FindMapElementByName(string elementName)
        {
            MapElement[] all = WorldQueryHelper.GetCachedSceneComponents<MapElement>();
            if (all == null || all.Length == 0) return null;

            string done = elementName + "_done";
            MapElement fallback = null;
            for (int i = 0; i < all.Length; i++)
            {
                MapElement el = all[i];
                if (el == null || string.IsNullOrEmpty(el.elementName)) continue;
                if (el.elementName == elementName)
                    return el;
                if (fallback == null && el.elementName == done)
                    fallback = el;
            }
            return fallback;
        }
    }
}
