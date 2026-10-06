using System;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The examine lines of a <c>DescriptionPool</c> are one deck for the whole party. Vanilla
    /// draws a random line and takes it out of the pool (<c>getDescriptionFromPool</c>), and refills
    /// an empty pool from its preset on the next draw. Each machine used to draw from its own copy,
    /// and the host's re-run of a client's examine drew a second, different line, so lines another
    /// player had already read came up again.
    /// <para>
    /// The examiner draws at once (its text shows with no wait) and its examine carries the line it
    /// drew (<c>ExamineObject</c>); the host takes that same line out of its deck when it re-runs
    /// the examine, and its state broadcast tells every other machine to take it out too. A draw
    /// that refilled an empty pool says so, so a machine applying it refills first. A joiner gets
    /// every pool's remaining lines in its late-join bulk (vanilla does not save pools; they start
    /// full on every load). Two players drawing from one pool in the same instant can both read the
    /// same line; the decks still agree afterwards.
    /// </para>
    /// </summary>
    internal static class DescriptionDeck
    {
        private static readonly FieldInfo PoolsField = AccessTools.Field(typeof(DescriptionPool), "pools"); // process-scoped: reflection cache

        /// <summary>The draw made during the examine now running (sent with it).</summary>
        private static bool _hasDraw;
        private static string _drawPool;
        private static string _drawLine;
        private static bool _drawRefreshed;

        /// <summary>Host re-running a client's examine: that client's line, already taken out of the deck.</summary>
        private static string _forcedPool;
        private static string _forcedLine;

        /// <summary>Nesting of <c>getDescriptionFromPool</c> (it calls itself after a refill).</summary>
        private static int _drawDepth;

        internal static void Reset()
        {
            _hasDraw = false;
            _drawPool = null;
            _drawLine = null;
            _drawRefreshed = false;
            _forcedPool = null;
            _forcedLine = null;
            _drawDepth = 0;
        }

        private static bool Connected => ModRuntime.Network != null && ModRuntime.Network.IsConnected;

        private static List<DescriptionPool.Pool> Pools()
        {
            DescriptionPool dp = Singleton<DescriptionPool>.Instance;
            return dp != null && PoolsField != null ? PoolsField.GetValue(dp) as List<DescriptionPool.Pool> : null;
        }

        private static DescriptionPool.Pool Find(string poolName)
        {
            List<DescriptionPool.Pool> pools = Pools();
            if (pools == null || string.IsNullOrEmpty(poolName))
                return null;
            for (int i = 0; i < pools.Count; i++)
            {
                if (pools[i] != null && pools[i].name == poolName)
                    return pools[i];
            }
            return null;
        }

        /// <summary>A local examine starts: forget any earlier draw.</summary>
        internal static void BeginExamine()
        {
            _hasDraw = false;
            _drawPool = null;
            _drawLine = null;
            _drawRefreshed = false;
        }

        /// <summary>The draw of the examine that just ran, once: a later examine never resends it.</summary>
        internal static bool TakeDraw(out string pool, out string line, out bool refreshed)
        {
            pool = _drawPool;
            line = _drawLine;
            refreshed = _drawRefreshed;
            bool had = _hasDraw;
            BeginExamine();
            return had;
        }

        private static void Record(string pool, string line, bool refreshed)
        {
            _hasDraw = true;
            _drawPool = pool;
            _drawLine = line ?? "";
            _drawRefreshed = refreshed;
        }

        /// <summary>
        /// Host, before re-running a client's examine: take the client's line out of the deck now
        /// (the examine may not reach the pool on the host), remember it for the state broadcast,
        /// and have the re-run's draw return that line.
        /// </summary>
        internal static void BeginRemoteExamine(ExamineObjectMessage msg)
        {
            BeginExamine();
            if (!msg.HasDraw)
                return;
            Apply(msg.DrawPool, msg.DrawLine, msg.DrawRefreshed);
            Record(msg.DrawPool, msg.DrawLine, msg.DrawRefreshed);
            _forcedPool = msg.DrawPool;
            _forcedLine = msg.DrawLine;
        }

        internal static void EndRemoteExamine()
        {
            _forcedPool = null;
            _forcedLine = null;
            BeginExamine();
        }

        /// <summary>A draw another machine made: refill first if it refilled, then take its line out.</summary>
        internal static void Apply(string poolName, string line, bool refreshed)
        {
            DescriptionPool.Pool pool = Find(poolName);
            if (pool == null)
                return;
            if (refreshed)
                pool.refreshDescriptions();
            if (pool.descriptions != null && line != null)
                pool.descriptions.Remove(line);
        }

        /// <summary>
        /// <c>getDescriptionFromPool</c> prefix. <paramref name="state"/>: 0 untracked, 1 a draw,
        /// 2 a draw that refills an empty pool first.
        /// </summary>
        internal static bool DrawPrefix(string poolName, ref string result, out int state)
        {
            state = 0;
            if (!Connected)
                return true;
            if (_forcedLine != null && _forcedPool == poolName)
            {
                result = _forcedLine;
                _forcedPool = null;
                _forcedLine = null;
                return false;
            }
            DescriptionPool.Pool pool = Find(poolName);
            if (pool == null)
                return true;
            _drawDepth++;
            state = pool.descriptions != null && pool.descriptions.Count == 0 && !pool.doNotRefresh ? 2 : 1;
            return true;
        }

        internal static void DrawFinalizer(Exception exception, string poolName, string result, int state)
        {
            if (state == 0)
                return;
            if (_drawDepth > 0)
                _drawDepth--;
            if (exception == null && _drawDepth == 0)
                Record(poolName, result, state == 2);
        }

        // ---- late join ----------------------------------------------------------------------------

        /// <summary>Host, late-join bulk: every pool's remaining lines.</summary>
        internal static void SendBulkTo(LanNetworkManager net, int playerId)
        {
            List<DescriptionPool.Pool> pools = Pools();
            if (net == null || pools == null || playerId <= 0)
                return;
            var names = new List<string>(pools.Count);
            var lines = new List<string[]>(pools.Count);
            for (int i = 0; i < pools.Count; i++)
            {
                DescriptionPool.Pool p = pools[i];
                if (p == null || string.IsNullOrEmpty(p.name) || p.descriptions == null)
                    continue;
                names.Add(p.name);
                lines.Add(p.descriptions.ToArray());
            }
            var msg = new CosmeticStateMessage
            {
                Kind = CosmeticStateMessage.KindDecks,
                DeckNames = names.ToArray(),
                DeckLines = lines.ToArray()
            };
            net.SendToPlayer(playerId, NetMessageType.CosmeticState, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Client: the host's decks replace this machine's.</summary>
        internal static void ApplyBulk(CosmeticStateMessage msg)
        {
            if (msg.DeckNames == null || msg.DeckLines == null)
                return;
            int applied = 0;
            for (int i = 0; i < msg.DeckNames.Length && i < msg.DeckLines.Length; i++)
            {
                DescriptionPool.Pool pool = Find(msg.DeckNames[i]);
                if (pool == null)
                    continue;
                pool.descriptions = new List<string>(msg.DeckLines[i] ?? new string[0]);
                applied++;
            }
            ModLog.Event(LogCat.World, "[Cosmetic] examine decks from host: " + applied + "/" + msg.DeckNames.Length);
        }
    }
}
