using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Host-authoritative placement of outside-location pads (bunkers, villages, cellars, dream pockets).
    /// <para>
    /// Vanilla <c>OutsideLocations.spawnLocation</c> puts each pad at
    /// <c>locationPositions[actualSpawnedLocationsCount]</c> and then increments, and rotates it by
    /// <c>Core.getRandomHalfRotation()</c>. So the world position (and yaw) of a pad depends on the order
    /// in which THIS machine first spawned pads. Co-op peers spawn pads locally on first enter, so peers
    /// that first-enter locations in different orders got the same location at different coordinates,
    /// while nearly all in-pad sync (doors, padlocks, traps, GameEvents, physics, stations) is keyed by
    /// absolute position.
    /// </para>
    /// <para>
    /// Save persistence (vanilla): <c>OutsideLocations.SaveState</c> stores spawnedLocations
    /// (name → saved GameObject id) and <c>actualSpawnedLocationsCount</c>; the pad objects themselves are
    /// saved and restored at their saved transforms (no re-slotting on load). A client loads the host's
    /// save through world share, so pads that exist at share time already agree. Only pads first spawned
    /// after that (or on a peer that never had them) diverge, which is what this class fixes.
    /// </para>
    /// <para>
    /// Design: the host owns a locationName → (slot, yaw) map. Every peer, host included, spawns a location at
    /// the host-assigned slot and yaw. The host derives the map for pads that already exist from their real
    /// transforms (so nothing extra is persisted; save-loaded pads and pads placed by the dream path are
    /// covered), allocates new slots monotonically like vanilla (falling back to the lowest free slot when the
    /// table is exhausted), and broadcasts every new assignment. A client that first-enters a location it has
    /// no assignment for asks the host and waits (the spawn coroutine is wrapped) before the marker is placed.
    /// Assignments are single-use per machine: consumed when the marker is spawned, so a later re-spawn of the
    /// same name (destroyed dream, destroyed location) asks the host again.
    /// </para>
    /// </summary>
    internal static partial class OutsidePadSlots
    {
        /// <summary>
        /// The Unity-free allocation rules (map, high-water mark, in-flight set, free-slot choice);
        /// see <see cref="OutsidePadSlotLogic"/>. This class adds the game side around it.
        /// </summary>
        private static readonly OutsidePadSlotLogic Logic = new OutsidePadSlotLogic();

        private static readonly System.Random YawRng = new System.Random();

        private static readonly FieldInfo PositionsField =
            AccessTools.Field(typeof(OutsideLocations), "locationPositions");

        private static OutsideLocations _owner;

        /// <summary>Pad transforms sit exactly on the slot coordinates (multiples of 5000); this only absorbs float noise.</summary>
        private const float SlotMatchEpsilon = 0.5f;

        private const float RequestRetrySec = 3f;

        /// <summary>
        /// Upper bound on a client's wait for the host's slot. Without it a host that never answers
        /// left the spawn coroutine (and the player's loading screen) parked forever.
        /// </summary>
        private const float SlotWaitTimeoutSec = 30f;

        // ---- state ----------------------------------------------------------------------------

        /// <summary>
        /// Network stop: host-assigned slots belong to that session. The host re-derives its map
        /// from the live pad transforms on the next allocation (see <see cref="SeedFromWorld"/>).
        /// </summary>
        internal static void Reset()
        {
            Logic.Reset();
            _owner = null;
        }

        /// <summary>
        /// Bind to the current <see cref="OutsideLocations"/>. A new instance (new world / chapter load)
        /// means every previous assignment is meaningless.
        /// </summary>
        private static bool Bind(OutsideLocations ol)
        {
            if (ol == null)
                return false;
            if (!ReferenceEquals(_owner, ol))
            {
                Logic.Reset();
                _owner = ol;
            }
            return true;
        }

        private static List<Vector3> Positions(OutsideLocations ol)
        {
            return PositionsField != null ? PositionsField.GetValue(ol) as List<Vector3> : null;
        }

        private static int DeriveSlot(OutsideLocations ol, Vector3 pos)
        {
            var slots = Positions(ol);
            if (slots == null)
                return -1;
            return OutsidePadSlotLogic.DeriveSlot(slots.Count, i => slots[i].x, i => slots[i].z,
                pos.x, pos.z, SlotMatchEpsilon);
        }

        private static bool IsAlive(OutsideLocations ol, string name)
        {
            return ol.spawnedLocations != null
                && ol.spawnedLocations.TryGetValue(name, out Location loc)
                && loc != null;
        }

        /// <summary>
        /// Learn the assignments of pads that already exist (save-loaded, dream-path, or spawned before
        /// this class knew about them) from their real transforms. On the host the pad is the truth;
        /// on a client the host's assignment wins and a mismatch is reported.
        /// </summary>
        private static void SeedFromWorld(OutsideLocations ol, bool authority)
        {
            if (ol.spawnedLocations == null)
                return;
            Logic.RaiseHighWater(ol.actualSpawnedLocationsCount);

            foreach (var kv in ol.spawnedLocations)
            {
                Location loc = kv.Value;
                if (loc == null)
                    continue;
                int slot = DeriveSlot(ol, loc.transform.position);
                if (slot < 0)
                    continue; // not a slot pad (worldgen location, tutorial scene)

                var result = Logic.NotePadExists(kv.Key, slot,
                    OutsidePadSlotLogic.NormalizeYaw(loc.transform.eulerAngles.y), authority);
                if (result == OutsidePadSlotLogic.SeedResult.Mismatch
                    && Logic.TryGet(kv.Key, out OutsidePadSlotLogic.Entry assigned))
                {
                    ModLog.WarnRate(LogCat.World, "padslot-mismatch:" + kv.Key,
                        "[PadSlot] local pad '" + kv.Key + "' sits in slot " + slot
                        + " but host assigned slot " + assigned.Slot
                        + " (pad predates host assignment; in-pad position sync will not line up)", 30f);
                }
            }
        }

        // ---- host allocation ------------------------------------------------------------------

        /// <summary>
        /// Host / offline: the assignment for <paramref name="name"/>, allocating one if it has none.
        /// Returns an existing assignment when one is unconsumed (a peer reserved it) or its pad is alive.
        /// </summary>
        internal static OutsidePadSlotLogic.Entry AllocateAuthoritative(OutsideLocations ol, string name,
            out bool isNew)
        {
            isNew = false;
            if (!Bind(ol) || string.IsNullOrEmpty(name))
                return null;
            SeedFromWorld(ol, authority: true);

            int slotCount = Positions(ol)?.Count ?? 0;
            var e = Logic.Allocate(name, slotCount, n => IsAlive(ol, n), () => 90 * YawRng.Next(0, 4),
                out isNew);
            if (e == null)
            {
                ModLog.Error(LogCat.World,
                    "[PadSlot] no free outside-location slot for '" + name + "' (all "
                    + slotCount + " slots in use) — vanilla placement will fail");
                return null;
            }
            if (isNew)
            {
                if (ol.actualSpawnedLocationsCount < Logic.HighWater)
                    ol.actualSpawnedLocationsCount = Logic.HighWater;
                ModLog.Event(LogCat.World,
                    "[PadSlot] assigned '" + name + "' slot " + e.Slot + " yaw " + e.Yaw);
            }
            return e;
        }

        // ---- spawn wrapper --------------------------------------------------------------------

        private static bool IsClientOfHost(out LanNetworkManager net)
        {
            net = ModRuntime.Network;
            // Role only: IsConnected is PeerCount > 0, which is false for a client during a
            // soft-reconnect blip. Allocating locally in that window placed the pad at different
            // coordinates from the host's.
            return net != null && net.Role == NetworkRole.Client;
        }

        /// <summary>
        /// Wraps vanilla <c>OutsideLocations.spawnLocation</c>: obtains the host-assigned slot first (a client
        /// waits for the host), makes vanilla read that slot, and restores the shared counter afterwards.
        /// </summary>
        internal static IEnumerator WrapSpawn(OutsideLocations ol, string name, IEnumerator original)
        {
            int savedCount = ol != null ? ol.actualSpawnedLocationsCount : 0;
            OutsidePadSlotLogic.Entry entry = null;
            bool client = IsClientOfHost(out LanNetworkManager net);

            if (client)
            {
                float nextRequestAt = 0f;
                bool warnedWait = false;
                float waitDeadline = Time.unscaledTime + SlotWaitTimeoutSec;
                while (true)
                {
                    if (ol == null)
                    {
                        (original as IDisposable)?.Dispose();
                        yield break;
                    }
                    if (!IsClientOfHost(out net))
                    {
                        ModLog.Warn(LogCat.World,
                            "[PadSlot] lost host while waiting for slot of '" + name + "' — vanilla local placement");
                        entry = null;
                        break;
                    }
                    Bind(ol);
                    if (Logic.TryGet(name, out OutsidePadSlotLogic.Entry e) && !e.Consumed)
                    {
                        entry = e;
                        break;
                    }
                    if (Time.unscaledTime >= waitDeadline)
                    {
                        ModLog.Error(LogCat.World,
                            "[PadSlot] host gave no slot for '" + name + "' within "
                            + SlotWaitTimeoutSec.ToString("F0")
                            + "s — falling back to vanilla placement (pad may not line up with the host)");
                        entry = null;
                        break;
                    }
                    if (Time.unscaledTime >= nextRequestAt)
                    {
                        if (nextRequestAt > 0f && !warnedWait)
                        {
                            warnedWait = true;
                            ModLog.Warn(LogCat.World,
                                "[PadSlot] host has not answered slot request for '" + name + "' — retrying");
                        }
                        nextRequestAt = Time.unscaledTime + RequestRetrySec;
                        string reqName = name;
                        net.Send(NetMessageType.LocationPadSlotRequest,
                            w => new LocationPadSlotRequestMessage { LocationName = reqName }.Serialize(w),
                            DeliveryMethod.ReliableOrdered);
                    }
                    yield return null;
                }
            }
            else
            {
                if (ol != null)
                {
                    entry = AllocateAuthoritative(ol, name, out bool isNew);
                    if (isNew)
                        BroadcastEntry(name, entry);
                }
            }

            // Explicit "unavailable" from the host (Slot -1): placement stays vanilla and the answer is
            // consumed, so the next spawn of this name asks the host again.
            entry = OutsidePadSlotLogic.ConsumeIfUnavailable(entry);

            int slot = entry != null ? entry.Slot : -1;
            if (slot >= 0 && ol != null)
            {
                Bind(ol);
                Logic.BeginSpawn(name);
                if (client)
                    ReportLocalCollision(ol, name, slot);
            }

            try
            {
                int step = 0;
                while (true)
                {
                    // Vanilla: MoveNext #0 runs to `yield return 0`; MoveNext #1 reads
                    // locationPositions[actualSpawnedLocationsCount] before its next yield.
                    if (step == 1 && slot >= 0 && ol != null)
                        ol.actualSpawnedLocationsCount = slot;
                    if (!original.MoveNext())
                        break;
                    step++;
                    yield return original.Current;
                }
            }
            finally
            {
                (original as IDisposable)?.Dispose();
                if (ol != null && slot >= 0)
                {
                    Logic.EndSpawn(name);
                    Logic.RaiseHighWater(slot + 1);
                    // Vanilla's own increment left slot+1; the counter must stay the shared high-water.
                    int final = Math.Max(Math.Max(savedCount, ol.actualSpawnedLocationsCount), Logic.HighWater);
                    ol.actualSpawnedLocationsCount = final;
                }
            }
        }

        private static void ReportLocalCollision(OutsideLocations ol, string name, int slot)
        {
            var slots = Positions(ol);
            if (slots == null || slot >= slots.Count || ol.spawnedLocations == null)
                return;
            foreach (var kv in ol.spawnedLocations)
            {
                if (kv.Key == name || kv.Value == null)
                    continue;
                if (DeriveSlot(ol, kv.Value.transform.position) == slot)
                {
                    ModLog.Error(LogCat.World,
                        "[PadSlot] host slot " + slot + " for '" + name + "' is already occupied locally by '"
                        + kv.Key + "' (that pad predates the host assignment); both pads will overlap");
                    return;
                }
            }
        }

        // ---- yaw hook (LocationMarker.spawnLocation transpiler target) -------------------------

        /// <summary>
        /// Called with vanilla's random half-rotation for the marker being spawned. Returns the host-assigned
        /// yaw when this location has an unconsumed assignment (and consumes it), else vanilla's value.
        /// </summary>
        public static int AdjustMarkerYaw(int vanillaYaw, LocationMarker marker)
        {
            try
            {
                var ol = Singleton<OutsideLocations>.Instance;
                if (marker == null || ol == null || string.IsNullOrEmpty(marker.locationName) || !Bind(ol))
                    return vanillaYaw;
                if (Logic.TryConsumeYaw(marker.locationName, out int assignedYaw))
                    return assignedYaw;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.World, "[PadSlot] AdjustMarkerYaw: " + ex.Message);
            }
            return vanillaYaw;
        }
    }
}
