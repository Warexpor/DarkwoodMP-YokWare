using System;
using System.Collections.Generic;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Unity-free core of <see cref="OutsidePadSlots"/>: the location name → (slot, yaw) map, the
    /// session-wide high-water mark, the in-flight spawn set, and slot allocation. Everything that
    /// needs the game (reading <c>locationPositions</c>, asking whether a pad is alive, the spawn
    /// coroutine, the wire) stays in <see cref="OutsidePadSlots"/> and reaches this class through
    /// plain ints, floats and delegates, so the rules can be tested without the game assemblies.
    /// </summary>
    internal sealed class OutsidePadSlotLogic
    {
        internal sealed class Entry
        {
            public int Slot;
            public int Yaw;
            /// <summary>This machine already spawned the marker with this assignment.</summary>
            public bool Consumed;
        }

        /// <summary>How a pad that already exists in the world related to what the map knew.</summary>
        internal enum SeedResult
        {
            /// <summary>The map had no assignment for this name; one was learned from the pad.</summary>
            Learned,
            /// <summary>Same slot as the map; the assignment is now consumed.</summary>
            Matched,
            /// <summary>Authority only: the pad's real slot replaced a different assigned slot.</summary>
            Rebased,
            /// <summary>Non-authority: the pad sits in another slot than the host assigned.</summary>
            Mismatch
        }

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Names whose spawn wrapper is running (assignment consumed, pad not yet registered).</summary>
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Lowest slot that has never been handed out (vanilla's monotonic counter, made session-wide).</summary>
        public int HighWater { get; private set; }

        public IEnumerable<KeyValuePair<string, Entry>> Entries => _entries;

        /// <summary>New world / chapter: every previous assignment is meaningless.</summary>
        public void Reset()
        {
            _entries.Clear();
            _inFlight.Clear();
            HighWater = 0;
        }

        public bool TryGet(string name, out Entry entry)
        {
            if (name == null)
            {
                entry = null;
                return false;
            }
            return _entries.TryGetValue(name, out entry);
        }

        /// <summary>Raise (never lower) the high-water mark.</summary>
        public void RaiseHighWater(int value)
        {
            if (value > HighWater)
                HighWater = value;
        }

        public bool IsInFlight(string name) => name != null && _inFlight.Contains(name);
        public void BeginSpawn(string name) => _inFlight.Add(name);
        public void EndSpawn(string name) => _inFlight.Remove(name);

        /// <summary>Yaw as a whole-degree value in [0, 360). Rounds half to even like Unity's RoundToInt.</summary>
        public static int NormalizeYaw(float yawDegrees)
        {
            int v = (int)Math.Round(yawDegrees) % 360;
            return v < 0 ? v + 360 : v;
        }

        /// <summary>
        /// Index of the slot whose (x, z) is within <paramref name="epsilon"/> of the pad, or -1
        /// when it sits on none (worldgen location, tutorial scene).
        /// </summary>
        public static int DeriveSlot(int slotCount, Func<int, float> slotX, Func<int, float> slotZ,
            float x, float z, float epsilon)
        {
            for (int i = 0; i < slotCount; i++)
            {
                if (Math.Abs(slotX(i) - x) <= epsilon && Math.Abs(slotZ(i) - z) <= epsilon)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// A pad named <paramref name="name"/> already exists in <paramref name="slot"/> with
        /// <paramref name="yaw"/>. On the authority the pad is the truth; elsewhere the host's
        /// assignment wins and a mismatch is reported. An existing pad consumes its assignment.
        /// </summary>
        public SeedResult NotePadExists(string name, int slot, int yaw, bool authority)
        {
            RaiseHighWater(slot + 1);
            if (!_entries.TryGetValue(name, out Entry e))
            {
                _entries[name] = new Entry { Slot = slot, Yaw = yaw, Consumed = true };
                return SeedResult.Learned;
            }

            SeedResult result = SeedResult.Matched;
            if (e.Slot != slot)
            {
                if (authority)
                {
                    e.Slot = slot;
                    e.Yaw = yaw;
                    result = SeedResult.Rebased;
                }
                else
                {
                    result = SeedResult.Mismatch;
                }
            }
            e.Consumed = true;
            return result;
        }

        /// <summary>
        /// True when <paramref name="slot"/> is held by an assignment other than
        /// <paramref name="exceptName"/>'s: still unconsumed, its pad alive, or its spawn in flight.
        /// </summary>
        public bool IsSlotOccupied(int slot, string exceptName, Func<string, bool> isAlive)
        {
            foreach (var kv in _entries)
            {
                if (kv.Value.Slot != slot || kv.Key == exceptName)
                    continue;
                if (!kv.Value.Consumed || isAlive(kv.Key) || _inFlight.Contains(kv.Key))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Next free slot for <paramref name="name"/>: the monotonic counter first (vanilla parity),
        /// then, when the table is exhausted (vanilla would throw), the lowest slot nobody occupies.
        /// -1 when there is none.
        /// </summary>
        public int PickFreeSlot(int slotCount, string name, Func<string, bool> isAlive)
        {
            if (slotCount <= 0)
                return -1;
            for (int s = HighWater; s < slotCount; s++)
            {
                if (!IsSlotOccupied(s, name, isAlive))
                    return s;
            }
            for (int s = 0; s < slotCount; s++)
            {
                if (!IsSlotOccupied(s, name, isAlive))
                    return s;
            }
            return -1;
        }

        /// <summary>
        /// Host / offline: the assignment for <paramref name="name"/>, allocating one if it has none.
        /// The same name always gets the same slot while its assignment is unconsumed (a peer reserved
        /// it), its pad is alive, or its spawn is in flight. Null when no slot is free.
        /// </summary>
        public Entry Allocate(string name, int slotCount, Func<string, bool> isAlive,
            Func<int> nextYaw, out bool isNew)
        {
            isNew = false;
            if (string.IsNullOrEmpty(name))
                return null;

            if (_entries.TryGetValue(name, out Entry existing)
                && (!existing.Consumed || isAlive(name) || _inFlight.Contains(name)))
                return existing;

            int slot = PickFreeSlot(slotCount, name, isAlive);
            if (slot < 0)
                return null;

            var e = new Entry { Slot = slot, Yaw = nextYaw(), Consumed = false };
            _entries[name] = e;
            RaiseHighWater(slot + 1);
            isNew = true;
            return e;
        }

        /// <summary>
        /// Client: take the host's assignment for <paramref name="name"/> (Slot -1 = the host has none
        /// free: vanilla placement). Ignored while this machine is mid-spawn with the one it holds.
        /// </summary>
        public bool ApplyHostAssignment(string name, int slot, int yaw)
        {
            if (string.IsNullOrEmpty(name) || _inFlight.Contains(name))
                return false;
            _entries[name] = new Entry { Slot = slot, Yaw = yaw, Consumed = false };
            return true;
        }

        /// <summary>
        /// An "unavailable" answer (Slot &lt; 0) places the pad the vanilla way. It is consumed so the
        /// next spawn of this name asks the host again instead of reusing the stale answer.
        /// </summary>
        public static Entry ConsumeIfUnavailable(Entry entry)
        {
            if (entry != null && entry.Slot < 0)
            {
                entry.Consumed = true;
                return null;
            }
            return entry;
        }

        /// <summary>
        /// The marker for <paramref name="name"/> is being spawned: hand out its assigned yaw once
        /// (an unconsumed assignment with a real slot) and consume it.
        /// </summary>
        public bool TryConsumeYaw(string name, out int yaw)
        {
            if (TryGet(name, out Entry e) && !e.Consumed && e.Slot >= 0)
            {
                e.Consumed = true;
                yaw = e.Yaw;
                return true;
            }
            yaw = 0;
            return false;
        }

        /// <summary>
        /// Late-join snapshot filter: a real slot, and not stale (a consumed assignment whose pad is
        /// gone and whose spawn is not running; a fresh spawn will ask again).
        /// </summary>
        public bool ShouldSnapshot(string name, Entry entry, Func<string, bool> isAlive)
        {
            if (entry.Slot < 0)
                return false;
            return !(entry.Consumed && !isAlive(name) && !_inFlight.Contains(name));
        }
    }
}
