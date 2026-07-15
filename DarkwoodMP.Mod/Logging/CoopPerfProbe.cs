using System;
using System.Diagnostics;
using System.Text;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Logging
{
    /// <summary>
    /// Co-op frame cost probe for Host and Client. Emits a periodic Event line while
    /// connected so dual-box FPS bugs show in LogOutput without Trace spam.
    /// Times are milliseconds accumulated between reports.
    /// </summary>
    public static class CoopPerfProbe
    {
        private static readonly Stopwatch Sw = new Stopwatch();
        private static readonly Stopwatch FootSw = new Stopwatch();

        private static bool _active;
        private static string _roleTag = "?";
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
        private static double _footMs;
        private static string _lastFootType = "";

        private static readonly int[] _pktByType = new int[256];

        private static int _pendLure, _pendLock, _pendLight, _pendTrap, _pendFeeder, _pendSaw, _pendConstruct;

        private static int _hostEntSendSnaps;
        private static int _hostEntSendCount;

        private const float ReportInterval = 2f;

        public static bool IsActive => _active;

        public static void SetActive(bool active, NetworkRole role = NetworkRole.Offline)
        {
            string tag = role == NetworkRole.Host ? "Host"
                : role == NetworkRole.Client ? "Client" : "?";
            if (_active == active && _roleTag == tag) return;
            _active = active;
            _roleTag = tag;
            ResetAccumulators();
            if (active)
            {
                _reportAt = Time.unscaledTime + ReportInterval;
                ModLog.Event(LogCat.Core,
                    "[Perf] probe ON role=" + _roleTag
                    + " every " + ReportInterval
                    + "s (poll/upd/top/foot/pend)");
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
            sb.Append("[Perf] role=").Append(_roleTag);
            sb.Append(" frames=").Append(_frames);
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

    /// <summary>Backward-compatible name for call sites / older docs.</summary>
    public static class ClientPerfProbe
    {
        public static bool IsActive => CoopPerfProbe.IsActive;
        public static void SetActive(bool active) => CoopPerfProbe.SetActive(active, NetworkRole.Client);
        public static void SetActive(bool active, NetworkRole role) => CoopPerfProbe.SetActive(active, role);
        public static void FrameBegin() => CoopPerfProbe.FrameBegin();
        public static void MarkPoll() => CoopPerfProbe.MarkPoll();
        public static void MarkUpdateRest() => CoopPerfProbe.MarkUpdateRest();
        public static void MarkPhysBuild() => CoopPerfProbe.MarkPhysBuild();
        public static void LateBegin() => CoopPerfProbe.LateBegin();
        public static void MarkObjInterp() => CoopPerfProbe.MarkObjInterp();
        public static void MarkEntityTick() => CoopPerfProbe.MarkEntityTick();
        public static void LateEnd() => CoopPerfProbe.LateEnd();
        public static void NoteEntityApply(int a, int s, double ms) => CoopPerfProbe.NoteEntityApply(a, s, ms);
        public static void NotePhysApply(int o, double ms) => CoopPerfProbe.NotePhysApply(o, ms);
        public static void NotePacketRx() => CoopPerfProbe.NotePacketRx();
        public static void NotePacketRx(NetMessageType type) => CoopPerfProbe.NotePacketRx(type);
        public static void NoteFullRbScan() => CoopPerfProbe.NoteFullRbScan();
        public static void NoteFindObjectsOfType() => CoopPerfProbe.NoteFindObjectsOfType();
        public static void NoteFindObjectsOfType(string typeName, double ms) =>
            CoopPerfProbe.NoteFindObjectsOfType(typeName, ms);
        public static void SetPendingCounts(int lure, int locks, int light, int trap, int feeder, int saw, int construct) =>
            CoopPerfProbe.SetPendingCounts(lure, locks, light, trap, feeder, saw, construct);
        public static void NoteEntityBroadcast(int entityCount) =>
            CoopPerfProbe.NoteEntityBroadcast(entityCount);
    }
}
