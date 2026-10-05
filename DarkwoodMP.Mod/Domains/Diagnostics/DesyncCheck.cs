using System;
using System.Collections.Generic;
using System.Text;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Desync checker. Every few seconds the host sends each settled client a fingerprint of the
    /// state they should share (<see cref="DesyncDigestMessage"/>): a hash per world section, and
    /// the entries themselves for the small sections around that player. The client builds the
    /// same sections from its own world and compares; for a hashed section that differs it asks
    /// for the host's entries and diffs them key by key. A difference that shows in two checks
    /// in a row is logged as a DESYNC line on the client and, through
    /// <see cref="DesyncReportMessage"/>, on the host; it is logged once more when it goes away.
    /// Read-only: it never changes the world, it only tells where the two copies drifted.
    /// </summary>
    internal static partial class DesyncCheck
    {
        /// <summary>Game minutes the client clock may trail or lead the host's (sync in flight).</summary>
        private const int ClockToleranceMinutes = 10;
        /// <summary>Wire cap for one section's entries (NetWriter long strings stop at 256 KiB).</summary>
        private const int MaxEntryChars = 200 * 1024;
        private const float OkLogEverySec = 300f;
        private const byte ClockSection = 0;

        private static int _seq;                 // reset-in: Reset
        private static float _nextAt;            // reset-in: Reset
        /// <summary>Host: peer → checks in a row it was settled at (a digest needs two).</summary>
        private static readonly Dictionary<int, int> _peerSettledChecks = new Dictionary<int, int>(); // reset-in: Reset
        private static readonly DesyncLedger _ledger = new DesyncLedger(); // process-scoped: cleared in Reset
        private static int _clientChecks;        // reset-in: Reset
        private static float _clientOkLogAt;     // reset-in: Reset

        internal static void Reset()
        {
            _seq = 0;
            _nextAt = 0f;
            _peerSettledChecks.Clear();
            _ledger.Clear();
            _clientChecks = 0;
            _clientOkLogAt = 0f;
            _syncedContainers.Clear();
        }

        private static bool Enabled => ModConfig.DesyncCheck == null || ModConfig.DesyncCheck.Value;

        private static float Interval =>
            Mathf.Clamp(ModConfig.DesyncCheckIntervalSec != null ? ModConfig.DesyncCheckIntervalSec.Value : 15, 5, 300);

        /// <summary>
        /// This machine's world is in a steady state worth comparing: loaded, no dream (pads load
        /// per peer and sit-outs have none), no entry or exit transition.
        /// </summary>
        private static bool LocalWorldSettled()
        {
            if (Core.mainMenu || Core.loadingGame || !Core.worldGenFinished() || Player.Instance == null)
                return false;
            if (Singleton<Controller>.Instance == null || Singleton<Flags>.Instance == null)
                return false;
            if (DreamSession.IsActive || DreamSyncManager.IsDreamActive || DreamSyncManager.IsHostDreamEntryPending
                || DreamSyncManager.HasPendingEntryTransition)
                return false;
            Dreams dreams = Dreams.Instance;
            if (dreams != null && (dreams.dreaming || dreams.dreamPrepared
                    || (dreams.startTransition != null && dreams.startTransition.isPlaying)))
                return false;
            return true;
        }

        // ---------------------------------------------------------------- host

        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Host || !Enabled)
                return;
            float now = Time.unscaledTime;
            if (now < _nextAt)
                return;
            _nextAt = now + Interval;
            if (!net.DesyncLinkQuiet || !LocalWorldSettled())
            {
                _peerSettledChecks.Clear();
                return;
            }

            var targets = new List<int>(4);
            var seen = new HashSet<int>();
            foreach (int id in net.EnumeratePeerIds())
            {
                if (!net.DesyncPeerSettled(id) || net.GetProxy(id) == null)
                    continue;
                seen.Add(id);
                _peerSettledChecks.TryGetValue(id, out int n);
                _peerSettledChecks[id] = n + 1;
                if (n + 1 >= 2)
                    targets.Add(id);
            }
            var drop = new List<int>();
            foreach (int id in _peerSettledChecks.Keys)
                if (!seen.Contains(id))
                    drop.Add(id);
            foreach (int id in drop)
                _peerSettledChecks.Remove(id);
            if (targets.Count == 0)
                return;

            _seq++;
            // World sections once; the ones around a player per target.
            var global = new Dictionary<byte, string>();
            foreach (Section s in Sections)
                if (!s.Focus)
                    global[s.Id] = Build(s, HostCtx(net, 0));

            foreach (int id in targets)
            {
                Ctx ctx = HostCtx(net, id);
                int n = Sections.Length;
                var msg = new DesyncDigestMessage
                {
                    Seq = _seq,
                    TotalTime = Singleton<Controller>.Instance.totalTime,
                    Chapter = Chapter(),
                    SectionCount = (byte)n,
                    SectionIds = new byte[n],
                    Hashes = new uint[n],
                    Counts = new int[n],
                    Inline = new string[n]
                };
                for (int i = 0; i < n; i++)
                {
                    Section s = Sections[i];
                    string text = s.Focus ? Build(s, ctx) : global[s.Id];
                    msg.SectionIds[i] = s.Id;
                    msg.Hashes[i] = DesyncEntries.Hash(text);
                    msg.Counts[i] = DesyncEntries.Count(text);
                    msg.Inline[i] = s.Focus ? Cap(text, out _) : string.Empty;
                }
                net.SendDesyncDigest(id, msg);
            }
        }

        internal static void HostOnDetailRequest(LanNetworkManager net, int playerId, DesyncDetailRequestMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0 || msg.SectionIds == null)
                return;
            if (!LocalWorldSettled())
                return;
            Ctx ctx = HostCtx(net, playerId);
            int sent = 0;
            for (int i = 0; i < msg.SectionIds.Length && sent < Sections.Length; i++)
            {
                Section s = Find(msg.SectionIds[i]);
                if (s == null)
                    continue;
                string text = Cap(Build(s, ctx), out bool cut);
                net.SendDesyncDetail(playerId, new DesyncDetailMessage
                {
                    Seq = msg.Seq,
                    SectionId = s.Id,
                    Truncated = cut,
                    Entries = text
                });
                sent++;
            }
        }

        internal static void HostOnReport(LanNetworkManager net, int playerId, DesyncReportMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Host || string.IsNullOrEmpty(msg.Lines))
                return;
            string[] lines = msg.Lines.Split('\n');
            int n = 0;
            foreach (string raw in lines)
            {
                if (raw.Length == 0)
                    continue;
                if (++n > DesyncLedger.MaxLinesPerCheck * 4)
                    break;
                string line = raw.Length > 400 ? raw.Substring(0, 400) : raw;
                if (line.StartsWith("DESYNC", StringComparison.Ordinal))
                    ModLog.Warn(LogCat.Session, "[Desync p" + playerId + "] " + line);
                else
                    ModLog.Event(LogCat.Session, "[Desync p" + playerId + "] " + line);
            }
        }

        private static string Cap(string text, out bool cut)
        {
            cut = false;
            if (text == null || text.Length <= MaxEntryChars)
                return text ?? string.Empty;
            cut = true;
            int nl = text.LastIndexOf('\n', MaxEntryChars - 1);
            return nl > 0 ? text.Substring(0, nl + 1) : string.Empty;
        }

        // -------------------------------------------------------------- client

        internal static void ClientOnDigest(LanNetworkManager net, DesyncDigestMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client || !Enabled || msg.SectionIds == null)
                return;
            if (!net.DesyncLinkQuiet || !LocalWorldSettled())
                return;
            _clientChecks++;
            var lines = new List<string>();

            var clock = new List<DesyncEntries.Diff>(2);
            int chapter = Chapter();
            if (chapter != msg.Chapter)
                clock.Add(new DesyncEntries.Diff { Key = "chapter", Host = msg.Chapter.ToString(), Client = chapter.ToString() });
            int total = Singleton<Controller>.Instance.totalTime;
            if (Math.Abs(total - msg.TotalTime) > ClockToleranceMinutes)
                clock.Add(new DesyncEntries.Diff { Key = "time", Host = Clock(msg.TotalTime), Client = Clock(total) });
            lines.AddRange(_ledger.Evaluate(ClockSection, "Clock", clock));

            Ctx ctx = ClientCtx(net);
            var request = new List<byte>();
            for (int i = 0; i < msg.SectionIds.Length; i++)
            {
                Section s = Find(msg.SectionIds[i]);
                if (s == null)
                    continue;
                string mine = Build(s, ctx);
                if (s.Focus)
                {
                    lines.AddRange(_ledger.Evaluate(s.Id, s.Name, DesyncEntries.Compare(
                        DesyncEntries.Parse(msg.Inline != null && i < msg.Inline.Length ? msg.Inline[i] : null),
                        DesyncEntries.Parse(mine), s.Same)));
                }
                else if (DesyncEntries.Hash(mine) == (msg.Hashes != null && i < msg.Hashes.Length ? msg.Hashes[i] : 0u))
                {
                    lines.AddRange(_ledger.Evaluate(s.Id, s.Name, null));
                }
                else
                {
                    request.Add(s.Id);
                }
            }
            if (request.Count > 0)
            {
                net.SendDesyncDetailRequest(new DesyncDetailRequestMessage
                {
                    Seq = msg.Seq,
                    SectionCount = (byte)request.Count,
                    SectionIds = request.ToArray()
                });
            }
            Emit(net, msg.Seq, lines);

            float now = Time.unscaledTime;
            if (now >= _clientOkLogAt)
            {
                _clientOkLogAt = now + OkLogEverySec;
                ModLog.Event(LogCat.Session, "[Desync] check #" + _clientChecks + " seq " + msg.Seq
                    + ": " + _ledger.ReportedCount + " open desync(s)"
                    + (request.Count > 0 ? ", " + request.Count + " section(s) being compared" : ""));
            }
        }

        internal static void ClientOnDetail(LanNetworkManager net, DesyncDetailMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client || !Enabled)
                return;
            if (!net.DesyncLinkQuiet || !LocalWorldSettled())
                return;
            Section s = Find(msg.SectionId);
            if (s == null)
                return;
            string mine = Build(s, ClientCtx(net));
            List<string> lines = _ledger.Evaluate(s.Id, s.Name, DesyncEntries.Compare(
                DesyncEntries.Parse(msg.Entries), DesyncEntries.Parse(mine), s.Same, msg.Truncated));
            Emit(net, msg.Seq, lines);
        }

        private static void Emit(LanNetworkManager net, int seq, List<string> lines)
        {
            if (lines.Count == 0)
                return;
            var sb = new StringBuilder();
            foreach (string line in lines)
            {
                if (line.StartsWith("DESYNC", StringComparison.Ordinal))
                    ModLog.Warn(LogCat.Session, "[Desync] " + line);
                else
                    ModLog.Event(LogCat.Session, "[Desync] " + line);
                sb.Append(line).Append('\n');
            }
            net.SendDesyncReport(new DesyncReportMessage { Seq = seq, Lines = sb.ToString() });
        }

        private static string Clock(int total) => "day " + (total / 1440) + " " + (total % 1440 / 60) + ":" + (total % 60).ToString("00");

        private static int Chapter()
        {
            WorldGenerator wg = Singleton<WorldGenerator>.Instance;
            return wg != null ? wg.chapterID : 0;
        }
    }
}
