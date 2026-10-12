using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde;
using LiteNetLib;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A line in the chat when a player is joining, is in the game, or is gone (left, or the
    /// connection was lost). The host knows all three and tells every client
    /// (<see cref="NetMessageType.PeerNotice"/>); each machine words the line in its own language.
    /// Without it a player dropping out showed nowhere but the log.
    /// </summary>
    internal static class PeerNotices
    {
        public const byte Joining = 1;
        public const byte InGame = 2;
        public const byte Left = 3;
        public const byte Lost = 4;

        /// <summary>Host: players already told to be in the game (a world resend loads them again).</summary>
        private static readonly HashSet<int> _inGame = new HashSet<int>(); // reset-in: Reset

        /// <summary>
        /// Host: joiners that dropped the link to load the shared world on their own and will
        /// connect again under a new id, by name, with the time they dropped. Their drop and
        /// return are one join: without this the chat read "joining, left, joining".
        /// </summary>
        private static readonly Dictionary<string, float> _loading = new Dictionary<string, float>(); // reset-in: Reset

        /// <summary>How long a joiner may take to load the world before it counts as gone.</summary>
        private const float LoadingGrace = 180f;

        public static void Reset()
        {
            _inGame.Clear();
            _loading.Clear();
        }

        /// <summary>Host: a new player's name is known (the first thing a joiner sends after the handshake).</summary>
        internal static void HostJoining(LanNetworkManager net, int playerId, string name)
        {
            // Back from loading the world: already announced.
            if (name != null && _loading.Remove(name))
                return;
            HostTell(net, playerId, Joining, name);
        }

        /// <summary>
        /// Host: a joiner dropped the link to load the world (the join's own offline load);
        /// its return is no news, and only a joiner that never comes back is told as gone.
        /// </summary>
        internal static void HostLoadingDetach(LanNetworkManager net, int playerId, string name)
        {
            _inGame.Remove(playerId);
            if (net == null || net.Role != NetworkRole.Host || string.IsNullOrEmpty(name))
                return;
            _loading[name] = UnityEngine.Time.realtimeSinceStartup;
        }

        /// <summary>Host, each frame: joiners that went to load the world and never came back left.</summary>
        internal static void HostTick(LanNetworkManager net)
        {
            if (_loading.Count == 0)
                return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            List<string> gone = null;
            foreach (var kv in _loading)
            {
                if (now - kv.Value > LoadingGrace)
                    (gone ?? (gone = new List<string>())).Add(kv.Key);
            }
            if (gone == null)
                return;
            foreach (string name in gone)
            {
                _loading.Remove(name);
                // No id any more: tell every client.
                var msg = new PeerNoticeMessage { PlayerId = 0, Kind = Left, Name = name };
                Show(msg);
                net.Broadcast(NetMessageType.PeerNotice, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>Host: the player sent its first in-world state.</summary>
        internal static void HostInGame(LanNetworkManager net, int playerId)
        {
            if (_inGame.Add(playerId))
                HostTell(net, playerId, InGame, PlayerNames.Shown(playerId));
        }

        /// <summary>Host: the player is gone; <paramref name="name"/> is read before its records are cleared.</summary>
        internal static void HostGone(LanNetworkManager net, int playerId, string name, bool lost)
        {
            _inGame.Remove(playerId);
            HostTell(net, playerId, lost ? Lost : Left, name);
        }

        private static void HostTell(LanNetworkManager net, int playerId, byte kind, string name)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0)
                return;
            var msg = new PeerNoticeMessage { PlayerId = playerId, Kind = kind, Name = name ?? "" };
            Show(msg);
            net.SendToAllExcept(playerId, NetMessageType.PeerNotice, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        internal static void HandleOnClient(LanNetworkManager net, PeerNoticeMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client || msg.PlayerId == net.LocalPlayerId)
                return;
            Show(msg);
        }

        private static void Show(PeerNoticeMessage msg)
        {
            string name = PlayerNames.Clean(msg.Name);
            if (string.IsNullOrEmpty(name) || name == "Player")
                name = PlayerNames.Shown(msg.PlayerId);
            string line;
            if (Loc.Russian)
            {
                // The fallback name is already "Игрок N".
                string who = name.StartsWith("Игрок ") ? name : "Игрок " + name;
                switch (msg.Kind)
                {
                    case Joining: line = who + " подключается."; break;
                    case InGame: line = who + " в игре."; break;
                    case Left: line = who + " покинул игру."; break;
                    case Lost: line = who + " потерял соединение."; break;
                    default: return;
                }
            }
            else
            {
                switch (msg.Kind)
                {
                    case Joining: line = name + " is joining."; break;
                    case InGame: line = name + " is in the game."; break;
                    case Left: line = name + " left the game."; break;
                    case Lost: line = name + " lost connection."; break;
                    default: return;
                }
            }
            ChatHud.AddSystemLine(line);
        }
    }
}
