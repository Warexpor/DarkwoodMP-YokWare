using System.Collections.Generic;

namespace DWMPHorde.Networking
{
    /// <summary>Host map: network PlayerId → install-scoped LAN StableClientKey.</summary>
    public sealed partial class LanNetworkManager
    {
        private readonly Dictionary<int, string> _stableKeyByPlayer = new Dictionary<int, string>(8);

        /// <summary>Host: StableClientKey announced in client Handshake (LAN / no Steam).</summary>
        internal bool TryGetStableClientKeyForPlayer(int playerId, out string stableKey)
        {
            stableKey = null;
            if (playerId <= 0)
                return false;
            if (_stableKeyByPlayer.TryGetValue(playerId, out string k)
                && !string.IsNullOrEmpty(k))
            {
                stableKey = k;
                return true;
            }
            return false;
        }

        internal void NoteStableClientKey(int playerId, string rawKey)
        {
            if (playerId <= 0)
                return;
            string key = ClientStateBackup.SanitizeStableClientKey(rawKey);
            if (string.IsNullOrEmpty(key))
                return;
            _stableKeyByPlayer[playerId] = key;
        }

        internal void ClearStableClientKey(int playerId)
        {
            if (playerId > 0)
                _stableKeyByPlayer.Remove(playerId);
        }

        internal void ClearAllStableClientKeys()
        {
            _stableKeyByPlayer.Clear();
        }
    }
}
