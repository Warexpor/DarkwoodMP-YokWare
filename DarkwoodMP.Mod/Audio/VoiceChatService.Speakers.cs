using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>Playback: one 3D speaker per talker, placed and muffled each frame.</summary>
    public static partial class VoiceChatService
    {
        /// <summary>
        /// A speaker that has had no voice data this long and an empty buffer is reaped (it is
        /// re-created on the next packet). Peer leave is handled sooner by <see cref="RemoveSpeaker"/>.
        /// </summary>
        private const float IdleReapSec = 30f;

        /// <summary>A whisper carries this share of <c>VoiceMaxDistance</c>; a shout all of it.</summary>
        private const float QuietRangeShare = 0.35f;
        /// <summary>Seconds for the loudness envelope to fall from a shout to silence (it rises at once).</summary>
        private const float LevelReleaseSec = 1.5f;
        /// <summary>Vanilla's sound-blocking layers (<c>Character.heardSound</c>: walls and solid world).</summary>
        private const int OcclusionMask = 32769;
        /// <summary>Close by, a voice is mostly in the middle; it pans fully from this far out.</summary>
        private const float FullPanDistance = 300f;
        /// <summary>Another player's walkie playing out loud: heard this far, at full volume this close.</summary>
        internal const float NearRadioRange = 260f;
        private const float NearRadioFull = 45f;
        private const float NearRadioVolume = 0.7f;
        private const float WallVolume = 0.75f;
        private const float WallCutoff = 1500f;
        /// <summary>A radio stays held by its talker this long after their last packet.</summary>
        private const float ChannelHoldSec = 0.35f;

        private static int _radioHolder; // reset-in: Reset
        private static float _radioHolderAt; // reset-in: Reset

        /// <summary>A walkie packet from <paramref name="id"/>: it takes the channel if nobody is on it.</summary>
        private static void ClaimChannel(int id)
        {
            if (_radioHolder == 0 || _radioHolder == id || Time.unscaledTime - _radioHolderAt > ChannelHoldSec)
            {
                _radioHolder = id;
                _radioHolderAt = Time.unscaledTime;
            }
        }

        private static bool HoldsChannel(int id)
            => _radioHolder == id && Time.unscaledTime - _radioHolderAt <= ChannelHoldSec;

        /// <summary>A peer stands inside a building (vanilla <c>CharBase.isInside</c>, its ground refreshed first: the stand-in has no tick of its own).</summary>
        private static bool IsInside(LanNetworkManager net, int playerId)
        {
            var proxy = playerId > 0 && net != null ? net.GetProxy(playerId) : null;
            if (proxy == null)
                return false;
            WorldProxyEffectNetHandlers.RefreshStandInGround(proxy);
            CharBase cb = proxy.CachedCharBase;
            return cb != null && cb.isInside;
        }

        private static Speaker EnsureSpeaker(int id)
        {
            if (_speakers.TryGetValue(id, out Speaker existing) && existing.Go != null)
                return existing;
            Speaker s = CreateSpeaker(id);
            _speakers[id] = s;
            return s;
        }

        private static Speaker CreateSpeaker(int id)
        {
            if (_root == null)
            {
                _root = new GameObject("YokWare_Voice");
                UnityEngine.Object.DontDestroyOnLoad(_root);
            }

            var s = new Speaker
            {
                Id = id,
                Ring = new float[Math.Max((int)_sampleRate * 4, 44100)]
            };
            s.Go = new GameObject("YokWare_Voice_" + id);
            s.Go.transform.SetParent(_root.transform, false);
            s.Src = s.Go.AddComponent<AudioSource>();
            s.Beh = s.Go.AddComponent<VoiceSpeakerBehaviour>();
            s.Beh.S = s;
            s.Beh.SrcRate = (int)_sampleRate;
            s.Muffle = s.Go.AddComponent<AudioLowPassFilter>();
            s.Muffle.cutoffFrequency = 22000f;
            s.Src.clip = CarrierClip();
            s.Src.loop = true;
            s.Src.playOnAwake = false;
            s.Src.volume = 1f;
            // Panned toward where the talker stands; the distance falloff is ours (by how loud they
            // spoke), so Unity's own rolloff is flat and doppler off (it would bend a moving voice).
            s.Src.spatialBlend = 0f;
            s.Src.rolloffMode = AudioRolloffMode.Custom;
            s.Src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
            s.Src.minDistance = 1f;
            s.Src.maxDistance = 100000f;
            s.Src.dopplerLevel = 0f;
            s.Src.spread = 60f;
            // Same indoor reverb vanilla puts on a sound made inside (default filter, toggled).
            s.Reverb = s.Go.AddComponent<AudioReverbFilter>();
            s.Reverb.enabled = false;
            s.Src.Play();

            var click = new GameObject("YokWare_VoiceClick_" + id);
            click.transform.SetParent(s.Go.transform, false);
            s.Click = click.AddComponent<AudioSource>();
            s.Click.playOnAwake = false;
            s.Click.spatialBlend = 1f;
            s.Click.rolloffMode = AudioRolloffMode.Linear;
            s.Click.minDistance = NearRadioFull;
            s.Click.maxDistance = NearRadioRange;
            s.Click.dopplerLevel = 0f;
            s.ClickMuffle = click.AddComponent<AudioLowPassFilter>();
            s.ClickMuffle.cutoffFrequency = 22000f;
            s.ClickReverb = click.AddComponent<AudioReverbFilter>();
            s.ClickReverb.enabled = false;
            return s;
        }

        private static void UpdateSpeakers()
        {
            if (Time.unscaledTime >= _nextStatsLog)
            {
                _nextStatsLog = Time.unscaledTime + 10f;
                if (_txPackets > 0 || HasRecentRx())
                {
                    ModLog.Trace(LogCat.Audio, () =>
                    {
                        string line = "[Voice] 10s tx=" + _txPackets + WorldSoundPickup.TakeStats();
                        foreach (Speaker s in _speakers.Values)
                        {
                            int buffered, under;
                            lock (s.Lock) { buffered = s.Buffered; under = s.Underruns; s.PacketsIn = 0; s.Underruns = 0; }
                            line += " | p" + s.Id + " buf=" + buffered + " under=" + under
                                + " level=" + s.LevelEnv.ToString("0.00") + (s.RadioMode ? " radio" : "")
                                + (s.OccludedNow ? " walled" : "");
                        }
                        return line;
                    });
                }
                _txPackets = 0;
            }

            if (_speakers.Count == 0)
                return;

            float dt = Time.unscaledDeltaTime;
            float vol = ModConfig.VoiceVolume?.Value ?? 1f;
            float rangeFull = ModConfig.VoiceFullVolumeDistance?.Value ?? 150f;
            float rangeMax = ModConfig.VoiceMaxDistance?.Value ?? LocalAudioService.DefaultMaxAudioDistance;
            // Heard from where this player listens (the followed player while spectating).
            Vector3 listen = LocalAudioService.GetListenPosition();

            _reap.Clear();
            foreach (Speaker s in _speakers.Values)
            {
                if (s.Go == null || (Time.unscaledTime - s.LastData > IdleReapSec && s.Buffered == 0))
                {
                    _reap.Add(s.Id);
                    continue;
                }

                float since = Time.unscaledTime - s.LastData;
                lock (s.Lock)
                {
                    if (s.Priming && s.Buffered > 0 && since > PrimeFlushSec)
                        s.PrimeRelease = true;
                }

                // Walkie packets stopped: the talker let go of the key.
                if (s.WalkieActive && since > 0.3f)
                {
                    s.WalkieActive = false;
                    PlayTalkerClick(s, keyDown: false);
                }

                if (s.RadioWasActive && since > 0.3f)
                {
                    s.RadioWasActive = false;
                    WriteSquelchTail(s);
                }

                // Loudness: up at once with the voice, down slowly between words.
                float level = since > 0.4f ? 0f : s.Level;
                s.LevelEnv = level > s.LevelEnv ? level : Mathf.MoveTowards(s.LevelEnv, level, dt / LevelReleaseSec);

                var net = ModRuntime.Network;
                float proxVol = 0f;
                float cutoff = 22000f;
                float dist = float.MaxValue;
                Vector3 talkerPos = Vector3.zero;
                var proxy = net?.GetProxy(s.Id);
                bool talkerHere = Player.Instance != null && proxy != null && proxy.transform != null;
                if (talkerHere)
                {
                    talkerPos = proxy.transform.position;
                    dist = DistXz(listen, talkerPos);

                    // The louder they spoke, the farther it carries.
                    float loud = Mathf.Lerp(QuietRangeShare, 1f, s.LevelEnv);
                    float range = Mathf.Max(rangeMax * loud, 60f);
                    float full = Mathf.Min(rangeFull * loud, range * 0.5f);
                    float t = Mathf.InverseLerp(range, full, dist);
                    proxVol = t * vol;
                    cutoff = Mathf.Lerp(4500f, 22000f, t);

                    if (Time.unscaledTime >= s.NextOcclusionCheck)
                    {
                        s.NextOcclusionCheck = Time.unscaledTime + 0.15f;
                        s.OccludedNow = IsOccluded(listen, talkerPos);
                    }
                    s.Occlusion = Mathf.MoveTowards(s.Occlusion, s.OccludedNow ? 1f : 0f, dt * 5f);
                    // Vanilla's own muffle for a sound behind a wall (AudioController: 0.75 volume, 1500 Hz).
                    proxVol *= Mathf.Lerp(1f, WallVolume, s.Occlusion);
                    cutoff = Mathf.Lerp(cutoff, Mathf.Min(cutoff, WallCutoff), s.Occlusion);
                }

                // On the radio: this player's own walkie, or the nearest other player's walkie
                // playing it out loud (a small speaker, heard a short way). Radios are half
                // duplex: this player's own radio is silent while they key it. A channel carries
                // the talker who keyed first; a second one keying over them is heard garbled
                // under static.
                bool holder = s.WalkieActive && HoldsChannel(s.Id);
                bool doubling = s.WalkieActive && !holder && _radioHolder != 0 && HoldsChannel(_radioHolder);
                bool onAir = holder || doubling;
                float airShare = doubling ? DoublingUnderShare : 1f;
                bool talkerUnder = PeerUnderground(net, s.Id);
                bool talkerInside = talkerHere && IsInsideCached(s, net, s.Id);

                byte local = LocalWalkieState;
                float ownRadioVol = 0f;
                float ownQuality = 0f;
                if (onAir && WalkieStates.Live(local) && !_walkieTx && talkerHere)
                {
                    ownQuality = RadioQuality(talkerPos, listen, talkerInside, Player.Instance != null && Player.Instance.isInside,
                        talkerUnder, LocalUnderground);
                    if (ownQuality > RadioSquelchQuality)
                        ownRadioVol = vol * 0.9f * airShare * (WalkieStates.InHand(local) ? 1f : PocketVolume);
                }
                float nearRadioVol = 0f;
                float nearQuality = 0f;
                Vector3 nearRadioPos = Vector3.zero;
                float nearRadioCutoff = 22000f;
                int nearRadioId = onAir && net != null && talkerHere ? NearestRadio(net, s.Id, listen, out nearRadioPos) : 0;
                if (nearRadioId != 0)
                {
                    byte carrier = net.RemotePlayers.TryGetValue(nearRadioId, out RemotePlayerState cst) && cst != null ? cst.WalkieState : (byte)0;
                    nearQuality = RadioQuality(talkerPos, nearRadioPos, talkerInside, IsInside(net, nearRadioId),
                        talkerUnder, WalkieStates.IsUnderground(carrier));
                    if (nearQuality > RadioSquelchQuality)
                    {
                        bool hand = WalkieStates.InHand(carrier);
                        float d = DistXz(listen, nearRadioPos);
                        float range = hand ? NearRadioRange : NearRadioRange * 0.7f;
                        nearRadioVol = Mathf.InverseLerp(range, NearRadioFull, d) * vol * NearRadioVolume * airShare * (hand ? 1f : PocketVolume);
                        if (nearRadioId != s.NearRadioId || Time.unscaledTime >= s.NextRadioOcclusionCheck)
                        {
                            s.NextRadioOcclusionCheck = Time.unscaledTime + 0.15f;
                            s.RadioOccludedNow = IsOccluded(listen, nearRadioPos);
                        }
                        s.RadioOcclusion = Mathf.MoveTowards(s.RadioOcclusion, s.RadioOccludedNow ? 1f : 0f, dt * 5f);
                        nearRadioVol *= Mathf.Lerp(1f, WallVolume, s.RadioOcclusion);
                        nearRadioCutoff = Mathf.Lerp(hand ? 22000f : PocketCutoff, WallCutoff, s.RadioOcclusion);
                    }
                }
                s.NearRadioId = nearRadioId;

                // The loudest way wins; the one playing keeps it until another is clearly louder.
                HearMode mode = s.Mode;
                float current = mode == HearMode.OwnRadio ? ownRadioVol : mode == HearMode.NearRadio ? nearRadioVol : proxVol;
                current *= 1.15f;
                if (proxVol > current) { mode = HearMode.Direct; current = proxVol * 1.15f; }
                if (ownRadioVol > current) { mode = HearMode.OwnRadio; current = ownRadioVol * 1.15f; }
                if (nearRadioVol > current) mode = HearMode.NearRadio;
                if (mode == HearMode.OwnRadio && ownRadioVol <= 0f) mode = HearMode.Direct;
                if (mode == HearMode.NearRadio && nearRadioVol <= 0f) mode = HearMode.Direct;
                s.Mode = mode;
                s.RadioMode = mode != HearMode.Direct;

                // The signal on the radio it is heard through: static grows as it weakens, and
                // a weak one breaks up (decode side).
                s.RadioQuality = mode == HearMode.NearRadio ? nearQuality : mode == HearMode.OwnRadio ? ownQuality : 0f;
                s.FarStatic = FarStaticGain(s.RadioQuality);
                s.Doubling = Mathf.MoveTowards(s.Doubling, holder && AnyoneDoubling(s.Id) ? 1f : 0f, dt * 6f);

                float outVol = mode == HearMode.OwnRadio ? ownRadioVol : mode == HearMode.NearRadio ? nearRadioVol : proxVol;
                float outCutoff = mode == HearMode.OwnRadio ? (WalkieStates.InHand(local) ? 22000f : PocketCutoff)
                    : mode == HearMode.NearRadio ? nearRadioCutoff : cutoff;
                float soundDist = mode == HearMode.NearRadio ? DistXz(listen, nearRadioPos) : dist;
                if (mode == HearMode.NearRadio)
                    s.Go.transform.position = nearRadioPos;
                else if (talkerHere)
                    s.Go.transform.position = talkerPos;

                if (s.Click != null)
                {
                    if (talkerHere)
                        s.Click.transform.position = talkerPos;
                    // The talker's key beep is a sound in the world like their voice: the same
                    // wall muffle, and the room's reverb when they stand inside.
                    s.Click.volume = Mathf.Lerp(1f, WallVolume, s.Occlusion);
                    if (s.ClickMuffle != null)
                        s.ClickMuffle.cutoffFrequency = Mathf.Lerp(22000f, WallCutoff, s.Occlusion);
                    if (s.ClickReverb != null && s.ClickReverb.enabled != talkerInside)
                        s.ClickReverb.enabled = talkerInside;
                }
                if (s.Reverb != null && Time.unscaledTime >= s.NextInsideCheck)
                {
                    s.NextInsideCheck = Time.unscaledTime + 0.5f;
                    bool inside = mode == HearMode.OwnRadio
                        ? Player.Instance != null && Player.Instance.isInside
                        : IsInside(net, mode == HearMode.NearRadio ? nearRadioId : s.Id);
                    if (s.Reverb.enabled != inside)
                        s.Reverb.enabled = inside;
                }

                if (s.Beh != null)
                    s.Beh.Volume = Mathf.Clamp(outVol * VoicePlayerVolumes.Get(s.Id), 0f, 2f);
                if (s.Muffle != null)
                {
                    s.SmoothCutoff = Mathf.Lerp(s.SmoothCutoff, outCutoff, Mathf.Clamp01(dt * 8f));
                    s.Muffle.cutoffFrequency = s.SmoothCutoff;
                }
                if (s.Src != null)
                {
                    // This player's own radio is in their hands: no pan. A voice or another
                    // player's radio pans toward where it is, gently when close.
                    float blend = mode == HearMode.OwnRadio || soundDist == float.MaxValue
                        ? 0f : Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(soundDist / FullPanDistance));
                    s.Blend = Mathf.MoveTowards(s.Blend, blend, dt * 3f);
                    s.Src.spatialBlend = s.Blend;
                }
            }

            foreach (int id in _reap)
            {
                if (_speakers.TryGetValue(id, out Speaker dead) && dead.Go != null)
                    UnityEngine.Object.Destroy(dead.Go);
                _speakers.Remove(id);
            }
        }

        /// <summary>A second talker keying over the one holding the channel is heard at this share, under the static.</summary>
        private const float DoublingUnderShare = 0.4f;

        private static bool AnyoneDoubling(int holderId)
        {
            foreach (Speaker o in _speakers.Values)
            {
                if (o.Id != holderId && o.WalkieActive && Time.unscaledTime - o.LastData < ChannelHoldSec)
                    return true;
            }
            // This player keying over them counts too: the far radios hear both.
            return _walkieTx;
        }

        /// <summary><see cref="IsInside"/>, refreshed at most twice a second per talker.</summary>
        private static bool IsInsideCached(Speaker s, LanNetworkManager net, int id)
        {
            if (Time.unscaledTime >= s.NextTalkerInsideCheck)
            {
                s.NextTalkerInsideCheck = Time.unscaledTime + 0.5f;
                s.TalkerInside = IsInside(net, id);
            }
            return s.TalkerInside;
        }

        private static float DistXz(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        /// <summary>
        /// The nearest other player within earshot whose walkie is on (PlayerState
        /// <c>WalkieState</c>), not the talker themself; 0 if none.
        /// </summary>
        internal static int NearestRadio(LanNetworkManager net, int talkerId, Vector3 from, out Vector3 pos)
        {
            pos = Vector3.zero;
            int best = 0;
            float bestDist = NearRadioRange;
            foreach (Players.RemotePlayerProxy p in net.EnumerateRemoteProxies())
            {
                if (p == null || !p.isActiveAndEnabled || p.PlayerId <= 0 || p.PlayerId == talkerId)
                    continue;
                if (!net.RemotePlayers.TryGetValue(p.PlayerId, out RemotePlayerState st) || st == null || !WalkieStates.Live(st.WalkieState))
                    continue;
                float d = DistXz(from, p.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p.PlayerId;
                    pos = p.transform.position;
                }
            }
            return best;
        }

        private static bool HasRecentRx()
        {
            foreach (Speaker s in _speakers.Values)
            {
                if (s.PacketsIn > 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A wall between listener and talker, by vanilla's own test for whether a creature hears a
        /// sound through something (<c>Character.heardSound</c>: one ray on its blocking layers).
        /// </summary>
        private static bool IsOccluded(Vector3 from, Vector3 to)
        {
            try
            {
                Vector3 delta = to - from;
                float mag = delta.magnitude;
                if (mag < 1f)
                    return false;
                return Physics.Raycast(from, delta / mag, mag, OcclusionMask, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                return false;
            }
        }
    }
}
