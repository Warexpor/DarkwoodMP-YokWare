using System;

namespace DWMPHorde
{
    /// <summary>
    /// Client estimate of the host clock from <c>EntityState.HostTime</c> stamps. The target
    /// offset is the least-delayed packet (largest host-minus-local value) of the last
    /// <see cref="WindowSec"/> seconds, so the estimate is "host now minus the best-case
    /// latency" and a route or drift change is followed once the window has moved on. The
    /// offset in use slews toward that target at <see cref="SlewPerSec"/> (playback runs at
    /// most a few percent fast or slow, never jumps); a jump of more than
    /// <see cref="ResyncThreshold"/> (new host after a migration, a long stall) re-seeds it.
    /// </summary>
    public sealed class HostClockEstimator
    {
        public const float ResyncThreshold = 1f;
        public const float BucketSec = 0.5f;
        public const int Buckets = 8;
        public const float WindowSec = BucketSec * Buckets;
        /// <summary>Seconds of offset change per second of local time (5%: not visible on a creature).</summary>
        public const float SlewPerSec = 0.05f;

        private readonly float[] _bucketMax = new float[Buckets];
        private int _bucket;
        private float _bucketStart;
        private float _lastLocal;
        private bool _has;
        private float _offset;
        private float _target;

        public bool HasEstimate => _has;
        public float Offset => _offset;
        /// <summary>Windowed best-case offset the estimate slews toward.</summary>
        public float Target => _target;

        public void Reset()
        {
            _has = false;
            _offset = 0f;
            _target = 0f;
            _bucket = 0;
            _bucketStart = 0f;
            _lastLocal = 0f;
            for (int i = 0; i < Buckets; i++)
                _bucketMax[i] = float.NegativeInfinity;
        }

        public HostClockEstimator() => Reset();

        public void AddSample(float hostTime, float localTime)
        {
            float o = hostTime - localTime;
            if (!_has || o - _offset > ResyncThreshold || _offset - o > ResyncThreshold)
            {
                Seed(o, localTime);
                return;
            }

            // Move the window: buckets older than WindowSec drop out.
            int steps = 0;
            while (localTime >= _bucketStart + BucketSec && steps < Buckets)
            {
                _bucket = (_bucket + 1) % Buckets;
                _bucketMax[_bucket] = float.NegativeInfinity;
                _bucketStart += BucketSec;
                steps++;
            }
            if (localTime >= _bucketStart + BucketSec)
                _bucketStart = localTime;
            if (o > _bucketMax[_bucket])
                _bucketMax[_bucket] = o;

            float target = float.NegativeInfinity;
            for (int i = 0; i < Buckets; i++)
            {
                if (_bucketMax[i] > target)
                    target = _bucketMax[i];
            }
            _target = target;

            float dt = localTime - _lastLocal;
            if (dt < 0f) dt = 0f;
            _lastLocal = localTime;
            float step = SlewPerSec * dt;
            float d = _target - _offset;
            if (d > step) d = step;
            else if (d < -step) d = -step;
            _offset += d;
        }

        private void Seed(float o, float localTime)
        {
            for (int i = 0; i < Buckets; i++)
                _bucketMax[i] = float.NegativeInfinity;
            _bucket = 0;
            _bucketStart = localTime;
            _bucketMax[0] = o;
            _offset = o;
            _target = o;
            _lastLocal = localTime;
            _has = true;
        }

        public float ToHost(float localTime) => localTime + _offset;
    }

    /// <summary>
    /// Arrival lateness of the snapshot stream: how much later than the best case each batch got
    /// here (seconds, against <see cref="HostClockEstimator"/>). Mean and mean deviation, both
    /// smoothed (RFC 6298 style); <see cref="Margin"/> is the render delay it costs on top of the
    /// send interval.
    /// </summary>
    public sealed class ArrivalJitter
    {
        public const float MeanGain = 0.125f;
        public const float DevGain = 0.25f;
        public const float MaxMargin = 0.15f;

        private bool _has;
        public float Mean { get; private set; }
        public float Dev { get; private set; }

        public void Reset()
        {
            _has = false;
            Mean = 0f;
            Dev = 0f;
        }

        public void Add(float age)
        {
            if (!_has)
            {
                _has = true;
                Mean = age;
                Dev = 0f;
                return;
            }
            float err = age - Mean;
            Mean += err * MeanGain;
            Dev += (Math.Abs(err) - Dev) * DevGain;
        }

