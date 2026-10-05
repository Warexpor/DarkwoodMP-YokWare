namespace DWMPHorde
{
    /// <summary>
    /// Client estimate of the host clock from <c>EntityState.HostTime</c> stamps. The offset
    /// follows the least-delayed packet (largest host-minus-local value), so the estimate is
    /// "host now minus the best-case latency". Slower packets pull it down only slowly, which
    /// absorbs jitter; a jump of more than <see cref="ResyncThreshold"/> (new host after a
    /// migration, a long stall) re-seeds it.
    /// </summary>
    public sealed class HostClockEstimator
    {
        /// <summary>Share of the gap a slower packet closes (clock drift, route change).</summary>
        public const float DecayPerSample = 0.02f;
        public const float ResyncThreshold = 1f;

        private bool _has;
        private float _offset;

        public bool HasEstimate => _has;
        public float Offset => _offset;

        public void Reset()
        {
            _has = false;
            _offset = 0f;
        }

        public void AddSample(float hostTime, float localTime)
        {
            float o = hostTime - localTime;
            if (!_has || o - _offset > ResyncThreshold || _offset - o > ResyncThreshold)
            {
                _offset = o;
                _has = true;
                return;
            }
            if (o > _offset)
                _offset = o;
            else
                _offset += (o - _offset) * DecayPerSample;
        }

        public float ToHost(float localTime) => localTime + _offset;
    }

    public struct TimelineSample
    {
        public float T;
        public float X, Y, Z;
        public float RotY;
        /// <summary>The host's body clip at <see cref="T"/> (false for a sample made on the client).</summary>
        public bool HasClip;
        public string Clip;
        public short ClipFrame;
    }

    /// <summary>
    /// Host-time-stamped pose history for one entity. The client renders it a fixed delay
    /// behind the estimated host clock: linear between samples, a short capped coast past the
    /// newest one, then a hold. No Unity types, so it is unit-tested.
    /// </summary>
    public sealed class EntityTimeline
    {
        public const int Capacity = 8;

        private readonly TimelineSample[] _s = new TimelineSample[Capacity];
        private int _count;
        /// <summary>Host time of the newest clip shown (from a sample, or played on arrival).</summary>
        private float _clipShownT = float.NegativeInfinity;

        public int Count => _count;
        public TimelineSample Newest => _s[_count - 1];

        public void Clear()
        {
            _count = 0;
            _clipShownT = float.NegativeInfinity;
        }

        /// <summary>
        /// The clip to show at host time <paramref name="t"/>: the newest sample with a clip at or
        /// before <paramref name="t"/>, once. False when that sample (or a newer clip played on
        /// arrival, see <see cref="HoldClipsThrough"/>) was already shown, so the body's clip
        /// changes when its pose reaches the host moment of the change.
        /// </summary>
        public bool TakeClip(float t, out TimelineSample clip)
        {
            clip = default;
            for (int i = _count - 1; i >= 0; i--)
            {
                TimelineSample x = _s[i];
                if (x.T > t)
                    continue;
                if (x.T <= _clipShownT)
                    return false;
                if (!x.HasClip)
                    continue;
                _clipShownT = x.T;
                clip = x;
                return true;
            }
            return false;
        }

        /// <summary>
        /// A clip stamped <paramref name="t"/> was played as soon as it arrived (attack, hit,
        /// death): samples up to that host time no longer change the clip.
        /// </summary>
        public void HoldClipsThrough(float t)
        {
            if (t > _clipShownT)
                _clipShownT = t;
        }

        /// <summary>
        /// Appends a sample. Out-of-order or same-time samples are dropped (false). The host
        /// skips unchanged bodies, so a resting enemy can go quiet for seconds; when the next
        /// sample is more than two <paramref name="sendInterval"/>s newer, the old spot is
        /// held until one interval before it, so the move does not start early.
        /// </summary>
        public bool Add(TimelineSample x, float sendInterval)
        {
            if (_count > 0)
            {
                TimelineSample last = _s[_count - 1];
                if (x.T <= last.T)
                    return false;
                if (sendInterval > 0f && x.T - last.T > sendInterval * 2f)
                {
                    TimelineSample hold = last;
                    hold.T = x.T - sendInterval;
                    hold.HasClip = false; // a pose hold, not a clip the host sent again
                    Push(hold);
                }
            }
            Push(x);
            return true;
        }

        private void Push(TimelineSample x)
        {
            if (_count == Capacity)
            {
                for (int i = 1; i < Capacity; i++)
                    _s[i - 1] = _s[i];
                _count--;
            }
            _s[_count++] = x;
        }

        /// <summary>
        /// Pose at host time <paramref name="t"/>. Before the oldest sample: the oldest pose.
        /// Past the newest: coast along the last segment for at most
        /// <paramref name="maxExtrapolate"/> seconds, then hold. False when empty.
        /// </summary>
        public bool Sample(float t, float maxExtrapolate, out TimelineSample pose)
        {
            pose = default;
            if (_count == 0)
                return false;

            TimelineSample first = _s[0];
            if (_count == 1 || t <= first.T)
            {
                pose = first;
                pose.T = t;
                return true;
            }

            TimelineSample newest = _s[_count - 1];
            if (t >= newest.T)
            {
                TimelineSample prev = _s[_count - 2];
                float seg = newest.T - prev.T;
                float extra = t - newest.T;
                if (extra > maxExtrapolate) extra = maxExtrapolate;
                if (extra < 0f) extra = 0f;
                pose = newest;
                if (seg > 0f && extra > 0f)
                {
                    float k = extra / seg;
                    pose.X += (newest.X - prev.X) * k;
                    pose.Y += (newest.Y - prev.Y) * k;
                    pose.Z += (newest.Z - prev.Z) * k;
                }
                pose.T = t;
                return true;
            }

            for (int i = _count - 1; i > 0; i--)
            {
                TimelineSample a = _s[i - 1];
                TimelineSample b = _s[i];
                if (t < a.T)
                    continue;
                float span = b.T - a.T;
                float u = span > 0f ? (t - a.T) / span : 1f;
                pose.T = t;
                pose.X = a.X + (b.X - a.X) * u;
                pose.Y = a.Y + (b.Y - a.Y) * u;
                pose.Z = a.Z + (b.Z - a.Z) * u;
                pose.RotY = LerpAngle(a.RotY, b.RotY, u);
                return true;
            }

            pose = first;
            pose.T = t;
            return true;
        }

        /// <summary>Degrees, shortest way round (same as Unity's <c>Mathf.LerpAngle</c>).</summary>
        public static float LerpAngle(float a, float b, float u)
        {
            float d = (b - a) % 360f;
            if (d > 180f) d -= 360f;
            else if (d < -180f) d += 360f;
            return a + d * u;
        }
    }
}
