using DWMPHorde.Networking;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Remembers which remote proxy most recently entered a host EventTriggers
    /// volume. Used so story threats (dream forest spirit, etc.) can spawn/chase
    /// the peer who walked into the area instead of always Player.Instance.
    /// </summary>
    internal static class ThreatTriggerContext
    {
        private static int _lastProxyPlayerId;
        private static float _lastProxyEnterTime = -999f;
        private static Vector3 _lastProxyEnterPos;

        public static void NoteProxyEnter(RemotePlayerProxy proxy, Vector3 volumePos)
        {
            if (proxy == null) return;
            _lastProxyPlayerId = proxy.PlayerId;
            _lastProxyEnterTime = Time.unscaledTime;
            _lastProxyEnterPos = volumePos;
        }

        public static void NoteHostEnter()
        {
            // Host body entered a volume — clear proxy preference so attackPlayer
            // / dream spirit spawn use nearest/host normally.
            _lastProxyPlayerId = 0;
            _lastProxyEnterTime = -999f;
        }

        public static void Reset()
        {
            _lastProxyPlayerId = 0;
            _lastProxyEnterTime = -999f;
            _lastProxyEnterPos = Vector3.zero;
        }

        public static Transform TryGetRecentProxyTransform(float maxAgeSec)
        {
            if (_lastProxyPlayerId <= 0) return null;
            if (Time.unscaledTime - _lastProxyEnterTime > maxAgeSec) return null;
            var net = ModRuntime.Network;
            if (net == null) return null;
            RemotePlayerProxy proxy = net.GetProxy(_lastProxyPlayerId);
            if (proxy == null) return null;
            CharBase cb = proxy.CachedCharBase;
            if (cb != null && (!cb.alive || cb.invisible || cb.ignoreMe))
                return null;
            return proxy.transform;
        }

        /// <summary>
        /// Same memory, but only if that enter happened in the slot of <paramref name="pad"/> (a
        /// dream pad): a door in the meadow must not count as the dream bunker.
        /// </summary>
        public static Transform TryGetRecentProxyOnPad(Transform pad, float maxAgeSec)
        {
            Transform t = TryGetRecentProxyTransform(maxAgeSec);
            return t != null && DWMPHorde.Sync.DreamSyncManager.IsInPadSlot(pad, _lastProxyEnterPos) ? t : null;
        }

        public static Vector3? TryGetRecentProxyPosition(float maxAgeSec)
        {
            Transform t = TryGetRecentProxyTransform(maxAgeSec);
            return t != null ? t.position : (Vector3?)null;
        }
    }
}
