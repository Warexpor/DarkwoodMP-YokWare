using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Client-only co-op frame cost probe. Always emits a periodic Event line while
    /// Role=Client + connected so dual-box FPS bugs show up in LogOutput without Trace spam.
    /// Times are milliseconds accumulated between reports.
    /// </summary>
    public static class ClientPerfProbe
    {
        private static readonly Stopwatch Sw = new Stopwatch();

        private static bool _active;
        private static int _frames;
        private static float _dtSum;
        private static float _dtMax;
        private static float _reportAt;

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

        private const float ReportInterval = 2f;

        public static bool IsActive => _active;

        public static void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;
            ResetAccumulators();
            if (active)
            {
                _reportAt = Time.unscaledTime + ReportInterval;
                ModLog.Event(LogCat.Core,
                    "[Perf] client probe ON — report every " + ReportInterval
                    + "s while co-op connected (look for frameMs / phys / entity lines)");
            }
            else
            {
                ModLog.Event(LogCat.Core, "[Perf] client probe OFF");
            }
        }

        public static void ResetAccumulators()
        {
            _frames = 0;
            _dtSum = 0f;
            _dtMax = 0f;
            _pollMs = _updateRestMs = _physBuildMs = 0;
            _lateMs = _objInterpMs = _entityTickMs = 0;
            _entityApplyMs = _physApplyMs = 0;
            _entitySnaps = _entityApplied = _entitySkipped = 0;
            _physSnaps = _physObjs = _packetsRx = 0;
            _fullRbScans = _findObjTypeCalls = 0;
            Sw.Reset();
        }

        public static void FrameBegin()
        {
            if (!_active) return;
            float dt = Time.unscaledDeltaTime * 1000f;
            _frames++;
            _dtSum += dt;
            if (dt > _dtMax) _dtMax = dt;
            Sw.Restart();
        }

        public static void MarkPoll()
        {
            if (!_active) return;
            _pollMs += Sw.Elapsed.TotalMilliseconds;
            Sw.Restart();
        }

        public static void MarkUpdateRest()
        {
            if (!_active) return;
            _updateRestMs += Sw.Elapsed.TotalMilliseconds;
            Sw.Restart();
        }

        public static void MarkPhysBuild()
        {
            if (!_active) return;
            _physBuildMs += Sw.Elapsed.TotalMilliseconds;
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
            _objInterpMs += Sw.Elapsed.TotalMilliseconds;
            Sw.Restart();
        }

        public static void MarkEntityTick()
        {
            if (!_active) return;
            _entityTickMs += Sw.Elapsed.TotalMilliseconds;
            Sw.Restart();
        }

        public static void LateEnd()
        {
            if (!_active) return;
            _lateMs += Sw.Elapsed.TotalMilliseconds;
            Sw.Restart();
            MaybeReport();
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

            var sb = new StringBuilder(256);
            sb.Append("[Perf] frames=").Append(_frames);
            sb.Append(" fps~").Append(fps.ToString("F0"));
            sb.Append(" avgMs=").Append(avg.ToString("F1"));
            sb.Append(" maxMs=").Append(_dtMax.ToString("F1"));
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
            sb.Append(" fullRbScan=").Append(_fullRbScans);
            sb.Append(" findOfType=").Append(_findObjTypeCalls);

            // Always Event so Support/Dev/Public-with-Core can see it without Trace flood.
            ModLog.Event(LogCat.Core, sb.ToString());
            ResetAccumulators();
        }
    }
}
