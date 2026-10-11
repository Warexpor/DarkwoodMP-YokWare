using UnityEngine;

namespace DWMPHorde.Audio
{
    /// <summary>
    /// The room's reverb on a voice or a radio heard from inside a building. Vanilla switches a
    /// stock <see cref="AudioReverbFilter"/> on and off for its own short sounds; on a voice that
    /// runs for minutes the stock filter is as loud as the voice itself, and the switch cut the
    /// reverb's whole tail (or added it) inside one audio buffer: a loud snap when a player or a
    /// radio crossed a doorway. Here the filter is a quieter, shorter room, and its level is faded
    /// in and out instead of switched.
    /// </summary>
    internal static class RoomReverb
    {
        /// <summary>The room's level under the voice, in millibels (the stock filter is 0).</summary>
        private const float RoomMb = -900f;
        private const float DecaySec = 0.8f;
        private const float FadeSec = 0.4f;
        /// <summary>Below this share of the room's level the filter is off.</summary>
        private const float Floor = 0.003f;

        internal static void Setup(AudioReverbFilter f)
        {
            if (f == null)
                return;
            f.reverbPreset = AudioReverbPreset.User;
            f.dryLevel = 0f;
            f.room = -10000f;
            f.roomHF = -600f;
            f.roomLF = 0f;
            f.decayTime = DecaySec;
            f.decayHFRatio = 0.5f;
            f.reflectionsLevel = -10000f;
            f.reflectionsDelay = 0f;
            f.reverbLevel = 0f;
            f.reverbDelay = 0.04f;
            f.hfReference = 5000f;
            f.lfReference = 250f;
            f.diffusion = 100f;
            f.density = 100f;
            f.enabled = false;
        }

        /// <summary>Every frame: the room comes in or goes out over <see cref="FadeSec"/>.</summary>
        internal static void Drive(AudioReverbFilter f, bool inside, float dt)
        {
            if (f == null)
                return;
            float share = f.enabled ? Mathf.Pow(10f, (f.room - RoomMb) / 2000f) : 0f;
            share = Mathf.MoveTowards(share, inside ? 1f : 0f, dt / FadeSec);
            Apply(f, share);
        }

        /// <summary>Before a one-shot (a key beep, a knob click): the room is there or not, at once.</summary>
        internal static void Set(AudioReverbFilter f, bool inside)
        {
            if (f != null)
                Apply(f, inside ? 1f : 0f);
        }

        private static void Apply(AudioReverbFilter f, float share)
        {
            if (share <= Floor)
            {
                if (f.enabled)
                    f.enabled = false;
                return;
            }
            f.room = Mathf.Max(-10000f, RoomMb + 2000f * Mathf.Log10(share));
            if (!f.enabled)
                f.enabled = true;
        }
    }
}
