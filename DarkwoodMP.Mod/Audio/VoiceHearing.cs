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
    /// Radio talk also plays out loud from every other player's live walkie (PlayerState
    /// <c>WalkieState</c>), a small sound where that player stands, whispers included; a radio
    /// howling with feedback is heard much farther.
    /// Config <c>[Voice] VoiceAlertsEnemies</c> (host's setting).
    /// </summary>
    internal static class VoiceHearing
    {
        private const float WindowSec = 0.5f;
        /// <summary>Below this loudness (0..1) talk is a murmur nothing hears.</summary>
        private const float Murmur = 0.45f;
        private const float NearRange = 80f;
        private const float ShoutRange = 450f;
        /// <summary>A walkie playing a transmission: anything above silence, heard this far around it.</summary>
        private const float RadioSilence = 0.15f;
        private const float RadioRange = 160f;
        private const float RadioPocketRange = 100f;
        /// <summary>Feedback howling from a radio: loud and shrill, heard far.</summary>
        private const float HowlRange = 420f;

        private static readonly Dictionary<int, float> _peak = new Dictionary<int, float>(); // reset-in: Reset
        /// <summary>Who talked on the radio this half second (their own radio does not play it).</summary>
        private static readonly HashSet<int> _radioTalkers = new HashSet<int>(); // reset-in: Reset
        private static readonly List<int> _ids = new List<int>(); // process-scoped: scratch
        private static float _nextWindow; // reset-in: Reset

        internal static void Reset()
        {
            _peak.Clear();
            _radioTalkers.Clear();
            _nextWindow = 0f;
        }

        internal static void Forget(int playerId)
        {
            _peak.Remove(playerId);
            _radioTalkers.Remove(playerId);
        }

        /// <summary>A packet of <paramref name="playerId"/>'s voice at loudness <paramref name="level"/> (0..1).</summary>
        internal static void Heard(int playerId, float level, bool walkie)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0 || !Enabled)
                return;
            if (walkie && level >= RadioSilence)
                _radioTalkers.Add(playerId);
            if (level < Murmur)
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
                _radioTalkers.Clear();
                return;
            }
            if (Time.unscaledTime < _nextWindow)
                return;
            _nextWindow = Time.unscaledTime + WindowSec;
            if (_peak.Count == 0 && _radioTalkers.Count == 0)
                return;
            if (!Enabled || Core.loadingGame || Core.mainMenu)
            {
                _peak.Clear();
                _radioTalkers.Clear();
                return;
            }
            if (_radioTalkers.Count > 0)
                AlertRadios(net);
            // A radio howling with feedback (worked out by this player's own voice playback).
            if (VoiceChatService.HowlNow > 0.3f)
                Character.alertInArea(VoiceChatService.HowlAt, HowlRange * VoiceChatService.HowlNow, dangerousSound: false, 1f);

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

        /// <summary>
        /// Every live walkie but the talker's own plays the transmission out loud where its
        /// carrier stands: louder in hand than in a pocket, and not at all where the signal does
        /// not reach (too far, underground) or the radio is off or flat.
        /// </summary>
        private static void AlertRadios(LanNetworkManager net)
        {
            bool onlyTalker(int id) => _radioTalkers.Count == 1 && _radioTalkers.Contains(id);
            Vector3 talkerPos = Vector3.zero;
            bool talkerUnder = false;
            bool found = false;
            foreach (int t in _radioTalkers)
            {
                if (TryPosition(net, t, out talkerPos))
                {
                    talkerUnder = WalkieStates.IsUnderground(StateOf(net, t));
                    found = true;
                    break;
                }
            }
            _radioTalkers.Clear();
            if (!found)
                return;

            AlertRadio(net, net.LocalPlayerId, VoiceChatService.LocalWalkieState, talkerPos, talkerUnder, onlyTalker(net.LocalPlayerId));
            foreach (KeyValuePair<int, RemotePlayerState> kv in net.RemotePlayers)
            {
                if (kv.Value != null)
                    AlertRadio(net, kv.Key, kv.Value.WalkieState, talkerPos, talkerUnder, onlyTalker(kv.Key));
            }
        }

        private static void AlertRadio(LanNetworkManager net, int id, byte state, Vector3 talkerPos, bool talkerUnder, bool isTalker)
        {
            if (isTalker || !WalkieStates.Live(state) || !TryPosition(net, id, out Vector3 pos))
                return;
            float q = VoiceChatService.RadioQuality(talkerPos, pos, false, false, talkerUnder, WalkieStates.IsUnderground(state));
            if (q <= 0.03f)
                return;
            float range = WalkieStates.InHand(state) ? RadioRange : RadioPocketRange;
            Character.alertInArea(pos, range, dangerousSound: false, 1f);
        }

        private static byte StateOf(LanNetworkManager net, int id)
        {
            if (id == net.LocalPlayerId)
                return VoiceChatService.LocalWalkieState;
            return net.RemotePlayers.TryGetValue(id, out RemotePlayerState st) && st != null ? st.WalkieState : (byte)0;
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
