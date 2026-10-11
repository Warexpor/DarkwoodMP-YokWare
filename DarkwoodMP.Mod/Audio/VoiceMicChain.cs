using System;
using System.Collections.Generic;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// What the microphone's sound goes through between the recording and the codec, as one
    /// stream: brought to 48 kHz (the rate the noise suppression was trained at; most
    /// devices record at it already), cleaned by <see cref="RnNoise"/> when the player has
    /// noise suppression on, brought down to the voice rate through a proper low-pass,
    /// set to the mic volume and cut into packets. No Unity in it: it runs on the mic's
    /// worker thread.
    /// </summary>
    internal sealed class VoiceMicChain
    {
        /// <summary>The rate the recording is asked for.</summary>
        internal const int ModelRate = RnNoise.SampleRate;
        private const int FrameSamples = VoiceCodec.PacketSamples;
        private const float ToModelScale = 32768f;

        /// <summary>
        /// The longest the chain holds a sound back before it is in a packet's queue: the
        /// suppression's 10 ms frame and its 20 ms of overlap and look-ahead
        /// (<see cref="RnNoise.DelaySamples"/>), and the low-passes of the rate changes.
        /// </summary>
        internal const float MaxDelaySec = 0.04f;

        private readonly VoiceResampler _toModel;
        private readonly VoiceResampler _toVoice = new VoiceResampler(ModelRate, VoiceCodec.SampleRate);
        private readonly List<float> _in48 = new List<float>(4096);
        private readonly List<float> _out = new List<float>(4096);
        private readonly float[] _frame48 = new float[RnNoise.FrameSize];
        private RnNoise _rn;
        private bool _rnBroken;

        /// <summary>How hard the suppression cuts (<see cref="RnNoise.Strength"/>); 1 is the model as trained.</summary>
        internal float Strength = 1f;

        /// <summary>Why the noise suppression could not start (null while it can).</summary>
        internal string SuppressionError { get; private set; }

        internal VoiceMicChain(int inRate)
        {
            if (inRate != ModelRate)
                _toModel = new VoiceResampler(inRate, ModelRate);
        }

        /// <summary>
        /// Take <paramref name="n"/> recorded samples (-1..1) and add the packets they complete
        /// to <paramref name="frames"/>.
        /// </summary>
        internal void Process(float[] input, int n, float gain, bool suppress, List<float[]> frames)
        {
            if (_toModel != null)
                _toModel.Process(input, n, _in48, 1f);
            else
            {
                for (int i = 0; i < n; i++)
                    _in48.Add(input[i]);
            }

            int used = 0;
            while (_in48.Count - used >= RnNoise.FrameSize)
            {
                _in48.CopyTo(used, _frame48, 0, RnNoise.FrameSize);
                used += RnNoise.FrameSize;
                // Before the mic volume: the model hears the mic as it is, at the level it was trained for.
                if (suppress && !_rnBroken)
                    Suppress(_frame48);
                else
                    _rn = null;
                _toVoice.Process(_frame48, RnNoise.FrameSize, _out, gain);
            }
            if (used > 0)
                _in48.RemoveRange(0, used);

            used = 0;
            while (_out.Count - used >= FrameSamples)
            {
                var frame = new float[FrameSamples];
                for (int i = 0; i < FrameSamples; i++)
                {
                    float v = _out[used + i];
                    // A soft ceiling instead of hard clipping when the mic volume is turned up.
                    frame[i] = v > 0.9f || v < -0.9f ? Math.Sign(v) * (0.9f + 0.1f * (float)Math.Tanh((Math.Abs(v) - 0.9f) * 10f)) : v;
                }
                used += FrameSamples;
                frames.Add(frame);
            }
            if (used > 0)
                _out.RemoveRange(0, used);
        }

        private void Suppress(float[] frame)
        {
            if (_rn == null)
            {
                try
                {
                    _rn = new RnNoise();
                }
                catch (Exception ex)
                {
                    // The weights did not load: the mic goes on without suppression, and says so once.
                    _rnBroken = true;
                    SuppressionError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    return;
                }
            }
            for (int i = 0; i < frame.Length; i++)
                frame[i] *= ToModelScale;
            _rn.Strength = Math.Min(Math.Max(Strength, RnNoise.MinStrength), RnNoise.MaxStrength);
            _rn.Process(frame, 0);
            for (int i = 0; i < frame.Length; i++)
                frame[i] *= 1f / ToModelScale;
        }
    }
}
