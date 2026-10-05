using System.Text;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Timeline health for the playtest log: how often a moving creature is drawn between two
    /// host samples (good), coasting past the newest one, or holding for want of one; how the
    /// samples arrive (gap between a body's samples, batch lateness) and the render delay that
    /// buys. One [EntTimeline] line every <see cref="StatsInterval"/> s with the perf probe or
    /// entity tracing on.
    /// </summary>
    public static partial class ClientEntityInterpolationService
    {
        private const float StatsInterval = 5f;
        private static float _statsAt; // reset-in: ResetTimelineStats
        private static int _statFrames, _statInterp, _statCoast, _statHold, _statBefore; // reset-in: ResetTimelineStats
        private static int _statBatches; // reset-in: ResetTimelineStats
        private static float _statLateSum, _statLateMax; // reset-in: ResetTimelineStats
        private static int _statGaps; // reset-in: ResetTimelineStats
        private static float _statGapSum, _statGapMax; // reset-in: ResetTimelineStats
        private static float _statDelaySum; // reset-in: ResetTimelineStats

        private static bool StatsOn => EntitySyncLog.On || CoopPerfProbe.IsActive;

        private static void ResetTimelineStats()
        {
            _statsAt = 0f;
            _statFrames = _statInterp = _statCoast = _statHold = _statBefore = 0;
            _statBatches = 0;
            _statLateSum = _statLateMax = 0f;
            _statGaps = 0;
            _statGapSum = _statGapMax = 0f;
            _statDelaySum = 0f;
        }

        private static void NoteBatchLateness(float lateness)
        {
            if (!StatsOn) return;
            _statBatches++;
            _statLateSum += lateness;
            if (lateness > _statLateMax)
                _statLateMax = lateness;
        }

        private static void NoteSampleGap(float gap)
        {
            if (!StatsOn) return;
            _statGaps++;
            _statGapSum += gap;
            if (gap > _statGapMax)
                _statGapMax = gap;
        }

        /// <summary>A frame of a body whose samples are arriving (a resting body's hold is not counted).</summary>
        private static void NoteFrameKind(EntityInterpState state, TimelinePoseKind kind)
        {
            if (!StatsOn) return;
            _statFrames++;
            _statDelaySum += state.delay;
            state.statFrames++;
            switch (kind)
            {
                case TimelinePoseKind.Interpolated: _statInterp++; break;
                case TimelinePoseKind.Coasting: _statCoast++; state.statCoast++; break;
                case TimelinePoseKind.Holding: _statHold++; state.statHold++; break;
                case TimelinePoseKind.BeforeOldest: _statBefore++; break;
            }
        }

        private static void MaybeReportTimelineStats(float now)
        {
            if (!StatsOn)
                return;
            if (_statsAt <= 0f)
            {
                _statsAt = now + StatsInterval;
                return;
            }
            if (now < _statsAt)
                return;
            _statsAt = now + StatsInterval;

            // The body that held most (at least a second of frames), to name in the line.
            short worstId = 0;
            float worstHold = 0f;
            int bodies = 0;
            foreach (var kv in _states)
            {
                EntityInterpState st = kv.Value;
                if (st.statFrames > 0)
                    bodies++;
                if (st.statFrames >= 60)
                {
                    float h = (st.statHold + st.statCoast) / (float)st.statFrames;
                    if (h > worstHold)
                    {
                        worstHold = h;
                        worstId = kv.Key;
                    }
                }
                st.statFrames = st.statCoast = st.statHold = 0;
            }

            if (_statFrames > 0 || _statBatches > 0)
            {
                float f = Mathf.Max(_statFrames, 1);
                var sb = new StringBuilder(256);
                sb.Append("[EntTimeline] bodies=").Append(bodies)
                    .Append(" frames=").Append(_statFrames)
                    .Append(" interp%=").Append((_statInterp * 100f / f).ToString("F1"))
                    .Append(" coast%=").Append((_statCoast * 100f / f).ToString("F1"))
                    .Append(" hold%=").Append((_statHold * 100f / f).ToString("F1"))
                    .Append(" before%=").Append((_statBefore * 100f / f).ToString("F1"))
                    .Append(" | delayMs avg=").Append((_statDelaySum / f * 1000f).ToString("F0"))
                    .Append(" | gapMs n=").Append(_statGaps)
                    .Append(" avg=").Append((_statGaps > 0 ? _statGapSum / _statGaps * 1000f : 0f).ToString("F0"))
                    .Append(" max=").Append((_statGapMax * 1000f).ToString("F0"))
                    .Append(" | lateMs n=").Append(_statBatches)
                    .Append(" avg=").Append((_statBatches > 0 ? _statLateSum / _statBatches * 1000f : 0f).ToString("F1"))
                    .Append(" max=").Append((_statLateMax * 1000f).ToString("F1"))
                    .Append(" jitter mean=").Append((_jitter.Mean * 1000f).ToString("F1"))
                    .Append(" dev=").Append((_jitter.Dev * 1000f).ToString("F1"))
                    .Append(" margin=").Append((_jitter.Margin * 1000f).ToString("F1"))
                    .Append(" | clock off=").Append(_hostClock.Offset.ToString("F3"))
                    .Append(" target=").Append(_hostClock.Target.ToString("F3"));
                if (worstId != 0)
                {
                    sb.Append(" | worst id=").Append(worstId)
                        .Append(" coast+hold%=").Append((worstHold * 100f).ToString("F1"));
                    if (_states.TryGetValue(worstId, out EntityInterpState w))
                        sb.Append(" intervalMs=").Append((w.interval * 1000f).ToString("F0"))
                            .Append(" delayMs=").Append((w.delay * 1000f).ToString("F0"));
                }
                ModLog.Event(LogCat.Entity, sb.ToString());
            }

            float keepAt = _statsAt;
            ResetTimelineStats();
            _statsAt = keepAt;
        }
    }
}
