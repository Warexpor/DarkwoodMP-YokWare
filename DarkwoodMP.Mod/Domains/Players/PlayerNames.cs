using System.Collections.Generic;
using System.Text;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The names players go by (Multiplayer > Settings > Name), for the name over a player's head
    /// and the chat. A client tells the host its name after the handshake and whenever it changes
    /// (<see cref="NetMessageType.PlayerName"/>); the host hands every name out with the peer roster
    /// (<see cref="PeerRosterEntry.Name"/>), so a late joiner and a promoted host know them all.
    /// </summary>
    internal static class PlayerNames
    {
        public const int MaxLength = 24;

        /// <summary>Host: names the clients reported, by player id.</summary>
        private static readonly Dictionary<int, string> _reported = new Dictionary<int, string>(); // reset-in: Reset
        /// <summary>Everyone's name from the latest roster (host and clients alike).</summary>
        private static readonly Dictionary<int, string> _roster = new Dictionary<int, string>(); // reset-in: Reset
        /// <summary>The name last told to the host (client) or put in the roster (host).</summary>
        private static string _sent; // reset-in: Reset

        public static void Reset()
        {
            _reported.Clear();
            _roster.Clear();
            _sent = null;
        }

        private static string _steamName; // process-scoped: the Steam account's name, read once

        /// <summary>
        /// This player's name: the configured one, cleaned for the wire and the game font. Left at
        /// the default, a Steam player goes by their Steam name.
        /// </summary>
        internal static string LocalName()
        {
            string name = Clean(ModConfig.PlayerName != null ? ModConfig.PlayerName.Value : null);
            if (string.IsNullOrEmpty(name) || name == "Player")
                name = SteamName();
            return string.IsNullOrEmpty(name) ? "Player" : name;
        }

        private static string SteamName()
        {
            if (_steamName != null)
                return _steamName;
            try
            {
                if (!SteamManager.Initialized)
                    return null;
                _steamName = Clean(Steamworks.SteamFriends.GetPersonaName());
            }
            catch
            {
                _steamName = "";
            }
            return _steamName;
        }

        /// <summary>Trimmed, control characters dropped, at most <see cref="MaxLength"/> characters.</summary>
        internal static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsControl(c))
                    continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            return s.Length > MaxLength ? s.Substring(0, MaxLength).TrimEnd() : s;
        }

        /// <summary>
        /// The name shown for player <paramref name="playerId"/>: the one they go by, or
        /// "Player N" while it is unknown or still the default.
        /// </summary>
        internal static string Shown(int playerId)
        {
            var net = ModRuntime.Network;
            string name = null;
            if (net != null && playerId == net.LocalPlayerId)
                name = LocalName();
            else
                _roster.TryGetValue(playerId, out name);
            if (string.IsNullOrEmpty(name) || name == "Player")
                return (Loc.Russian ? "Игрок " : "Player ") + playerId;
            return name;
        }

        /// <summary>Every frame: a client tells the host its name; a host whose own name changed re-sends the roster.</summary>
        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || !net.IsConnected || !net.IsHandshakeComplete)
            {
                // A reconnect (or a new host after a migration) is told again.
                _sent = null;
                return;
            }
            string name = LocalName();
            if (name == _sent)
                return;
            _sent = name;
            if (net.Role == NetworkRole.Client)
            {
                var msg = new PlayerNameMessage { Name = name };
                net.Broadcast(NetMessageType.PlayerName, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                ModLog.Event(LogCat.Session, "[Names] told the host this player's name: " + name);
            }
            else if (net.Role == NetworkRole.Host)
            {
                net.BroadcastPeerRoster();
            }
        }

        /// <summary>Host: a client's name.</summary>
        internal static void HandleName(LanNetworkManager net, PlayerNameMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Host)
                return;
            int id = net.CurrentReceivePlayerId;
            if (id <= 0 || id == net.LocalPlayerId)
                return;
            string name = Clean(msg.Name);
            if (_reported.TryGetValue(id, out string old) && old == name)
                return;
            _reported[id] = name;
            ModLog.Event(LogCat.Session, "[Names] p" + id + " goes by " + name);
            net.BroadcastPeerRoster();
        }

        /// <summary>Host: a peer left.</summary>
        internal static void HostPeerLeft(int playerId) => _reported.Remove(playerId);

        /// <summary>Host: the name to put in the roster for <paramref name="playerId"/>.</summary>
        internal static string ForRoster(LanNetworkManager net, int playerId)
        {
            if (net != null && playerId == net.LocalPlayerId)
                return LocalName();
            if (_reported.TryGetValue(playerId, out string name))
                return name;
            // A promoted host has not heard its peers yet: keep what the old host's roster said.
            return _roster.TryGetValue(playerId, out name) ? name : "";
        }

        /// <summary>Host and clients: the names in the roster just applied.</summary>
        internal static void ApplyRoster(PeerRosterEntry[] entries)
        {
            _roster.Clear();
            if (entries == null)
                return;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].PlayerId > 0 && !string.IsNullOrEmpty(entries[i].Name))
                    _roster[entries[i].PlayerId] = Clean(entries[i].Name);
            }
        }
    }
}
