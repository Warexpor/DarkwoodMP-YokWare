using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Per-key log throttle that reports how many lines were dropped since the last one
    /// that went out, so a rate-limited warning still says how loud the underlying fault is.
    /// Callers build the message only when <see cref="ShouldLog"/> returns true.
    /// </summary>
    internal static class NetLogThrottle
    {
        private struct Slot
        {
            public float NextAt;
            public int Suppressed;
        }

        private const int KeyCap = 256;
        private static readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(32);

        /// <summary>
        /// True when the caller should emit now. <paramref name="suppressed"/> is the number of
        /// lines dropped under this key since the previous emit (0 on the first).
        /// </summary>
        public static bool ShouldLog(string key, float intervalSec, out int suppressed)
        {
            suppressed = 0;
            float now = Time.unscaledTime;
            if (_slots.TryGetValue(key, out Slot slot))
            {
                if (now < slot.NextAt)
                {
                    slot.Suppressed++;
                    _slots[key] = slot;
                    return false;
                }
                suppressed = slot.Suppressed;
            }
            else if (_slots.Count >= KeyCap)
            {
                _slots.Clear();
            }
            _slots[key] = new Slot { NextAt = now + intervalSec, Suppressed = 0 };
            return true;
        }

        /// <summary>Suffix for a throttled line, empty when nothing was dropped.</summary>
        public static string SuppressedSuffix(int suppressed)
            => suppressed > 0 ? " (+" + suppressed + " suppressed)" : string.Empty;

        public static void Reset() => _slots.Clear();
    }
}
