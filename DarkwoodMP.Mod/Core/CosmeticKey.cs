using System;

namespace DWMPHorde
{
    /// <summary>
    /// Unity-free hashing for cosmetic roll seeds (<c>Sync.CosmeticRolls</c>). A seed is built from
    /// what every machine has bit for bit: object names and authored local offsets, plus a location
    /// root's placement on its coarse grid. Floats are cut to whole cells before hashing, so the
    /// same placement reached by different transform paths (live spawn, world generation, a save
    /// load) lands in the same cell.
    /// </summary>
    internal static class CosmeticKey
    {
        /// <summary>Cell for an object's own offsets (world units; objects are tens of units big).</summary>
        internal const float LocalCell = 1f;

        /// <summary>
        /// Cell for a location root's world position. Roots sit on the world grid (multiples of
        /// 300) or on pad slots (multiples of 5000), so a 50-unit cell keeps every root 25 units
        /// away from a cell edge.
        /// </summary>
        internal const float RootCell = 50f;

        private const uint FnvOffset = 2166136261u;
        private const uint FnvPrime = 16777619u;

        internal static uint Start() => FnvOffset;

        internal static uint Add(uint h, int v)
        {
            unchecked
            {
                h = (h ^ (uint)(v & 0xFF)) * FnvPrime;
                h = (h ^ (uint)((v >> 8) & 0xFF)) * FnvPrime;
                h = (h ^ (uint)((v >> 16) & 0xFF)) * FnvPrime;
                h = (h ^ (uint)((v >> 24) & 0xFF)) * FnvPrime;
                return h;
            }
        }

        /// <summary>Ordinal, culture-free (string.GetHashCode differs between runtimes).</summary>
        internal static uint Add(uint h, string s)
        {
            if (s == null)
                return Add(h, -1);
            unchecked
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    h = (h ^ (uint)(c & 0xFF)) * FnvPrime;
                    h = (h ^ (uint)(c >> 8)) * FnvPrime;
                }
            }
            return Add(h, s.Length);
        }

        /// <summary>The cell a coordinate falls in (round half up).</summary>
        internal static int Cell(float v, float cell)
        {
            if (float.IsNaN(v) || float.IsInfinity(v))
                return int.MinValue;
            double c = Math.Floor((double)v / cell + 0.5);
            if (c > int.MaxValue) return int.MaxValue;
            if (c < int.MinValue + 1) return int.MinValue + 1;
            return (int)c;
        }

        /// <summary>Final avalanche (murmur3 fmix32) so near keys give unrelated seeds.</summary>
        internal static int Finish(uint h)
        {
            unchecked
            {
                h ^= h >> 16;
                h *= 0x85EBCA6Bu;
                h ^= h >> 13;
                h *= 0xC2B2AE35u;
                h ^= h >> 16;
                return (int)h;
            }
        }

        /// <summary>A roll's seed under an anchor: the anchor's key and the roll's path below it.</summary>
        internal static int Mix(int anchorKey, uint chain)
            => Finish(Add(Add(Start(), anchorKey), (int)chain));

        /// <summary>A separate stream for another component on the same object.</summary>
        internal static int Salt(int key, string salt) => Finish(Add(Add(Start(), key), salt));

        /// <summary>
        /// Location roots are renamed "<c>name_done</c>" once placed (vanilla
        /// <c>LocationMarker.spawnLocation</c>); the key uses the authored name.
        /// </summary>
        internal static string RootName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "";
            const string done = "_done";
            while (name.EndsWith(done, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - done.Length);
            return name;
        }
    }
}
