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
        /// <summary>Slowest check on this machine since the last summary (ms).</summary>
        private static float _worstMs;           // reset-in: Reset
        private const float SlowCheckMs = 25f;

        internal static void Reset()
        {
            _seq = 0;
            _nextAt = 0f;
            _peerSettledChecks.Clear();
            _ledger.Clear();
            _clientChecks = 0;
            _clientOkLogAt = 0f;
            _worstMs = 0f;
            _syncedContainers.Clear();
            _hostJob = null;
            _clientJob = null;
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
            if (GameScreen.AtTitle || Core.loadingGame || !Core.worldGenFinished() || Player.Instance == null)
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

        /// <summary>Host: run the next check on the next tick (test pilot).</summary>
        internal static void RunSoon() => _nextAt = 0f;

        /// <summary>
        /// A check in progress. Building every section in one frame took 3-3.5 ms on the host (and
        /// as much on the client comparing them): every 15 s a frame over a 100 Hz budget. A job
        /// builds one section per network tick instead, and sends (or compares) at the end.
        /// </summary>
        private sealed class HostJob
        {
            public int Seq;
            public List<int> Targets;
            public readonly Dictionary<byte, string> Global = new Dictionary<byte, string>();
            public int Section;            // next section index while building the world-wide ones
            public int Target;             // next target once those are done
            public DesyncDigestMessage Msg;  // the target's digest being filled
            public bool HasMsg;
            public Ctx TargetCtx;
            public double Ms;              // build time summed over its steps
        }

        private static HostJob _hostJob;     // reset-in: Reset

        internal static void Tick(LanNetworkManager net)
        {
            if (net == null || !Enabled)
                return;
            if (net.Role == NetworkRole.Client)
            {
                StepClient(net);
                return;
            }
            if (net.Role != NetworkRole.Host)
                return;
            if (_hostJob != null)
            {
                StepHost(net);
                return;
            }
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
            _hostJob = new HostJob { Seq = _seq, Targets = targets };
            StepHost(net);
        }

        /// <summary>Host: one section of the check in progress (world-wide first, then each target's).</summary>
        private static void StepHost(LanNetworkManager net)
        {
            HostJob job = _hostJob;
            if (!net.DesyncLinkQuiet || !LocalWorldSettled())
            {
                _hostJob = null; // a transition started: this check is void, the next one starts over
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int n = Sections.Length;
            // World sections once; the ones around a player per target.
            while (job.Section < n && Sections[job.Section].Focus)
                job.Section++;
            if (job.Section < n)
            {
                Section s = Sections[job.Section++];
                job.Global[s.Id] = Build(s, HostCtx(net, 0));
                job.Ms += watch.Elapsed.TotalMilliseconds;
                return;
            }
            if (job.Target >= job.Targets.Count)
            {
                Finish();
                return;
            }
            int id = job.Targets[job.Target];
            if (net.GetProxy(id) == null)
            {
                job.Target++;
                job.HasMsg = false;
                job.Ms += watch.Elapsed.TotalMilliseconds;
                return;
            }
            if (!job.HasMsg)
            {
                job.HasMsg = true;
                job.TargetCtx = HostCtx(net, id);
                job.Msg = new DesyncDigestMessage
                {
                    Seq = job.Seq,
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
                    Section g = Sections[i];
                    job.Msg.SectionIds[i] = g.Id;
                    job.Msg.Inline[i] = null;
                    if (g.Focus)
                        continue;
                    string text = job.Global[g.Id];
                    job.Msg.Hashes[i] = DesyncEntries.Hash(text);
                    job.Msg.Counts[i] = DesyncEntries.Count(text);
                    job.Msg.Inline[i] = string.Empty;
                }
            }
            // The next focus section of this target not built yet.
            int next = -1;
            for (int i = 0; i < n; i++)
            {
                if (Sections[i].Focus && job.Msg.Inline[i] == null)
                {
                    next = i;
                    break;
                }
            }
            if (next >= 0)
            {
                Section s = Sections[next];
                string text = Build(s, job.TargetCtx);
                job.Msg.Hashes[next] = DesyncEntries.Hash(text);
                job.Msg.Counts[next] = DesyncEntries.Count(text);
                job.Msg.Inline[next] = Cap(text, out _);
                job.Ms += watch.Elapsed.TotalMilliseconds;
                return;
            }
            net.SendDesyncDigest(id, job.Msg);
            job.HasMsg = false;
            job.Target++;
            job.Ms += watch.Elapsed.TotalMilliseconds;
            if (job.Target >= job.Targets.Count)
                Finish();

            void Finish()
            {
                NoteCost("host digest seq " + job.Seq + " for " + job.Targets.Count + " peer(s)", job.Ms);
                _hostJob = null;
            }
        }

        /// <summary>A check is a frame's work: log one that costs a visible hitch.</summary>
        private static void NoteCost(string what, System.Diagnostics.Stopwatch watch)
            => NoteCost(what, watch.Elapsed.TotalMilliseconds);

        /// <summary>A check's build time (summed over its steps when it is spread over frames).</summary>
        private static void NoteCost(string what, double total)
        {
            float ms = (float)total;
            if (ms > _worstMs)
                _worstMs = ms;
            if (ms > SlowCheckMs)
                ModLog.WarnRate(LogCat.Session, "desync-slow", "[Desync] slow check: " + what + " took "
                    + ms.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms", 60f);
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

        private sealed class ClientJob
        {
            public DesyncDigestMessage Msg;
            public Ctx Ctx;
            public int Index;
            public readonly List<string> Lines = new List<string>();
            public readonly List<byte> Request = new List<byte>();
            public double Ms;
        }

        private static ClientJob _clientJob; // reset-in: Reset

        internal static void ClientOnDigest(LanNetworkManager net, DesyncDigestMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client || !Enabled || msg.SectionIds == null)
                return;
            if (!net.DesyncLinkQuiet || !LocalWorldSettled())
                return;
            // A digest that arrives while the last one is still being compared replaces it.
            _clientChecks++;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var job = new ClientJob { Msg = msg, Ctx = ClientCtx(net) };

            var clock = new List<DesyncEntries.Diff>(2);
            int chapter = Chapter();
            if (chapter != msg.Chapter)
                clock.Add(new DesyncEntries.Diff { Key = "chapter", Host = msg.Chapter.ToString(), Client = chapter.ToString() });
            int total = Singleton<Controller>.Instance.totalTime;
            if (Math.Abs(total - msg.TotalTime) > ClockToleranceMinutes)
                clock.Add(new DesyncEntries.Diff { Key = "time", Host = Clock(msg.TotalTime), Client = Clock(total) });
            job.Lines.AddRange(_ledger.Evaluate(ClockSection, "Clock", clock));
            job.Ms = watch.Elapsed.TotalMilliseconds;
            _clientJob = job;
        }

        /// <summary>Client: compare one section of the digest in progress; report at the end.</summary>
        private static void StepClient(LanNetworkManager net)
        {
            ClientJob job = _clientJob;
            if (job == null)
                return;
            if (!net.DesyncLinkQuiet || !LocalWorldSettled())
            {
                _clientJob = null;
                return;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            DesyncDigestMessage msg = job.Msg;
            Section s = null;
            int i = job.Index;
            while (i < msg.SectionIds.Length && (s = Find(msg.SectionIds[i])) == null)
                i++;
            if (s != null)
            {
                job.Index = i + 1;
                // Where the player stands now: the host built its copy around this player's
                // stand-in a moment ago, and the focus sections carry an edge margin.
                job.Ctx.FocusPos = Player.Instance != null ? Player.Instance.transform.position : job.Ctx.FocusPos;
                string mine = Build(s, job.Ctx);
                if (s.Focus)
                {
                    job.Lines.AddRange(_ledger.Evaluate(s.Id, s.Name, DesyncEntries.Compare(
                        DesyncEntries.Parse(msg.Inline != null && i < msg.Inline.Length ? msg.Inline[i] : null),
                        DesyncEntries.Parse(mine), s.Same)));
                }
                else if (DesyncEntries.Hash(mine) == (msg.Hashes != null && i < msg.Hashes.Length ? msg.Hashes[i] : 0u))
                {
                    job.Lines.AddRange(_ledger.Evaluate(s.Id, s.Name, null));
                }
                else
                {
                    job.Request.Add(s.Id);
                }
                job.Ms += watch.Elapsed.TotalMilliseconds;
                return;
            }
            _clientJob = null;
            if (job.Request.Count > 0)
            {
                net.SendDesyncDetailRequest(new DesyncDetailRequestMessage
                {
                    Seq = msg.Seq,
                    SectionCount = (byte)job.Request.Count,
                    SectionIds = job.Request.ToArray()
                });
            }
            NoteCost("client compare seq " + msg.Seq, job.Ms);
            Emit(net, msg.Seq, job.Lines);

            float now = Time.unscaledTime;
            if (now >= _clientOkLogAt)
            {
                _clientOkLogAt = now + OkLogEverySec;
                ModLog.Event(LogCat.Session, "[Desync] check #" + _clientChecks + " seq " + msg.Seq
                    + ": " + _ledger.ReportedCount + " open desync(s)"
                    + (job.Request.Count > 0 ? ", " + job.Request.Count + " section(s) being compared" : "")
                    + ", slowest check " + _worstMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
                _worstMs = 0f;
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
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string mine = Build(s, ClientCtx(net));
            List<string> lines = _ledger.Evaluate(s.Id, s.Name, DesyncEntries.Compare(
                DesyncEntries.Parse(msg.Entries), DesyncEntries.Parse(mine), s.Same, msg.Truncated));
            NoteCost("client detail " + s.Name, watch);
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
