using System;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Another player's positions (PlayerState, about 30 a second), played back a short delay
    /// behind their arrival: linear between samples, a short coast along the packet's velocity
    /// past the newest one, then a hold. PlayerState carries no send time, so each sample is put
    /// on the sender's own beat: one measured send interval per sequence step after the one
    /// before, pulled forward to a packet that comes earlier than that and only slightly towards
    /// one that comes later (a lost packet keeps its slot, a late one does not stretch its
    /// segment). The delay is the interval plus how late packets come (<see cref="ArrivalJitter"/>),
    /// so a late packet is ridden out instead of stalling the body and catching up. Before this
    /// the body chased the newest position with an exponential lerp: a 30 Hz staircase, about
    /// 60 ms behind, and a stall then a lurch on every late packet. Pure (no Unity types) so it is
    /// unit tested.
    /// </summary>
    public sealed class RemotePoseTimeline
    {
        public const int Capacity = 16;
        /// <summary>The sender's PlayerState interval (LanNetworkManager.SendInterval) until one is measured.</summary>
        public const float DefaultInterval = 0.033f;
        private const float IntervalGain = 0.1f;
        /// <summary>Arrival gaps per sequence step longer than this (a pause, a hitch) do not feed the interval.</summary>
        private const float MaxIntervalSample = 0.25f;
        /// <summary>A packet this much later than its beat starts the beat again on it (a pause, a stall).</summary>
        public const float ResyncLateness = 0.25f;
        /// <summary>Share of a late packet's lateness the beat moves by (keeps it from falling behind the sender).</summary>
        private const float DriftGain = 0.05f;
        /// <summary>A sequence step this large is a new stream (a reconnect, a new host), not lost packets.</summary>
        private const int MaxSequenceStep = 64;
        /// <summary>How far past the newest sample the body coasts along its velocity before it holds.</summary>
        public const float MaxCoast = 0.1f;
        /// <summary>A packet velocity above this (a teleport on the sender's side) is not coasted on.</summary>
        public const float MaxCoastSpeed = 2000f;
        public const float DelayMax = 0.3f;
        /// <summary>Delay change per second: growing (late packets) quickly, shrinking slowly (playback runs 5% fast).</summary>
        private const float DelayGrowPerSec = 0.25f;
        private const float DelayShrinkPerSec = 0.05f;

        private struct Pose
        {
            public float T;
            public float X, Y, Z;
            public float VX, VZ;
        }

        private readonly Pose[] _s = new Pose[Capacity];
        private int _count;
        private readonly ArrivalJitter _jitter = new ArrivalJitter();
        private float _interval = DefaultInterval;
        private bool _hasBeat;
        private uint _lastSeq;
        private float _lastArrival;
        private float _delay = -1f;
        private float _lastSampleAt = float.NegativeInfinity;

        public int Count => _count;
        public float Interval => _interval;
        public float JitterMargin => _jitter.Margin;
        /// <summary>Render delay in use (-1 before the first sample is played).</summary>
        public float Delay => _delay;

        /// <summary>Everything forgotten: the next sample starts a new stream (a teleport, a respawn).</summary>
        public void Clear()
        {
            _count = 0;
            _hasBeat = false;
            _delay = -1f;
            _jitter.Reset();
            _lastSampleAt = float.NegativeInfinity;
        }

        /// <summary>
        /// A new position. <paramref name="seq"/> is the sender's PlayerState sequence (0: none,
        /// then each sample is one interval on); <paramref name="arrival"/> the local receive time.
        /// A sample at or behind the newest (a reordered packet) is dropped.
        /// </summary>
        public void Add(uint seq, float arrival, float x, float y, float z, float vx, float vz)
        {
            int step = 1;
            if (_hasBeat && seq != 0 && _lastSeq != 0)
            {
                step = unchecked((int)(seq - _lastSeq));
                if (step <= 0 && step > -MaxSequenceStep)
                    return; // reordered or repeated
            }
            float t;
            if (!_hasBeat || step <= 0 || step > MaxSequenceStep || _count == 0)
            {
                t = arrival;
                _count = 0;
                _jitter.Reset();
                _delay = -1f;
            }
            else
            {
                float gap = (arrival - _lastArrival) / step;
                if (gap > 0f && gap < MaxIntervalSample)
                    _interval += (gap - _interval) * IntervalGain;
                float beat = _s[_count - 1].T + _interval * step;
                if (arrival <= beat)
                    t = arrival;
                else if (arrival - beat > ResyncLateness)
                {
                    // A long silence (the sender paused, a stall): a new stream from here.
                    t = arrival;
                    _count = 0;
                    _jitter.Reset();
                    _delay = -1f;
                }
                else
                    t = beat + (arrival - beat) * DriftGain;
                if (_count > 0 && t <= _s[_count - 1].T)
                    t = _s[_count - 1].T + 1e-4f;
                _jitter.Add(arrival - t);
            }
            _hasBeat = true;
            _lastSeq = seq;
            _lastArrival = arrival;

            if (_count == Capacity)
            {
                Array.Copy(_s, 1, _s, 0, Capacity - 1);
                _count--;
            }
            _s[_count++] = new Pose { T = t, X = x, Y = y, Z = z, VX = vx, VZ = vz };
        }

        /// <summary>
        /// The position at <paramref name="now"/> (local time) minus the delay, and the velocity
        /// it moves with there. False with no sample yet.
        /// </summary>
        public bool Sample(float now, out float x, out float y, out float z, out float vx, out float vz)
        {
            x = y = z = vx = vz = 0f;
            if (_count == 0)
                return false;

            float want = _interval + _jitter.Margin;
            if (want > DelayMax) want = DelayMax;
            float dt = _lastSampleAt > float.NegativeInfinity ? now - _lastSampleAt : 0f;
            if (dt < 0f) dt = 0f;
            _lastSampleAt = now;
            if (_delay < 0f)
                _delay = want;
            else if (want > _delay)
                _delay = Math.Min(want, _delay + DelayGrowPerSec * dt);
            else
                _delay = Math.Max(want, _delay - DelayShrinkPerSec * dt);

            float rt = now - _delay;
            Pose newest = _s[_count - 1];
            Pose first = _s[0];
            if (rt <= first.T && (_count > 1 || rt < first.T))
            {
                // Behind the oldest kept sample (the delay just grew, or a stream's first sample
                // is not due yet): where it was.
                x = first.X; y = first.Y; z = first.Z;
                return true;
            }
            if (rt >= newest.T)
                return Coast(newest, rt, out x, out y, out z, out vx, out vz);

            int i = _count - 1;
            while (i > 0 && _s[i - 1].T > rt)
                i--;
            Pose a = _s[i - 1], b = _s[i];
            float span = b.T - a.T;
            float u = span > 1e-5f ? (rt - a.T) / span : 1f;
            x = a.X + (b.X - a.X) * u;
            y = a.Y + (b.Y - a.Y) * u;
            z = a.Z + (b.Z - a.Z) * u;
            if (span > 1e-5f)
            {
                vx = (b.X - a.X) / span;
                vz = (b.Z - a.Z) / span;
            }
            // Older samples are not needed once the render time is past them.
            if (i > 2)
            {
                int drop = i - 2;
                Array.Copy(_s, drop, _s, 0, _count - drop);
                _count -= drop;
            }
            return true;
        }

        /// <summary>Past the newest sample: along its own velocity for a short while, then held.</summary>
        private static bool Coast(Pose s, float rt, out float x, out float y, out float z, out float vx, out float vz)
        {
            x = s.X; y = s.Y; z = s.Z;
            vx = vz = 0f;
            float ahead = rt - s.T;
            if (ahead <= 0f)
                return true;
            float speedSq = s.VX * s.VX + s.VZ * s.VZ;
            if (speedSq > MaxCoastSpeed * MaxCoastSpeed)
                return true;
            if (ahead < MaxCoast)
            {
                vx = s.VX;
                vz = s.VZ;
            }
            float c = ahead < MaxCoast ? ahead : MaxCoast;
            x += s.VX * c;
            z += s.VZ * c;
            return true;
        }
    }
}
