using System;
using System.Collections.Generic;
using DWMPHorde.Config;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// Other players' radios as things in the world around this player: this player's own
    /// transmission coming out of a radio near them, and the knob of a radio someone near them
    /// switches on or off.
    /// </summary>
    public static partial class VoiceChatService
    {
        // ------------------------------------------------------------------
        // This player's own transmission, out of a nearby player's radio
        // ------------------------------------------------------------------

        private const int SelfEchoId = int.MinValue;
        private const float SelfPrimeSec = 0.07f;

        private static Speaker _selfEcho; // reset-in: ResetSelfEcho
        private static float[] _selfBuf; // process-scoped: scratch (the mic frame itself is kept as pre-roll)
        private static int _selfNearId; // reset-in: ResetSelfEcho

        private static void ResetSelfEcho()
        {
            if (_selfEcho != null && _selfEcho.Go != null)
                UnityEngine.Object.Destroy(_selfEcho.Go);
            _selfEcho = null;
            _selfNearId = 0;
        }

        /// <summary>
        /// This player keys their walkie with another player's live radio in earshot: that radio
        /// plays them, as it plays anyone on the channel. Nothing travels over the network for
        /// this; the frame just sent is put through the same radio a received one goes through.
        /// </summary>
        private static void FeedSelfEcho(float[] frame, float level)
        {
            Speaker s = _selfEcho;
            if (s == null || s.Go == null || _selfNearId == 0 || !s.RadioMode)
                return;
            if (_selfBuf == null || _selfBuf.Length < frame.Length)
                _selfBuf = new float[frame.Length];
            Array.Copy(frame, _selfBuf, frame.Length);
            s.Level = level;
            s.LastData = Time.unscaledTime;
            WriteVoice(s, _selfBuf, frame.Length);
        }

        /// <summary>
        /// Every frame: which radio near this player plays their transmission, how loud, how
        /// muffled, and its squelch tail and roger beep when they let go of the key.
        /// </summary>
        private static void UpdateSelfEcho(LanNetworkManager net, float dt)
        {
            Speaker s = _selfEcho;
            Player p = Player.Instance;
            bool wanted = _walkieTx && net != null && p != null;
            if (s == null || s.Go == null)
            {
                if (!wanted)
                    return;
                s = _selfEcho = CreateSpeaker(SelfEchoId);
                // No network in between: only the mic's own frame pacing to cover. A long
                // buffer here would play this player's voice back to them noticeably late.
                s.PrimeSec = SelfPrimeSec;
            }

            float since = Time.unscaledTime - s.LastData;
            lock (s.Lock)
            {
                if (s.Priming && s.Buffered > 0 && since > PrimeFlushSec)
                    s.PrimeRelease = true;
            }
            if (s.RadioWasActive && since > 0.3f)
            {
                s.RadioWasActive = false;
                WriteSquelch(s, open: false);
            }
            if (!wanted && since > IdleReapSec && s.Buffered == 0)
            {
                ResetSelfEcho();
                return;
            }

            float vol = ModConfig.VoiceVolume?.Value ?? 1f;
            Vector3 listen = LocalAudioService.GetListenPosition();
            float outVol = 0f;
            float outCutoff = 22000f;
            Vector3 radioPos = Vector3.zero;
            int nearId = net != null && p != null && (wanted || since < 1f)
                ? NearestRadio(net, net.LocalPlayerId, listen, out radioPos) : 0;
            if (nearId != 0)
            {
                byte carrier = net.RemotePlayers.TryGetValue(nearId, out RemotePlayerState cst) && cst != null ? cst.WalkieState : (byte)0;
                float quality = RadioQuality(p.transform.position, radioPos, p.isInside, IsInside(net, nearId),
                    LocalUnderground, WalkieStates.IsUnderground(carrier));
                if (quality > RadioSquelchQuality)
                {
                    bool hand = WalkieStates.InHand(carrier);
                    float d = DistXz(listen, radioPos);
                    float range = hand ? NearRadioRange : NearRadioRange * 0.7f;
                    outVol = Mathf.InverseLerp(range, NearRadioFull, d) * vol * NearRadioVolume * (hand ? 1f : PocketVolume);
                    if (nearId != s.NearRadioId || Time.unscaledTime >= s.NextRadioOcclusionCheck)
                    {
                        s.NextRadioOcclusionCheck = Time.unscaledTime + 0.15f;
                        s.RadioOccludedNow = IsOccluded(listen, radioPos);
                    }
                    s.RadioOcclusion = Mathf.MoveTowards(s.RadioOcclusion, s.RadioOccludedNow ? 1f : 0f, dt * 5f);
                    outVol *= Mathf.Lerp(1f, WallVolume, s.RadioOcclusion);
                    outCutoff = Mathf.Lerp(hand ? 22000f : PocketCutoff, WallCutoff, s.RadioOcclusion);
                    s.RadioQuality = quality;
                    s.RadioHiss = Mathf.Lerp(RadioHissNear, RadioHissFar, 1f - quality);
                    s.FarStatic = FarStaticGain(quality);
                    s.Go.transform.position = radioPos;

                    if (s.Reverb != null && Time.unscaledTime >= s.NextInsideCheck)
                    {
                        s.NextInsideCheck = Time.unscaledTime + 0.5f;
                        bool inside = IsInside(net, nearId);
                        if (s.Reverb.enabled != inside)
                            s.Reverb.enabled = inside;
                    }
                }
                else
                {
                    nearId = 0;
                }
            }
            s.NearRadioId = nearId;
            _selfNearId = nearId;
            s.Mode = HearMode.NearRadio;
            // Radio processing stays on through the tail, so the squelch closes what it opened.
            s.RadioMode = nearId != 0 || s.RadioWasActive;
            s.LevelEnv = since > 0.4f ? 0f : s.Level;

            if (s.Beh != null)
                s.Beh.Volume = Mathf.Clamp(outVol, 0f, 2f);
            if (s.Muffle != null)
            {
                s.SmoothCutoff = Mathf.Lerp(s.SmoothCutoff, outCutoff, Mathf.Clamp01(dt * 8f));
                s.Muffle.cutoffFrequency = s.SmoothCutoff;
            }
            if (s.Src != null && nearId != 0)
            {
                float blend = Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(DistXz(listen, radioPos) / FullPanDistance));
                s.Blend = Mathf.MoveTowards(s.Blend, blend, dt * 3f);
                s.Src.spatialBlend = s.Blend;
            }
        }

        // ------------------------------------------------------------------
        // The knob of a radio another player switches on or off
        // ------------------------------------------------------------------

        /// <summary>A knob is a small sound: heard this share of the way a radio's speaker is.</summary>
        private const float KnobRangeShare = 0.6f;

        private static readonly Dictionary<int, byte> _knobStates = new Dictionary<int, byte>(); // reset-in: Reset
        private static AudioSource _knobSrc; // process-scoped: 3D source for other players' knobs
        private static AudioLowPassFilter _knobMuffle; // process-scoped: with _knobSrc
        private static AudioReverbFilter _knobReverb; // process-scoped: with _knobSrc

        /// <summary>
        /// A player near this one turns their radio's knob (it turns only in hand, so their
        /// PlayerState goes between "off" and "on in hand"): the click is heard a short way,
        /// muffled by a wall and with the room's reverb like any sound they make.
        /// </summary>
        private static void TickRemoteKnobs(LanNetworkManager net)
        {
            if (net == null || Player.Instance == null)
                return;
            foreach (Players.RemotePlayerProxy proxy in net.EnumerateRemoteProxies())
            {
                if (proxy == null || proxy.PlayerId <= 0)
                    continue;
                if (!net.RemotePlayers.TryGetValue(proxy.PlayerId, out RemotePlayerState st) || st == null)
                    continue;
                byte now = WalkieStates.Power(st.WalkieState);
                bool known = _knobStates.TryGetValue(proxy.PlayerId, out byte was);
                _knobStates[proxy.PlayerId] = now;
                if (!known || was == now || !proxy.isActiveAndEnabled)
                    continue;
                bool turned = (was == WalkieStates.Off && now == WalkieStates.Hand)
                    || (was == WalkieStates.Hand && now == WalkieStates.Off);
                if (turned)
                    PlayRemoteKnob(net, proxy);
            }
        }

        private static void PlayRemoteKnob(LanNetworkManager net, Players.RemotePlayerProxy proxy)
        {
            try
            {
                Vector3 at = proxy.transform.position;
                Vector3 listen = LocalAudioService.GetListenPosition();
                if (DistXz(listen, at) >= NearRadioRange * KnobRangeShare)
                    return;
                AudioClip clip = KnobTake();
                if (clip == null)
                    return;
                if (_knobSrc == null)
                {
                    if (_root == null)
                    {
                        _root = new GameObject("YokWare_Voice");
                        UnityEngine.Object.DontDestroyOnLoad(_root);
                    }
                    var go = new GameObject("YokWare_RadioKnob");
                    go.transform.SetParent(_root.transform, false);
                    _knobSrc = go.AddComponent<AudioSource>();
                    _knobSrc.playOnAwake = false;
                    _knobSrc.spatialBlend = 1f;
                    _knobSrc.rolloffMode = AudioRolloffMode.Linear;
                    _knobSrc.minDistance = NearRadioFull * KnobRangeShare;
                    _knobSrc.maxDistance = NearRadioRange * KnobRangeShare;
                    _knobSrc.dopplerLevel = 0f;
                    _knobMuffle = go.AddComponent<AudioLowPassFilter>();
                    _knobReverb = go.AddComponent<AudioReverbFilter>();
                }
                bool walled = IsOccluded(listen, at);
                _knobSrc.transform.position = at;
                _knobMuffle.cutoffFrequency = walled ? WallCutoff : 22000f;
                _knobReverb.enabled = IsInside(net, proxy.PlayerId);
                float vol = Mathf.Clamp01((ModConfig.VoiceVolume?.Value ?? 1f) * 0.3f * KnobLevel * (walled ? WallVolume : 1f));
                _knobSrc.PlayOneShot(clip, vol);
            }
            catch { /* audio not ready */ }
        }
    }
}
