using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// What a keyed radio picks up besides its holder's voice: the sounds of the world around
    /// them, as they reach the holder.
    /// <para>
    /// Every sound source in the game gets a pass-through tap in its own effect chain. A source's
    /// signal reaches its chain after Unity has applied the source's volume, its falloff with
    /// the distance to the listener (this player) and whatever filters stand before the tap
    /// (vanilla's wall muffle, the mod's own), so what a tap sees is that one sound as loud and
    /// as dull as it is where the holder stands. While the radio is keyed the taps add what they
    /// see into one track; <see cref="VoiceChatService"/> sends it beside the voice.
    /// </para>
    /// Left out: music, menu sounds and scene transitions (by their mixer group), and voice
    /// chat itself (other players' voices and radios, this radio's own beeps), so a radio never
    /// sends a transmission on again.
    /// </summary>
    internal static class WorldSoundPickup
    {
        /// <summary>The track's level against the sources as they play (they are taken before the game's mixer).</summary>
        private const float Gain = 1.2f;
        private const int RingLen = 1 << 16;
        private const int RingMask = RingLen - 1;
        /// <summary>The track is read this far behind what the taps wrote last, at least: a block still being mixed is not read.</summary>
        private const float GuardSec = 0.03f;
        /// <summary>Fallen further behind than this, the reader skips ahead.</summary>
        private const float MaxLagSec = 0.2f;
        private const float SweepEverySec = 3f;

        private static readonly float[] _ring = new float[RingLen]; // process-scoped: the track (audio thread writes, main thread reads, under _lock)
        private static readonly object _lock = new object();
        private static long _clearedTo; // process-scoped: with _ring
        private static long _end; // process-scoped: with _ring
        private static long _read; // process-scoped: with _ring
        private static int _blockFrames = 1024; // process-scoped: with _ring
        private static volatile bool _active; // reset-in: Reset
        private static volatile int _rate = 48000; // process-scoped: AudioSettings.outputSampleRate, read on the main thread
        private static float _nextSweep; // reset-in: Reset
        private static float[] _block = new float[0]; // process-scoped: scratch
        private static VoiceChatService.Biquad _lp1, _lp2; // process-scoped: anti-alias before going down to the voice rate
        private static int _lpRate; // process-scoped: the rate _lp1/_lp2 were made for
        private static readonly HashSet<int> _seen = new HashSet<int>(); // process-scoped: source GameObjects already looked at (ids are never reused in a run)

        /// <summary>Taps are only handed out in a session with the setting on (they cost a little on every sound).</summary>
        private static bool Wanted
        {
            get
            {
                var net = ModRuntime.Network;
                return net != null && net.IsConnected
                    && Config.ModConfig.VoiceEnabled != null && Config.ModConfig.VoiceEnabled.Value
                    && Config.ModConfig.VoiceRadioWorldSounds != null && Config.ModConfig.VoiceRadioWorldSounds.Value;
            }
        }

        public static void Reset()
        {
            _active = false;
            _nextSweep = 0f;
        }

        /// <summary>Every frame: the radio is keyed (or just was) and picks up sound.</summary>
        internal static void SetActive(bool on)
        {
            if (AudioSettings.outputSampleRate > 0)
                _rate = AudioSettings.outputSampleRate;
            if (on && !_active)
            {
                lock (_lock)
                {
                    _read = _end;
                }
                _nextSweep = 0f;
            }
            _active = on;
            // Sources that do not come from the game's sound pool (ambient emitters placed in
            // the scenes, the mod's own) are found by looking, now and then while keyed.
            if (on && Time.unscaledTime >= _nextSweep)
            {
                _nextSweep = Time.unscaledTime + SweepEverySec;
                try
                {
                    AudioSource[] all = UnityEngine.Object.FindObjectsOfType<AudioSource>();
                    for (int i = 0; i < all.Length; i++)
                        Consider(all[i]);
                }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Audio, "[Voice] sound pickup sweep: " + ex.Message);
                }
            }
        }

        /// <summary>A source is about to play, or was found: give it a tap unless it is one of the kinds left out.</summary>
        internal static void Consider(AudioSource src)
        {
            if (src == null || !Wanted)
                return;
            GameObject go = src.gameObject;
            if (!_seen.Add(go.GetInstanceID()))
                return;
            if (LeftOut(src) || go.GetComponent<Tap>() != null)
                return;
            go.AddComponent<Tap>();
            _taps++;
        }

        private static int _taps; // process-scoped: stats
        private static int _sent; // process-scoped: stats
        private static float _peak; // process-scoped: stats

        /// <summary>For the 10 s voice line: taps handed out so far, blocks sent and their peak since the last call.</summary>
        internal static string TakeStats()
        {
            if (_sent == 0)
                return "";
            string line = " | pickup taps=" + _taps + " sent=" + _sent + " peak=" + _peak.ToString("0.00");
            _sent = 0;
            _peak = 0f;
            return line;
        }

        private static bool LeftOut(AudioSource src)
        {
            Transform root = src.transform.root;
            if (root != null && root.name == "YokWare_Voice")
                return true;
            UnityEngine.Audio.AudioMixerGroup group = src.outputAudioMixerGroup;
            if (group == null)
                return false;
            string n = group.name ?? "";
            return n.StartsWith("music", StringComparison.OrdinalIgnoreCase)
                || n.Equals("ui", StringComparison.OrdinalIgnoreCase)
                || n.Equals("TRANSITIONS", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>In a source's effect chain: sees its signal, changes nothing.</summary>
        private sealed class Tap : MonoBehaviour
        {
            private void OnAudioFilterRead(float[] data, int channels)
            {
                if (_active)
                    Add(data, channels);
            }
        }

        /// <summary>Audio thread: one source's block, added into the track at the block's place in time.</summary>
        private static void Add(float[] data, int channels)
        {
            if (channels <= 0)
                return;
            int frames = data.Length / channels;
            long pos = (long)Math.Round(AudioSettings.dspTime * _rate);
            lock (_lock)
            {
                _blockFrames = frames;
                long to = pos + frames;
                // Far from where the track was last written (a pause, the first block): start over there.
                if (pos < _clearedTo - RingLen / 2 || pos > _clearedTo + RingLen / 2)
                {
                    Array.Clear(_ring, 0, RingLen);
                    _clearedTo = pos;
                    _read = pos;
                }
                for (long p = _clearedTo; p < to; p++)
                    _ring[p & RingMask] = 0f;
                if (to > _clearedTo)
                    _clearedTo = to;
                if (to > _end)
                    _end = to;
                float inv = 1f / channels;
                for (int i = 0; i < frames; i++)
                {
                    float m = 0f;
                    int at = i * channels;
                    for (int c = 0; c < channels; c++)
                        m += data[at + c];
                    _ring[(pos + i) & RingMask] += m * inv;
                }
            }
        }

        /// <summary>
        /// Main thread: the next <paramref name="count"/> samples of the track at the voice rate
        /// into <paramref name="dst"/>. False when there is not enough yet (the radio was keyed
        /// this instant) or nothing but silence was picked up.
        /// </summary>
        internal static bool Read(float[] dst, int count)
        {
            int rate = _rate;
            int need = (int)((long)count * rate / VoiceCodec.SampleRate);
            if (need <= 0 || need >= RingLen / 2)
                return false;
            if (_block.Length < need)
                _block = new float[need];
            lock (_lock)
            {
                long guard = Math.Max(_blockFrames, (long)(rate * GuardSec));
                long ready = _end - guard - _read;
                if (ready > (long)(rate * MaxLagSec) + need)
                {
                    _read = _end - guard - need - _blockFrames;
                    ready = _end - guard - _read;
                }
                if (ready < need)
                    return false;
                for (int i = 0; i < need; i++)
                    _block[i] = _ring[(_read + i) & RingMask];
                _read += need;
            }

            if (_lpRate != rate)
            {
                _lpRate = rate;
                _lp1 = VoiceChatService.Biquad.LowPass(6500f, rate);
                _lp2 = VoiceChatService.Biquad.LowPass(6500f, rate);
            }
            bool down = rate > VoiceCodec.SampleRate;
            double energy = 0;
            for (int i = 0; i < need; i++)
            {
                float v = _block[i];
                if (down)
                    v = _lp2.Run(_lp1.Run(v));
                _block[i] = v;
                energy += v * (double)v;
            }
            if (energy / need < 1e-9)
                return false;

            double step = (double)rate / VoiceCodec.SampleRate;
            for (int j = 0; j < count; j++)
            {
                double at = j * step;
                int i0 = (int)at;
                int i1 = i0 + 1 < need ? i0 + 1 : i0;
                float v = (_block[i0] + (_block[i1] - _block[i0]) * (float)(at - i0)) * Gain;
                // Several loud sources at once bend over instead of clipping.
                dst[j] = v > 0.8f || v < -0.8f ? Mathf.Sign(v) * (0.8f + 0.2f * (float)Math.Tanh((Math.Abs(v) - 0.8f) * 5f)) : v;
                float a = dst[j] < 0f ? -dst[j] : dst[j];
                if (a > _peak)
                    _peak = a;
            }
            _sent++;
            return true;
        }

        /// <summary>The game's pooled sounds, as each starts to play.</summary>
        [HarmonyPatch(typeof(AudioObject), "_OnPlay")]
        internal static class PooledSoundPlayPatch
        {
            private static void Postfix(AudioObject __instance)
            {
                try
                {
                    if (__instance != null)
                        Consider(__instance.primaryAudioSource);
                }
                catch { /* a sound without a tap is only not picked up */ }
            }
        }
    }
}
