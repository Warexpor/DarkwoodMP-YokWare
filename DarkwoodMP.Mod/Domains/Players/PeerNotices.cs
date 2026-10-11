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

        public static void Reset() => _inGame.Clear();

        /// <summary>Host: a new player's name is known (the first thing a joiner sends after the handshake).</summary>
        internal static void HostJoining(LanNetworkManager net, int playerId, string name)
            => HostTell(net, playerId, Joining, name);

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
