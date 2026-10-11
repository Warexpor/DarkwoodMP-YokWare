using System;
using System.Diagnostics;
using System.Text;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Co-op frame cost probe for Host and Client. Emits a periodic Event line while
    /// hosting (peers or none) or connected, so dual-box FPS bugs show in LogOutput without
    /// Trace spam. Times are milliseconds accumulated between reports.
    /// <c>over=N/budget</c> counts the frames over one and a half times the frame budget
    /// (refresh interval with vsync, else the fps cap); <c>gc=N</c> the garbage collections in
    /// the window; <c>worst[...]</c> says what the longest frame held: the mod's network tick
    /// time, the log lines written, and whether a collection ran in it.
    /// Only runs under LogPreset Dev/Trace or Debug.PerfProbe; otherwise every entry point
    /// is a single bool check (SetActive refuses to arm it).
    /// </summary>
    public static partial class CoopPerfProbe
    {
        private static readonly Stopwatch Sw = new Stopwatch();
        private static readonly Stopwatch FootSw = new Stopwatch();
        private static readonly Stopwatch SegSw = new Stopwatch();

        private static bool _active;
        private static string _roleTag = "?";
        private static int _frames;
        private static float _dtSum;
        private static float _dtMax;
        private static float _reportAt;

        /// <summary>Frames over the budget this window, and the budget they were held to (ms).</summary>
        private static int _lateFrames;
        private static float _budgetMs;
        private static int _gcAtWindowStart;
        /// <summary>The frame in progress: network tick time, log lines, collections at its start.</summary>
        private static double _frameNetMs;
        private static int _frameLogLines;
        private static int _gcAtFrameStart;
        /// <summary>The same three for the longest frame of the window.</summary>
        private static double _worstNetMs;
        private static int _worstLogLines;
        private static int _worstGc;
        /// <summary>The longest frame split (<see cref="PerfFramePhases"/>): scripts + render CPU, present wait, physics steps.</summary>
        private static double _worstWorkMs;
        private static double _worstWaitMs;
        private static int _worstFixed;
        /// <summary>Per-frame averages of the same split over the window (how close frames run to the budget).</summary>
        private static double _sumWorkMs, _sumPhysMs, _sumUpdMs, _sumCoMs, _sumLateMs, _sumRendMs;
        private static int _overWorkFrames;
        private static double _worstPhysMs, _worstUpdMs, _worstCoMs, _worstLateMs, _worstRendMs;

        private static double _pollMs;
        private static double _updateRestMs;
        private static double _physBuildMs;
        private static double _lateMs;
        private static double _objInterpMs;
        private static double _entityTickMs;
        private static double _entityApplyMs;
        private static double _physApplyMs;

        private static int _entitySnaps;
        private static int _entityApplied;
        private static int _entitySkipped;
        private static int _physSnaps;
        private static int _physObjs;
        private static int _packetsRx;
        private static int _fullRbScans;
        private static int _findObjTypeCalls;
        private static double _footMs;
        private static string _lastFootType = "";

        private static readonly int[] _pktByType = new int[256];

        private static int _pendLure, _pendLock, _pendLight, _pendTrap, _pendFeeder, _pendSaw, _pendConstruct;

        private static int _hostEntSendSnaps;
        private static int _hostEntSendCount;
        /// <summary>Host snapshot ticks this window and the real time between them (target 50 ms).</summary>
        private static int _hostEntTicks;
        private static float _hostEntIntervalSum;
        private static float _hostEntIntervalMax;

        private static string _segName;
        private static string _lastSegName = "";
        private static double _segMaxMs;
        private static string _segMaxName = "";
        private static double _frameSegMaxMs;
        private static string _frameSegMaxName = "";

        private const float ReportInterval = 2f;
        /// <summary>Log a one-shot Event when an Update sub-segment exceeds this (ms).</summary>
        private const double SegSpikeMs = 25.0;
        /// <summary>Single-frame dt that feels like a 0-FPS hitch — emit [PerfCliff] immediately.</summary>
        private const float FrameCliffMs = 100f;
        /// <summary>Window average below this FPS → [PerfCliff] on the 2s report.</summary>
        private const float ReportCliffFps = 15f;

        private static float _lastCliffLogTime = -999f;

        public static bool IsActive => _active;

        /// <summary>Probe allowed by config: Dev/Trace preset or the PerfProbe flag.</summary>
        public static bool IsEnabledByConfig
        {
            get
            {
                LogPreset preset = ModLog.CurrentPreset;
                return preset == LogPreset.Dev || preset == LogPreset.Trace
                    || Config.ModConfig.IsPerfProbe;
            }
        }

        public static void SetActive(bool active, NetworkRole role = NetworkRole.Offline)
        {
            if (active && !IsEnabledByConfig)
                active = false;
            if (!active && !_active)
                return; // already idle: nothing to reset or log
            string tag = role == NetworkRole.Host ? "Host"
                : role == NetworkRole.Client ? "Client" : "?";
            if (_active == active && _roleTag == tag) return;
            _active = active;
            _roleTag = tag;
            ResetAccumulators();
            if (active)
            {
                PerfFramePhases.Ensure();
                _reportAt = Time.unscaledTime + ReportInterval;
                ModLog.Event(LogCat.Core,
                    "[Perf] probe ON role=" + _roleTag
                    + " every " + ReportInterval
                    + "s; [PerfCliff] if frame≥" + FrameCliffMs.ToString("F0")
                    + "ms or fps~<" + ReportCliffFps.ToString("F0")
                    + " (poll/upd/top/foot/pend)");
            }
            else
            {
                ModLog.Event(LogCat.Core, "[Perf] probe OFF");
            }
        }

        public static void ResetAccumulators()
        {
            _frames = 0;
            _dtSum = 0f;
            _dtMax = 0f;
            _lateFrames = 0;
            _gcAtWindowStart = _gcAtFrameStart = GC.CollectionCount(0);
            _frameNetMs = _worstNetMs = 0;
            _frameLogLines = _worstLogLines = 0;
            _worstGc = 0;
            _worstWorkMs = _worstWaitMs = 0;
            _worstFixed = 0;
            _sumWorkMs = _sumPhysMs = _sumUpdMs = _sumCoMs = _sumLateMs = _sumRendMs = 0;
            _overWorkFrames = 0;
            _worstPhysMs = _worstUpdMs = _worstCoMs = _worstLateMs = _worstRendMs = 0;
            _pollMs = _updateRestMs = _physBuildMs = 0;
            _lateMs = _objInterpMs = _entityTickMs = 0;
            _entityApplyMs = _physApplyMs = 0;
            _entitySnaps = _entityApplied = _entitySkipped = 0;
            _physSnaps = _physObjs = _packetsRx = 0;
            _fullRbScans = _findObjTypeCalls = 0;
            _footMs = 0;
            _lastFootType = "";
            Array.Clear(_pktByType, 0, _pktByType.Length);
            _pendLure = _pendLock = _pendLight = _pendTrap = _pendFeeder = _pendSaw = _pendConstruct = 0;
            _hostEntSendSnaps = _hostEntSendCount = 0;
            _hostEntTicks = 0;
            _hostEntIntervalSum = _hostEntIntervalMax = 0f;
            _segName = null;
            _lastSegName = "";
            _segMaxMs = 0;
            _segMaxName = "";
            _frameSegMaxMs = 0;
            _frameSegMaxName = "";
            Sw.Reset();
        }

        /// <summary>
        /// Begin a named Update sub-segment (after MarkPoll). End with
        /// <see cref="EndUpdateSegment"/>. Uses a separate stopwatch — does not
        /// disturb poll/upd accounting. Spikes ≥ <see cref="SegSpikeMs"/> emit Event.
        /// Keep segments contiguous so a main-thread stall always lands in one name.
        /// </summary>
        public static void BeginUpdateSegment(string name)
        {
            if (!_active || string.IsNullOrEmpty(name)) return;
            // Nested/overlapping Begin without End — close prior so gap isn't lost.
            if (!string.IsNullOrEmpty(_segName))
                EndUpdateSegment();
            _segName = name;
            SegSw.Restart();
        }

        public static void EndUpdateSegment()
        {
            if (!_active || string.IsNullOrEmpty(_segName)) return;
            SegSw.Stop();
            double ms = SegSw.Elapsed.TotalMilliseconds;
            _lastSegName = _segName;
            if (ms > _segMaxMs)
            {
                _segMaxMs = ms;
                _segMaxName = _segName;
            }
            if (ms > _frameSegMaxMs)
            {
                _frameSegMaxMs = ms;
                _frameSegMaxName = _segName;
            }
            if (ms >= SegSpikeMs)
            {
                ModLog.Event(LogCat.Core,
                    "[PerfSeg] role=" + _roleTag + " " + _segName + "=" + ms.ToString("F1") + "ms");
            }
            _segName = null;
        }

        public static void FrameBegin()
        {
            if (!_active) return;
            float dt = Time.unscaledDeltaTime * 1000f;
            _frames++;
            _dtSum += dt;
            // dt is the frame that just ended: what was counted since the last FrameBegin is its.
            int gcNow = GC.CollectionCount(0);
            if (dt > _dtMax)
            {
                _dtMax = dt;
                _worstNetMs = _frameNetMs;
                _worstLogLines = _frameLogLines;
                _worstGc = gcNow - _gcAtFrameStart;
                _worstWorkMs = PerfFramePhases.LastWorkMs;
                _worstWaitMs = PerfFramePhases.LastWaitMs;
                _worstFixed = PerfFramePhases.LastFixedSteps;
                _worstPhysMs = PerfFramePhases.LastPhysMs;
                _worstUpdMs = PerfFramePhases.LastUpdMs;
                _worstCoMs = PerfFramePhases.LastCoMs;
                _worstLateMs = PerfFramePhases.LastLateMs;
                _worstRendMs = PerfFramePhases.LastRendMs;
            }
            _sumWorkMs += PerfFramePhases.LastWorkMs;
            _sumPhysMs += PerfFramePhases.LastPhysMs;
            _sumUpdMs += PerfFramePhases.LastUpdMs;
            _sumCoMs += PerfFramePhases.LastCoMs;
            _sumLateMs += PerfFramePhases.LastLateMs;
            _sumRendMs += PerfFramePhases.LastRendMs;
            if (_budgetMs > 0f && PerfFramePhases.LastWorkMs > _budgetMs)
                _overWorkFrames++;
            _frameNetMs = 0;
            _frameLogLines = 0;
            _gcAtFrameStart = gcNow;
            _budgetMs = FrameBudgetMs();
            if (_budgetMs > 0f && dt > _budgetMs * 1.5f)
                _lateFrames++;
            if (dt >= FrameCliffMs)
            {
                float now = Time.unscaledTime;
                // Rate-limit cliff spam during sustained hitches (still every ~0.5s).
                if (now - _lastCliffLogTime >= 0.5f)
                {
                    _lastCliffLogTime = now;
                    ModLog.Event(LogCat.Core,
                        "[PerfCliff] role=" + _roleTag
                        + " frameMs=" + dt.ToString("F0")
                        + " (single-frame hitch ≥" + FrameCliffMs.ToString("F0") + "ms)");
                }
            }
            Sw.Restart();
        }

        /// <summary>The frame time the game is held to: the refresh interval with vsync, else the fps cap (0 = uncapped).</summary>
        private static float FrameBudgetMs()
        {
            int vsync = QualitySettings.vSyncCount;
            if (vsync > 0)
            {
                int refresh = Screen.currentResolution.refreshRate;
                return refresh > 0 ? 1000f * vsync / refresh : 0f;
            }
            int target = Application.targetFrameRate;
            return target > 0 ? 1000f / target : 0f;
        }

        /// <summary>Stopwatch time since the last mark, counted into the frame's network tick total.</summary>
        private static double Lap()
        {
            double ms = Sw.Elapsed.TotalMilliseconds;
            _frameNetMs += ms;
            return ms;
        }

        /// <summary>A log line was written (ModLog), for the longest frame's account.</summary>
        public static void NoteLogLine()
        {
            if (_active) _frameLogLines++;
        }

        public static void MarkPoll()
        {
            if (!_active) return;
            _pollMs += Lap();
            _frameSegMaxMs = 0;
            _frameSegMaxName = "";
            Sw.Restart();
        }

        public static void MarkUpdateRest()
        {
            if (!_active) return;
            // Close any open segment so its time is attributed before we stop upd.
            if (!string.IsNullOrEmpty(_segName))
                EndUpdateSegment();
            double frameMs = Lap();
            _updateRestMs += frameMs;
            if (frameMs >= SegSpikeMs)
            {
                string hot = !string.IsNullOrEmpty(_frameSegMaxName)
                    ? _frameSegMaxName + ":" + _frameSegMaxMs.ToString("F1")
                    : "none";
                ModLog.Event(LogCat.Core,
                    "[PerfSeg] role=" + _roleTag + " updFrame=" + frameMs.ToString("F1")
                    + "ms hotSeg=" + hot);
            }
            Sw.Restart();
        }

        public static void MarkPhysBuild()
        {
            if (!_active) return;
            _physBuildMs += Lap();
            Sw.Restart();
        }

        public static void LateBegin()
        {
            if (!_active) return;
            Sw.Restart();
        }

        public static void MarkObjInterp()
        {
            if (!_active) return;
            _objInterpMs += Lap();
            Sw.Restart();
        }

        public static void MarkEntityTick()
        {
            if (!_active) return;
            _entityTickMs += Lap();
            Sw.Restart();
        }

        public static void LateEnd()
        {
            if (!_active) return;
            _lateMs += Lap();
            Sw.Restart();
            MaybeReport();
        }

        /// <summary>The 2 s report on a frame with no network tick (a host with no peers).</summary>
        public static void ReportIfDue()
        {
            if (_active) MaybeReport();
        }

        public static void NoteEntityApply(int applied, int skipped, double ms)
        {
            if (!_active) return;
            _entitySnaps++;
            _entityApplied += applied;
            _entitySkipped += skipped;
            _entityApplyMs += ms;
        }

        public static void NotePhysApply(int objects, double ms)
        {
            if (!_active) return;
            _physSnaps++;
            _physObjs += objects;
            _physApplyMs += ms;
        }

        public static void NotePacketRx()
        {
            if (!_active) return;
            _packetsRx++;
        }

        public static void NotePacketRx(NetMessageType type)
        {
            if (!_active) return;
            _packetsRx++;
            int i = (byte)type;
            if (i >= 0 && i < _pktByType.Length)
                _pktByType[i]++;
        }

        public static void NoteFullRbScan()
        {
            if (!_active) return;
            _fullRbScans++;
        }

        public static void NoteFindObjectsOfType()
        {
            if (!_active) return;
            _findObjTypeCalls++;
        }

        public static void NoteFindObjectsOfType(string typeName, double ms)
        {
            if (!_active) return;
            _findObjTypeCalls++;
            _footMs += ms;
            if (!string.IsNullOrEmpty(typeName))
                _lastFootType = typeName;
        }

        /// <summary>Call around FOOT; returns elapsed ms and records it.</summary>
        public static double BeginFootTiming()
        {
            if (!_active) return 0;
            FootSw.Restart();
            return 0;
        }

        public static void EndFootTiming(string typeName)
        {
            if (!_active) return;
            FootSw.Stop();
            NoteFindObjectsOfType(typeName, FootSw.Elapsed.TotalMilliseconds);
        }

        public static void SetPendingCounts(
            int lure, int locks, int light, int trap, int feeder, int saw, int construct)
        {
            if (!_active) return;
            _pendLure = lure;
            _pendLock = locks;
            _pendLight = light;
            _pendTrap = trap;
            _pendFeeder = feeder;
            _pendSaw = saw;
            _pendConstruct = construct;
        }

        public static void NoteEntityBroadcast(int entityCount)
        {
            if (!_active) return;
            _hostEntSendSnaps++;
            _hostEntSendCount += entityCount;
        }

        /// <summary>Host: one snapshot tick ran, <paramref name="interval"/> seconds after the previous one.</summary>
        public static void NoteEntityTick(float interval)
        {
            if (!_active || interval <= 0f) return;
            _hostEntTicks++;
            _hostEntIntervalSum += interval;
            if (interval > _hostEntIntervalMax)
                _hostEntIntervalMax = interval;
        }

        private static void MaybeReport()
        {
            if (Time.unscaledTime < _reportAt) return;
            _reportAt = Time.unscaledTime + ReportInterval;

            if (_frames <= 0)
            {
                ResetAccumulators();
                return;
            }

            float avg = _dtSum / _frames;
            float fps = avg > 0.01f ? 1000f / avg : 0f;

            var sb = new StringBuilder(384);
            bool cliff = fps < ReportCliffFps || _dtMax >= FrameCliffMs;
            if (cliff)
                sb.Append("[PerfCliff] ");
            else
                sb.Append("[Perf] ");
            sb.Append("role=").Append(_roleTag);
            sb.Append(" frames=").Append(_frames);
            sb.Append(" fps~").Append(fps.ToString("F0"));
            sb.Append(" avgMs=").Append(avg.ToString("F1"));
            sb.Append(" maxMs=").Append(_dtMax.ToString("F1"));
            if (cliff)
                sb.Append(" CLIFF");
            sb.Append(" over=").Append(_lateFrames).Append('/').Append(_budgetMs.ToString("F1")).Append("ms");
            sb.Append(" gc=").Append(GC.CollectionCount(0) - _gcAtWindowStart);
            sb.Append(" worst[net=").Append(_worstNetMs.ToString("F1"));
            sb.Append(" log=").Append(_worstLogLines);
            sb.Append(" gc=").Append(_worstGc);
            sb.Append(" work=").Append(_worstWorkMs.ToString("F1"));
            sb.Append(" wait=").Append(_worstWaitMs.ToString("F1"));
            sb.Append(" fixed=").Append(_worstFixed);
            sb.Append(" (phys=").Append(_worstPhysMs.ToString("F1"));
            sb.Append(" upd=").Append(_worstUpdMs.ToString("F1"));
            sb.Append(" co=").Append(_worstCoMs.ToString("F1"));
            sb.Append(" late=").Append(_worstLateMs.ToString("F1"));
            sb.Append(" rend=").Append(_worstRendMs.ToString("F1")).Append(")]");
            sb.Append(" avg[work=").Append((_sumWorkMs / _frames).ToString("F2"));
            sb.Append(" phys=").Append((_sumPhysMs / _frames).ToString("F2"));
            sb.Append(" upd=").Append((_sumUpdMs / _frames).ToString("F2"));
            sb.Append(" co=").Append((_sumCoMs / _frames).ToString("F2"));
            sb.Append(" late=").Append((_sumLateMs / _frames).ToString("F2"));
            sb.Append(" rend=").Append((_sumRendMs / _frames).ToString("F2"));
            sb.Append(" workOver=").Append(_overWorkFrames).Append(']');
            sb.Append(" | poll=").Append(_pollMs.ToString("F1"));
            sb.Append(" upd=").Append(_updateRestMs.ToString("F1"));
            sb.Append(" physBuild=").Append(_physBuildMs.ToString("F1"));
            sb.Append(" late=").Append(_lateMs.ToString("F1"));
            sb.Append(" objInterp=").Append(_objInterpMs.ToString("F1"));
            sb.Append(" entTick=").Append(_entityTickMs.ToString("F1"));
            sb.Append(" | entApply=").Append(_entityApplyMs.ToString("F1"));
            sb.Append("ms snaps=").Append(_entitySnaps);
            sb.Append(" applied=").Append(_entityApplied);
            sb.Append(" skip=").Append(_entitySkipped);
            sb.Append(" | physApply=").Append(_physApplyMs.ToString("F1"));
            sb.Append("ms snaps=").Append(_physSnaps);
            sb.Append(" objs=").Append(_physObjs);
            sb.Append(" | pktRx=").Append(_packetsRx);
            AppendTopPacketTypes(sb, 5);
            sb.Append(" | footN=").Append(_findObjTypeCalls);
            sb.Append(" footMs=").Append(_footMs.ToString("F1"));
            if (_findObjTypeCalls > 0 && !string.IsNullOrEmpty(_lastFootType))
                sb.Append(" footType=").Append(_lastFootType);
            sb.Append(" fullRbScan=").Append(_fullRbScans);
            sb.Append(" | pend lure=").Append(_pendLure);
            sb.Append(" lock=").Append(_pendLock);
            sb.Append(" light=").Append(_pendLight);
            sb.Append(" trap=").Append(_pendTrap);
            sb.Append(" feeder=").Append(_pendFeeder);
            sb.Append(" saw=").Append(_pendSaw);
            sb.Append(" construct=").Append(_pendConstruct);
            if (_hostEntSendSnaps > 0)
            {
                sb.Append(" | hostEntSend snaps=").Append(_hostEntSendSnaps);
                sb.Append(" ents=").Append(_hostEntSendCount);
            }
            if (_hostEntTicks > 0)
            {
                sb.Append(" | hostEntTick ms avg=").Append((_hostEntIntervalSum / _hostEntTicks * 1000f).ToString("F1"));
                sb.Append(" max=").Append((_hostEntIntervalMax * 1000f).ToString("F1"));
            }
            if (_segMaxMs >= 1.0 && !string.IsNullOrEmpty(_segMaxName))
            {
                sb.Append(" | segMax=").Append(_segMaxName);
                sb.Append(':').Append(_segMaxMs.ToString("F1"));
            }

            ModLog.Event(LogCat.Core, sb.ToString());
            ResetAccumulators();
        }

        private static void AppendTopPacketTypes(StringBuilder sb, int max)
        {
            // Selection without heap: pick top N indices
            int[] topIdx = new int[max];
            int[] topVal = new int[max];
            for (int i = 0; i < max; i++)
            {
                topIdx[i] = -1;
                topVal[i] = 0;
            }

            for (int t = 0; t < _pktByType.Length; t++)
            {
                int v = _pktByType[t];
                if (v <= 0) continue;
                for (int k = 0; k < max; k++)
                {
                    if (v > topVal[k])
                    {
                        for (int m = max - 1; m > k; m--)
                        {
                            topVal[m] = topVal[m - 1];
                            topIdx[m] = topIdx[m - 1];
                        }
                        topVal[k] = v;
                        topIdx[k] = t;
                        break;
                    }
                }
            }

            if (topIdx[0] < 0) return;
            sb.Append(" top=");
            bool first = true;
            for (int k = 0; k < max; k++)
            {
                if (topIdx[k] < 0) break;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(ShortMsgName(topIdx[k]));
                sb.Append(':');
                sb.Append(topVal[k]);
            }
        }

        private static string ShortMsgName(int typeByte)
        {
            try
            {
                var t = (NetMessageType)typeByte;
                return t.ToString();
            }
            catch
            {
                return typeByte.ToString();
            }
        }
    }
}
