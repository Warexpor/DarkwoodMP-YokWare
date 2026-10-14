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

        private static PhysicsStateMessage _physWhole; // process-scoped: the snapshot BuildPacketHot is writing
        private static readonly Action<NetWriter> WritePhysWhole = w => _physWhole.Serialize(w); // process-scoped: cached writer

        private readonly NetWriter _physMeasureWriter = new NetWriter();

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
            // A client in its solo dream has only its own copy's bodies to send.
            if (Sync.PersonalPads.BlocksSend(NetMessageType.PhysicsState, _role))
                return;

            msg.Reliable = false;
            msg.Sequence = ++_nextPhysicsStateSequence;
            _physWhole = msg;
            BuildPacketHot(NetMessageType.PhysicsState, WritePhysWhole, out byte[] data, out int length);
            _physWhole = default;

            int budget = MinUnreliablePacketBytes(false, skipLoadingPeers, excludePlayerId);
            if (length <= budget)
            {
                FanOutHot(data, length, DeliveryMethod.Unreliable, skipLoadingPeers, excludePlayerId);
                return;
            }

            SendPhysicsStateChunked(msg, length, budget, skipLoadingPeers, excludePlayerId);
        }

        /// <summary>Over-limit path, kept out of the hot method so its work only runs when it is needed.</summary>
        private void SendPhysicsStateChunked(PhysicsStateMessage msg, int length, int budget,
            bool skipLoadingPeers, int excludePlayerId)
        {
            int oc = msg.EffectiveObjectCount;
            int dc = msg.EffectiveDoorCount;
            int tc = msg.EffectiveTrapCount;
            int gc = msg.EffectiveGeneratorCount;

            // Every entry serialized once, back to back in category order (as the whole message
            // lays them out); each packet then copies its run of each category. The bytes are the
            // ones a PhysicsStateMessage of that slice would write.
            NetWriter body = _physMeasureWriter;
            body.Reset();
            EnsureSliceCapacity(ref _physEnds, oc + dc + tc + gc);
            int[] ends = _physEnds;
            int e = 0;
            for (int i = 0; i < oc; i++) { msg.Objects[i].Serialize(body); ends[e++] = body.Length; }
            for (int i = 0; i < dc; i++) { msg.Doors[i].Serialize(body); ends[e++] = body.Length; }
            for (int i = 0; i < tc; i++) { msg.Traps[i].Serialize(body); ends[e++] = body.Length; }
            for (int i = 0; i < gc; i++) { msg.Generators[i].Serialize(body); ends[e++] = body.Length; }
            byte[] bytes = body.RawBuffer;
            int total = oc + dc + tc + gc;

            int cap = Math.Max(budget - PhysicsStateFrameBytes, 64);
            int next = 0; // entry index across the four categories
            int packets = 0;
            while (next < total)
            {
                int first = next;
                int startAt = first > 0 ? ends[first - 1] : 0;
                // Always take at least one entry per packet so the loop terminates; an entry that
                // alone exceeds the limit is promoted to ReliableOrdered by SendToLanPeer.
                next++;
                while (next < total && ends[next] - startAt <= cap)
                    next++;

                NetWriter w = _hotPacketWriter;
                w.Reset();
                w.Put((byte)NetMessageType.PhysicsState);
                w.Put(false);
                w.Put(++_nextPhysicsStateSequence);
                PutRun(w, bytes, ends, first, next, 0, oc);
                PutRun(w, bytes, ends, first, next, oc, oc + dc);
                PutRun(w, bytes, ends, first, next, oc + dc, oc + dc + tc);
                PutRun(w, bytes, ends, first, next, oc + dc + tc, total);
                w.CopyDataInto(ref _hotPacketBuf, out int clen);
                FanOutHot(_hotPacketBuf, clen, DeliveryMethod.Unreliable, skipLoadingPeers, excludePlayerId);
                packets++;
            }

            if (ModLog.IsTrace(LogCat.Network))
                ModLog.TraceRate(LogCat.Network, "phys-split",
                    "PhysicsState split into " + packets + " packets (" + length + "B whole > "
                        + budget + "B limit; objs=" + oc + " doors=" + dc + " traps=" + tc + " gens=" + gc + ")",
                    5f);
        }

        /// <summary>
        /// One category's part of a packet: its count, then the entries of
        /// [<paramref name="from"/>, <paramref name="to"/>) that fall in [<paramref name="catFrom"/>, <paramref name="catTo"/>).
        /// </summary>
        private static void PutRun(NetWriter w, byte[] bytes, int[] ends, int from, int to, int catFrom, int catTo)
        {
            int a = Math.Max(from, catFrom);
            int b = Math.Min(to, catTo);
            int n = b > a ? b - a : 0;
            w.Put(n);
            if (n == 0)
                return;
            int startAt = a > 0 ? ends[a - 1] : 0;
            w.PutRaw(bytes, startAt, ends[b - 1] - startAt);
        }

        private int[] _physEnds = Array.Empty<int>();

        private static void EnsureSliceCapacity<T>(ref T[] buf, int n)
        {
            if (buf.Length < n)
                buf = new T[Math.Max(n, buf.Length == 0 ? n : buf.Length * 2)];
        }
    }
}
