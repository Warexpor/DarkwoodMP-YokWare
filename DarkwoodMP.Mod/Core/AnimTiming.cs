using System;

namespace DWMPHorde
{
    /// <summary>
    /// Unity-free timing for animations on the shared animation clock (<c>Sync.AnimClock</c>).
    /// Every function is a pure function of a seed and a clock time, so every machine, a late
    /// joiner and the host after a reload compute the same frame at the same moment, with no
    /// history to carry.
    /// </summary>
    public static class AnimTiming
    {
        /// <summary><paramref name="x"/> modulo <paramref name="period"/>, in [0, period).</summary>
        public static double Mod(double x, double period)
        {
            if (period <= 0)
                return 0;
            double m = x % period;
            return m < 0 ? m + period : m;
        }

        /// <summary>The shortest signed way from one phase to another on a cycle: (-period/2, period/2].</summary>
        public static double WrapError(double error, double period)
        {
            if (period <= 0)
                return 0;
            double e = Mod(error, period);
            return e > period * 0.5 ? e - period : e;
        }

        /// <summary>A value in [-1, 1] for step <paramref name="k"/> of a seeded sequence.</summary>
        public static double Jitter(int seed, long k)
        {
            uint h = CosmeticKey.Add(CosmeticKey.Add(CosmeticKey.Start(), seed), (int)(k & 0xFFFFFFFF));
            h = CosmeticKey.Add(h, (int)(k >> 32));
            uint bits = (uint)CosmeticKey.Finish(h) & 0xFFFFFF;
            return bits / (double)0xFFFFFF * 2.0 - 1.0;
        }

        /// <summary>A whole number in [min, max) for step <paramref name="k"/> of a seeded sequence.</summary>
        public static int Pick(int seed, long k, int min, int max)
        {
            if (max <= min)
                return min;
            double u = (Jitter(seed ^ 0x5bd1e995, k) + 1.0) * 0.5;
            int v = min + (int)Math.Floor(u * (max - min));
            return v >= max ? max - 1 : v;
        }

        // ---- replays after a random delay (AnimationPlay.playDelayMin/Max) ----------------------

        /// <summary>
        /// Time of replay <paramref name="k"/>: <c>k * mean + amp * jitter</c> with
        /// <c>amp = (max - min) / 4</c>, so the gap between two replays always lies in
        /// [min, max] like vanilla's <c>Random.Range(min, max)</c> wait.
        /// </summary>
        public static double ReplayTime(int seed, double min, double max, long k)
        {
            double mean = (min + max) * 0.5;
            double amp = (max - min) * 0.25;
            return k * mean + amp * Jitter(seed, k);
        }

        /// <summary>The first replay after <paramref name="now"/>.</summary>
        public static double NextReplay(int seed, double min, double max, double now, out long k)
        {
            double mean = (min + max) * 0.5;
            if (mean <= 0)
            {
                k = 0;
                return double.PositiveInfinity;
            }
            k = (long)Math.Floor(now / mean) - 1;
            double t = ReplayTime(seed, min, max, k);
            while (t <= now)
            {
                k++;
                t = ReplayTime(seed, min, max, k);
            }
            return t;
        }

        // ---- twitching (AnimationPlay.twitching) --------------------------------------------------

        /// <summary>Mean rest between two twitches (vanilla: <c>Random.Range(1f, 5f)</c>).</summary>
        public const double TwitchMeanRest = 3.0;
        /// <summary>How far a twitch start moves from its slot; keeps every rest at least 1 s.</summary>
        public const double TwitchJitter = 1.0;

        /// <summary>
        /// The frame a twitching animation shows at <paramref name="t"/>. Vanilla: play forward to a
        /// random frame in [1, frames), play back to the start, rest 1-5 s, again. Here twitch
        /// <c>k</c> starts at <c>k * C + jitter</c> with <c>C</c> the longest twitch plus the mean
        /// rest, and its turning frame is seeded by <c>k</c>.
        /// </summary>
        public static int TwitchFrame(int seed, int frames, double fps, double t)
        {
            if (frames < 2 || fps <= 0)
                return 0;
            double longest = 2.0 * (frames - 1) / fps;
            double cycle = longest + TwitchMeanRest;
            long k = (long)Math.Floor(t / cycle);
            double start = TwitchStart(seed, cycle, k);
            if (start > t)
            {
                k--;
                start = TwitchStart(seed, cycle, k);
            }
            else
            {
                double nextStart = TwitchStart(seed, cycle, k + 1);
                if (nextStart <= t)
                {
                    k++;
                    start = nextStart;
                }
            }
            int turn = Pick(seed, k, 1, frames);
            double u = t - start;
            double half = turn / fps;
            if (u < half)
                return Math.Min(turn, (int)Math.Floor(u * fps));
            if (u < 2 * half)
                return Math.Max(0, turn - (int)Math.Floor((u - half) * fps));
            return 0;
        }

        private static double TwitchStart(int seed, double cycle, long k)
            => k * cycle + TwitchJitter * Jitter(seed, k);
    }
}
