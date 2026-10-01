using System;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Unreliable PhysicsState stream: one sequence counter owned by the network layer, and
    /// snapshots split to the peers' single-datagram limit (the old single-packet send threw
    /// TooBigPacketException as soon as a busy scene outgrew the 1 KB MTU).
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>Framed bytes besides entries: type + Reliable + Sequence + four counts.</summary>
        private const int PhysicsStateFrameBytes = 1 + 1 + 4 + 4 * 4;

        /// <summary>
        /// Unreliable PhysicsState sequence for everything this node sends (own snapshots and host
        /// forwards of a client's free bodies). Receivers key by the immediate sender, so a
        /// forwarded snapshot must carry the forwarder's counter, never the originator's.
        /// </summary>
        private uint _nextPhysicsStateSequence;

        private readonly NetWriter _physMeasureWriter = new NetWriter();
        private WorldObjectState[] _physSliceObjects = Array.Empty<WorldObjectState>();
        private DoorState[] _physSliceDoors = Array.Empty<DoorState>();
        private TrapState[] _physSliceTraps = Array.Empty<TrapState>();
        private GeneratorState[] _physSliceGenerators = Array.Empty<GeneratorState>();

        /// <summary>
        /// Send a free-body PhysicsState snapshot unreliably, re-stamped from this node's own
        /// counter and split into as many packets as the smallest targeted peer MTU requires.
        /// Each packet carries its own newer sequence, so the receiver accepts all of them.
        /// </summary>
        internal void SendPhysicsStateStream(PhysicsStateMessage msg, bool skipLoadingPeers = false,
            int excludePlayerId = 0)
        {
            if (PeerCount == 0)
                return;

            msg.Reliable = false;
            msg.Sequence = ++_nextPhysicsStateSequence;
            PhysicsStateMessage whole = msg;
            BuildPacketHot(NetMessageType.PhysicsState, w => whole.Serialize(w),
                out byte[] data, out int length);

            int budget = MinUnreliablePacketBytes(false, skipLoadingPeers, excludePlayerId);
            if (length <= budget)
            {
                FanOutHot(data, length, DeliveryMethod.Unreliable, skipLoadingPeers, excludePlayerId);
                return;
            }

            SendPhysicsStateChunked(msg, length, budget, skipLoadingPeers, excludePlayerId);
        }

        /// <summary>Over-limit path, kept out of the hot method so its closures only allocate when it runs.</summary>
        private void SendPhysicsStateChunked(PhysicsStateMessage msg, int length, int budget,
            bool skipLoadingPeers, int excludePlayerId)
        {
            int oc = msg.EffectiveObjectCount;
            int dc = msg.EffectiveDoorCount;
            int tc = msg.EffectiveTrapCount;
            int gc = msg.EffectiveGeneratorCount;
            int cap = Math.Max(budget - PhysicsStateFrameBytes, 64);
            int oi = 0, di = 0, ti = 0, gi = 0;
            int packets = 0;
            while (oi < oc || di < dc || ti < tc || gi < gc)
            {
                int os = oi, ds = di, ts = ti, gs = gi;
                int used = 0;
                bool full = false;

                // Always take at least one entry per packet so the loop terminates; an entry that
                // alone exceeds the limit is promoted to ReliableOrdered by SendToLanPeer.
                while (!full && oi < oc)
                {
                    _physMeasureWriter.Reset();
                    msg.Objects[oi].Serialize(_physMeasureWriter);
                    int sz = _physMeasureWriter.Length;
                    if (used > 0 && used + sz > cap) { full = true; break; }
                    used += sz;
                    oi++;
                }
                while (!full && di < dc)
                {
                    _physMeasureWriter.Reset();
                    msg.Doors[di].Serialize(_physMeasureWriter);
                    int sz = _physMeasureWriter.Length;
                    if (used > 0 && used + sz > cap) { full = true; break; }
                    used += sz;
                    di++;
                }
                while (!full && ti < tc)
                {
                    _physMeasureWriter.Reset();
                    msg.Traps[ti].Serialize(_physMeasureWriter);
                    int sz = _physMeasureWriter.Length;
                    if (used > 0 && used + sz > cap) { full = true; break; }
                    used += sz;
                    ti++;
                }
                while (!full && gi < gc)
                {
                    _physMeasureWriter.Reset();
                    msg.Generators[gi].Serialize(_physMeasureWriter);
                    int sz = _physMeasureWriter.Length;
                    if (used > 0 && used + sz > cap) { full = true; break; }
                    used += sz;
                    gi++;
                }

                var slice = new PhysicsStateMessage
                {
                    Reliable = false,
                    Sequence = ++_nextPhysicsStateSequence
                };
                int n = oi - os;
                if (n > 0)
                {
                    EnsureSliceCapacity(ref _physSliceObjects, n);
                    Array.Copy(msg.Objects, os, _physSliceObjects, 0, n);
                    slice.Objects = _physSliceObjects;
                    slice.ObjectCount = n;
                }
                n = di - ds;
                if (n > 0)
                {
                    EnsureSliceCapacity(ref _physSliceDoors, n);
                    Array.Copy(msg.Doors, ds, _physSliceDoors, 0, n);
                    slice.Doors = _physSliceDoors;
                    slice.DoorCount = n;
                }
                n = ti - ts;
                if (n > 0)
                {
                    EnsureSliceCapacity(ref _physSliceTraps, n);
                    Array.Copy(msg.Traps, ts, _physSliceTraps, 0, n);
                    slice.Traps = _physSliceTraps;
                    slice.TrapCount = n;
                }
                n = gi - gs;
                if (n > 0)
                {
                    EnsureSliceCapacity(ref _physSliceGenerators, n);
                    Array.Copy(msg.Generators, gs, _physSliceGenerators, 0, n);
                    slice.Generators = _physSliceGenerators;
                    slice.GeneratorCount = n;
                }

                PhysicsStateMessage chunk = slice;
                BuildPacketHot(NetMessageType.PhysicsState, w => chunk.Serialize(w),
                    out byte[] cdata, out int clen);
                FanOutHot(cdata, clen, DeliveryMethod.Unreliable, skipLoadingPeers, excludePlayerId);
                packets++;
            }

            ModLog.TraceRate(LogCat.Network, "phys-split",
                () => "PhysicsState split into " + packets + " packets (" + length + "B whole > "
                    + budget + "B limit; objs=" + oc + " doors=" + dc + " traps=" + tc + " gens=" + gc + ")",
                5f);
        }

        private static void EnsureSliceCapacity<T>(ref T[] buf, int n)
        {
            if (buf.Length < n)
                buf = new T[Math.Max(n, buf.Length == 0 ? n : buf.Length * 2)];
        }
    }
}
