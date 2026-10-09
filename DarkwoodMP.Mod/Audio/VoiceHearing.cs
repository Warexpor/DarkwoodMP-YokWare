using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Host: creatures hear players talk. Twice a second, each player who spoke louder than a
    /// murmur in that half second makes a sound where they stand, through vanilla's own
    /// <c>Character.alertInArea</c> (the call a footstep, a door or a shot makes): normal speech
    /// carries about as far as a walking step, a shout farther than running. Whispering stays
    /// unheard. The loudness is what the talker's own client measured
    /// (<see cref="VoiceDataMessage.Level"/>), so the host needs no Steam for it.
    /// Config <c>[Voice] VoiceAlertsEnemies</c> (host's setting).
    /// </summary>
    internal static class VoiceHearing
    {
        private const float WindowSec = 0.5f;
        /// <summary>Below this loudness (0..1) talk is a murmur nothing hears.</summary>
        private const float Murmur = 0.45f;
        private const float NearRange = 80f;
        private const float ShoutRange = 450f;

        private static readonly Dictionary<int, float> _peak = new Dictionary<int, float>(); // reset-in: Reset
        private static readonly List<int> _ids = new List<int>(); // process-scoped: scratch
        private static float _nextWindow; // reset-in: Reset

        internal static void Reset()
        {
            _peak.Clear();
            _nextWindow = 0f;
        }

        internal static void Forget(int playerId) => _peak.Remove(playerId);

        /// <summary>A packet of <paramref name="playerId"/>'s voice at loudness <paramref name="level"/> (0..1).</summary>
        internal static void Heard(int playerId, float level)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0 || level < Murmur || !Enabled)
                return;
            if (!_peak.TryGetValue(playerId, out float peak) || level > peak)
                _peak[playerId] = level;
        }

        private static bool Enabled => ModConfig.VoiceAlertsEnemies == null || ModConfig.VoiceAlertsEnemies.Value;

        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host)
            {
                _peak.Clear();
                return;
            }
            if (Time.unscaledTime < _nextWindow)
                return;
            _nextWindow = Time.unscaledTime + WindowSec;
            if (_peak.Count == 0)
                return;
            if (!Enabled || Core.loadingGame || Core.mainMenu)
            {
                _peak.Clear();
                return;
            }

            _ids.Clear();
            _ids.AddRange(_peak.Keys);
            foreach (int id in _ids)
            {
                float level = _peak[id];
                if (TryPosition(net, id, out Vector3 pos))
                {
                    float range = Mathf.Lerp(NearRange, ShoutRange, Mathf.InverseLerp(Murmur, 1f, level));
                    Character.alertInArea(pos, range, dangerousSound: false, 1f);
                }
            }
            _peak.Clear();
        }

        /// <summary>Where a living player stands: the host's own body, or a client's stand-in.</summary>
        private static bool TryPosition(LanNetworkManager net, int id, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (id == net.LocalPlayerId)
            {
                Player p = Player.Instance;
                if (p == null || !p.alive || p.dying)
                    return false;
                pos = p._transform != null ? p._transform.position : p.transform.position;
                return true;
            }
            var proxy = net.GetProxy(id);
            if (proxy == null || !proxy.isActiveAndEnabled)
                return false;
            CharBase cb = proxy.CachedCharBase;
            if (cb != null && !cb.alive)
                return false;
            pos = proxy.transform.position;
            return true;
        }
    }
}
