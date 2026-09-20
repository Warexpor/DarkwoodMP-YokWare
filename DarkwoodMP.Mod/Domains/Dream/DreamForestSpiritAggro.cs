using DWMPHorde.Networking;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Dream bunker forest spirit must stick to whoever triggered the runaway spawn.
    /// Global ThreatTriggerContext retarget (later peer entering the volume) was making
    /// a host-off-route spirit DamagePlayer a far-away client still on the path.
    /// </summary>
    public static class DreamForestSpiritAggro
    {
        private static int _ownerPlayerId;
        private static float _until = -999f;
        private const float StickSec = 180f;

        public static void BindOwner(int playerId)
        {
            _ownerPlayerId = playerId;
            _until = Time.unscaledTime + StickSec;
        }

        public static void Reset()
        {
            _ownerPlayerId = 0;
            _until = -999f;
        }

        public static bool IsBunkerDreamSpirit(Character c)
        {
            if (c == null) return false;
            string n = c.name ?? "";
            return n.IndexOf("forestSpirit_bunkerDream", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("forestSpirit_bunker", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static Transform TryGetStickyTarget()
        {
            if (_ownerPlayerId <= 0) return null;
            if (Time.unscaledTime > _until) return null;

            var net = LanNetworkManager.Instance;
            if (net == null) return null;

            if (_ownerPlayerId == net.LocalPlayerId)
            {
                Player host = Player.Instance;
                if (host == null) return null;
                CharBase cb = host.GetComponent<CharBase>();
                if (cb != null && (!cb.alive || cb.invisible || cb.ignoreMe))
                    return null;
                return host._transform != null ? host._transform : host.transform;
            }

            RemotePlayerProxy proxy = net.GetProxy(_ownerPlayerId);
            if (proxy == null) return null;
            CharBase pcb = proxy.GetComponent<CharBase>();
            if (pcb != null && (!pcb.alive || pcb.invisible || pcb.ignoreMe))
                return null;
            return proxy.transform;
        }
    }
}
