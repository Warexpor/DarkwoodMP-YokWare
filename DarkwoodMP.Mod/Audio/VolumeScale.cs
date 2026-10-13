using System;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// The voice volume sliders (voice, radio, microphone, each player): decibels, not a straight
    /// multiplier. The left end mutes, the middle is as recorded (0 dB), the right end is
    /// <see cref="MaxDb"/> (about x16); a step is 1 dB. The config keeps the multiplier.
    /// </summary>
    internal static class VolumeScale
    {
        internal const float MinDb = -24f;
        internal const float MaxDb = 24f;
        internal static readonly float MaxGain = (float)Math.Pow(10.0, MaxDb / 20.0);

        /// <summary>Where the slider's knob sits (0..1) for <paramref name="gain"/>.</summary>
        internal static float ToSlider(float gain)
        {
            if (!(gain > 0f))
                return 0f;
            double db = 20.0 * Math.Log10(gain);
            return (float)Math.Max(0.0, Math.Min(1.0, (db - MinDb) / (MaxDb - MinDb)));
        }

        /// <summary>The multiplier at slider position <paramref name="t"/>, in whole dB; the left end is 0 (muted).</summary>
        internal static float FromSlider(float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            double db = Math.Round(MinDb + t * (MaxDb - MinDb));
            return db <= MinDb ? 0f : (float)Math.Pow(10.0, db / 20.0);
        }

        internal static float Clamp(float gain) => gain > 0f ? Math.Min(gain, MaxGain) : 0f;
    }
}
