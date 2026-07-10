using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DWMPHorde.Players
{
    public static class PlayerControlRouter
    {
        private static Player _main;
        private static readonly Dictionary<int, Player> _proxies = new Dictionary<int, Player>();

        public static Player MainPlayer => _main;

        public static bool HasSecond => _proxies.Count > 0;

        public static void EnsureMainRegistered()
        {
            if (_main != null)
                return;

            Player scenePlayer = ResolveSceneMainPlayer();
            if (scenePlayer != null)
                RegisterMain(scenePlayer);
        }

        private static Player ResolveSceneMainPlayer()
        {
            GameObject tagged = GameObject.FindGameObjectWithTag("Player");
            if (tagged != null)
            {
                Player taggedPlayer = tagged.GetComponent<Player>();
                if (taggedPlayer != null && taggedPlayer.GetComponent<CoopPlayerMarker>() == null)
                    return taggedPlayer;
            }

            Player instance = Player.Instance;
            if (instance != null && instance.GetComponent<CoopPlayerMarker>() == null)
                return instance;

            return null;
        }

        public static void RegisterMain(Player player)
        {
            if (player == null || player.GetComponent<CoopPlayerMarker>() != null)
                return;

            _main = player;
        }

        private static int _nextAutoId = -1;

        public static void RegisterSecond(Player player)
        {
            if (player == null) return;
            while (_proxies.ContainsKey(_nextAutoId))
                _nextAutoId--;
            _proxies[_nextAutoId--] = player;
        }

        public static void RegisterProxy(int playerId, Player player)
        {
            if (player == null)
                return;
            _proxies[playerId] = player;
        }

        public static void UnregisterProxy(int playerId)
        {
            _proxies.Remove(playerId);
        }

        public static Player GetProxy(int playerId)
        {
            _proxies.TryGetValue(playerId, out var player);
            return player;
        }

        public static IEnumerable<Player> GetAllProxies()
        {
            return _proxies.Values;
        }

        /// <summary>Returns the proxy Player matching the given instance, or null.</summary>
        public static Player GetProxyByInstance(Player instance)
        {
            if (instance == null) return null;
            foreach (var p in _proxies.Values)
            {
                if (p == instance)
                    return p;
            }
            return null;
        }

        public static void ClearAllProxies()
        {
            _proxies.Clear();
        }

        public static Player GetMainForVision()
        {
            EnsureMainRegistered();
            return _main != null ? _main : Player.Instance;
        }
    }
}