        /// <summary>Lateness to ride out: mean plus two deviations, never below zero.</summary>
        public float Margin
        {
            get
            {
                float m = Mean + 2f * Dev;
                if (m < 0f) m = 0f;
                if (m > MaxMargin) m = MaxMargin;
                return m;
            }
        }
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
        /// <summary>The host's animator keeps this clip moving (a loop, or a once clip vanilla replays every frame).</summary>
        public bool Animating;
        /// <summary>The host teleported into this pose: no motion from the previous sample, it is shown at <see cref="T"/>.</summary>
        public bool Cut;
        /// <summary>Made on the client (a pose hold, or the shown pose a timeline starts from), not a host sample.</summary>
        public bool Synthetic;
        /// <summary>A clip the host started and left again between the previous sample and this one (null: none).</summary>
        public string PrevClip;
        public float PrevClipT;
    }

    public enum TimelinePoseKind : byte
    {
        /// <summary>No samples.</summary>
        Empty,
        /// <summary>Render time before the oldest sample: the oldest pose.</summary>
        BeforeOldest,
        /// <summary>Between two samples.</summary>
        Interpolated,
        /// <summary>Past the newest sample, still coasting along its motion.</summary>
        Coasting,
        /// <summary>Past the newest sample and the coast: the pose waits for the next sample.</summary>
        Holding
    }

