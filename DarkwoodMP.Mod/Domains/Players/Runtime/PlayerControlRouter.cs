using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DWMPHorde.Players
{
    public static class PlayerControlRouter
    {
        private static Player _main; // process-scoped: the local player, re-registered by registerMe on each world load
        private static readonly Dictionary<int, Player> _proxies = new Dictionary<int, Player>();

        public static Player MainPlayer => _main;

        /// <summary>
        /// True while at least one registered proxy / second player still exists. Destroyed
        /// entries (Unity-null) are pruned first so a torn-down proxy cannot keep this true.
        /// </summary>
        public static bool HasSecond
        {
            get
            {
                PruneDestroyed();
                return _proxies.Count > 0;
            }
        }

        private static void PruneDestroyed()
        {
            if (_proxies.Count == 0) return;
            List<int> dead = null;
            foreach (var kv in _proxies)
            {
                if (kv.Value == null)
                {
                    if (dead == null) dead = new List<int>();
                    dead.Add(kv.Key);
                }
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++)
                _proxies.Remove(dead[i]);
        }

        public static void RegisterMain(Player player)
        {
            if (player == null || player.GetComponent<CoopPlayerMarker>() != null)
                return;

            _main = player;
        }

        private static int _nextAutoId = -1;

        /// <summary>
        /// Network stop: every registered proxy belongs to the session (StopNetwork destroys them),
        /// and Unity destroys them only at frame end, so HasSecond would stay true until then.
        /// </summary>
        internal static void Reset()
        {
            _proxies.Clear();
            _nextAutoId = -1;
        }

        /// <summary>
        /// Idempotent: a local co-op clone is registered by PlayerProxyBuilder and again by the
        /// registerMe prefix; the second call must not add a duplicate entry.
        /// </summary>
        public static void RegisterSecond(Player player)
        {
            if (player == null) return;
            if (GetProxyByInstance(player) != null) return;
            while (_proxies.ContainsKey(_nextAutoId))
                _nextAutoId--;
            _proxies[_nextAutoId--] = player;
        }

        public static IEnumerable<Player> GetAllProxies()
        {
            PruneDestroyed();
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
    }
}