    /// <summary>
    /// Host-time-stamped pose history for one entity. The client renders it a delay behind the
    /// estimated host clock: linear between samples, a short coast past the newest one that
    /// slows to a stop, then a hold. No Unity types, so it is unit-tested.
    /// </summary>
    public sealed class EntityTimeline
    {
        public const int Capacity = 8;
        /// <summary>A body the host skipped longer than this many send intervals rested: its old spot is held until one interval before the new sample.</summary>
        public const float HoldGapIntervals = 3f;
        /// <summary>Coast speed bound (world units per second; a sprinting dog is well under it).</summary>
        public const float MaxCoastSpeed = 2000f;

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
        /// The clip to show at host time <paramref name="t"/>: the newest clip start at or before
        /// <paramref name="t"/>, once. A sample carries up to two: the short clip the host passed
        /// through since the previous sample (<see cref="TimelineSample.PrevClip"/>), then its own.
        /// False when that start (or a newer clip played on arrival, see
        /// <see cref="HoldClipsThrough"/>) was already shown, so the body's clip changes when its
        /// pose reaches the host moment of the change. A pass-through clip comes back as a sample
        /// whose <see cref="TimelineSample.T"/> is its start and frame is 0.
        /// </summary>
        public bool TakeClip(float t, out TimelineSample clip)
        {
            clip = default;
            for (int i = _count - 1; i >= 0; i--)
            {
                TimelineSample x = _s[i];
                if (x.T <= t)
                {
                    if (x.T <= _clipShownT)
                        return false;
                    if (x.HasClip)
                    {
                        _clipShownT = x.T;
                        clip = x;
                        return true;
                    }
                }
                if (x.PrevClip != null && x.PrevClipT <= t)
                {
                    if (x.PrevClipT <= _clipShownT)
                        return false;
                    _clipShownT = x.PrevClipT;
                    clip = x;
                    clip.T = x.PrevClipT;
                    clip.Clip = x.PrevClip;
                    clip.ClipFrame = 0;
                    clip.Animating = false;
                    clip.PrevClip = null;
                    return true;
                }
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
        /// sample is more than <see cref="HoldGapIntervals"/> of <paramref name="sendInterval"/>
        /// newer, the old spot is held until one interval before it, so the move does not start
        /// early. A teleport (<see cref="TimelineSample.Cut"/>) needs no hold: it never blends.
        /// </summary>
        public bool Add(TimelineSample x, float sendInterval)
        {
            if (_count > 0)
            {
                TimelineSample last = _s[_count - 1];
                if (x.T <= last.T)
                    return false;
                if (!x.Cut && sendInterval > 0f && x.T - last.T > sendInterval * HoldGapIntervals)
                {
                    TimelineSample hold = last;
                    hold.T = x.T - sendInterval;
                    hold.HasClip = false; // a pose hold, not a clip the host sent again
                    hold.PrevClip = null;
                    hold.Cut = false;
                    hold.Synthetic = true;
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
        /// Across a teleport: the pose before it until its moment, then the new one. Past the
        /// newest: coast along the last host segment for at most <paramref name="maxCoast"/>
        /// seconds, slowing linearly to a stop (it covers half the distance a full-speed coast
        /// would), then hold. No coast out of a teleport or a client-made sample.
        /// </summary>
        public TimelinePoseKind Sample(float t, float maxCoast, out TimelineSample pose)
        {
            pose = default;
            if (_count == 0)
                return TimelinePoseKind.Empty;

            TimelineSample first = _s[0];
            if (t <= first.T)
            {
                pose = first;
                pose.T = t;
                return _count == 1 && t == first.T ? TimelinePoseKind.Interpolated : TimelinePoseKind.BeforeOldest;
            }

            TimelineSample newest = _s[_count - 1];
            if (t >= newest.T)
            {
                pose = newest;
                pose.T = t;
                float extra = t - newest.T;
                if (extra <= 0f)
                    return TimelinePoseKind.Interpolated;
                if (_count < 2 || maxCoast <= 0f)
                    return TimelinePoseKind.Holding;
                TimelineSample prev = _s[_count - 2];
                float seg = newest.T - prev.T;
                if (newest.Cut || newest.Synthetic || prev.Synthetic || seg <= 0f)
                    return TimelinePoseKind.Holding;

                float vx = (newest.X - prev.X) / seg;
                float vy = (newest.Y - prev.Y) / seg;
                float vz = (newest.Z - prev.Z) / seg;
                float sp = (float)Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (sp > MaxCoastSpeed)
                {
                    float k = MaxCoastSpeed / sp;
                    vx *= k; vy *= k; vz *= k;
                }
                float e = extra < maxCoast ? extra : maxCoast;
                float travel = e - e * e / (2f * maxCoast);
                pose.X += vx * travel;
                pose.Y += vy * travel;
                pose.Z += vz * travel;
                return extra < maxCoast ? TimelinePoseKind.Coasting : TimelinePoseKind.Holding;
            }

            for (int i = _count - 1; i > 0; i--)
            {
                TimelineSample a = _s[i - 1];
                TimelineSample b = _s[i];
                if (t < a.T)
                    continue;
                if (b.Cut)
                {
                    // A teleport: the body stays where it was until the host moment it moved.
                    pose = a;
                    pose.T = t;
                    return TimelinePoseKind.Interpolated;
                }
                float span = b.T - a.T;
                float u = span > 0f ? (t - a.T) / span : 1f;
                pose.T = t;
                pose.X = a.X + (b.X - a.X) * u;
                pose.Y = a.Y + (b.Y - a.Y) * u;
                pose.Z = a.Z + (b.Z - a.Z) * u;
                pose.RotY = LerpAngle(a.RotY, b.RotY, u);
                return TimelinePoseKind.Interpolated;
            }

            pose = first;
            pose.T = t;
            return TimelinePoseKind.BeforeOldest;
        }

        /// <summary>The newest sample at or before host time <paramref name="t"/> (the oldest when none is). False when empty.</summary>
        public bool LatestAt(float t, out TimelineSample sample)
        {
            sample = default;
            if (_count == 0)
                return false;
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_s[i].T <= t)
                {
                    sample = _s[i];
                    return true;
                }
            }
            sample = _s[0];
            return true;
        }

        /// <summary>True when a teleport sample's moment lies in (<paramref name="from"/>, <paramref name="to"/>].</summary>
        public bool HasCutIn(float from, float to)
        {
            for (int i = _count - 1; i >= 0; i--)
            {
                TimelineSample x = _s[i];
                if (x.T <= from)
                    return false;
                if (x.Cut && x.T <= to)
                    return true;
            }
            return false;
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

    /// <summary>
    /// Host side: the last two clip starts of one creature, so a short clip that began and ended
    /// between two snapshots (a turn's loop between its start and end) still reaches the client.
    /// No Unity types, so it is unit-tested.
    /// </summary>
    public struct ClipStartLog
    {
        /// <summary>The body the log belongs to (instance id): ids are recycled.</summary>
        public int Owner;
        public string Name0;
        public float T0;
        public string Name1;
        public float T1;
        public string LastSentClip;
        public float LastSentT;

        /// <summary>The body's animator started <paramref name="clip"/> at host time <paramref name="t"/> (a different clip than it showed).</summary>
        public void Note(int owner, string clip, float t)
        {
            if (owner != Owner)
            {
                this = default;
                Owner = owner;
                LastSentT = float.NegativeInfinity;
            }
            Name1 = Name0;
            T1 = T0;
            Name0 = clip;
            T0 = t;
        }

        public void MarkSent(int owner, string clip, float t)
        {
            if (owner != Owner)
            {
                this = default;
                Owner = owner;
            }
            LastSentClip = clip;
            LastSentT = t;
        }

        /// <summary>
        /// A clip that started after the last send and is neither the one sent then nor
        /// <paramref name="current"/> (the clip this send carries): the client would never see it.
        /// </summary>
        public bool TryIntermediate(int owner, string current, out string clip, out float t)
        {
            clip = null;
            t = 0f;
            if (owner != Owner || Name0 == null)
                return false;
            if (string.Equals(Name0, current, StringComparison.Ordinal))
            {
                if (Name1 == null || T1 <= LastSentT || T0 <= LastSentT)
                    return false;
                if (string.Equals(Name1, current, StringComparison.Ordinal)
                    || string.Equals(Name1, LastSentClip, StringComparison.Ordinal))
                    return false;
                clip = Name1;
                t = T1;
                return true;
            }
            // The current clip is chosen but not started yet: the last start is the pass-through.
            if (T0 <= LastSentT || string.Equals(Name0, LastSentClip, StringComparison.Ordinal))
                return false;
            clip = Name0;
            t = T0;
            return true;
        }
    }
}
