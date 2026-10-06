using System;

namespace DWMPHorde
{
    /// <summary>
    /// A client's estimate of the host's clock from ping/pong round trips (NTP style). Each pong
    /// gives an offset sample <c>host + rtt/2 - received</c>; the sample with the smallest round
    /// trip of the last <see cref="Window"/> is the target (the least queued one, so the half-rtt
    /// assumption is the most accurate there). The offset in use slews toward the target at
    /// <see cref="SlewPerSec"/>, so the clock never jumps for jitter; a target more than
    /// <see cref="SnapSec"/> away (a new host after a migration, a long stall) is taken at once
    /// and counted in <see cref="Snaps"/>.
    /// </summary>
    public sealed class ClockSync
    {
        public const int Window = 8;
        public const double SnapSec = 0.25;
        /// <summary>Seconds of offset change per second of local time (1%: no visible speed change).</summary>
        public const double SlewPerSec = 0.01;
        /// <summary>Round trips above this are not samples (a stalled link says nothing about the clock).</summary>
        public const double MaxRttSec = 3.0;

        private readonly double[] _offsets = new double[Window];
        private readonly double[] _rtts = new double[Window];
        private int _count;
        private int _next;
        private bool _has;
        private double _offset;
        private double _target;
        private double _lastLocal;

        public bool HasEstimate => _has;
        public double Offset => _offset;
        public double Target => _target;
        /// <summary>Times the estimate jumped instead of slewing.</summary>
        public int Snaps { get; private set; }

        public ClockSync() => Reset();

        public void Reset()
        {
            _count = 0;
            _next = 0;
            _has = false;
            _offset = 0;
            _target = 0;
            _lastLocal = 0;
            Snaps = 0;
        }

        /// <summary>A pong: the local send and receive times of the ping and the host time it carried.</summary>
        public void AddSample(double localSent, double hostTime, double localReceived)
        {
            double rtt = localReceived - localSent;
            if (rtt < 0 || rtt > MaxRttSec || double.IsNaN(hostTime) || double.IsInfinity(hostTime))
                return;
            _offsets[_next] = hostTime + rtt * 0.5 - localReceived;
            _rtts[_next] = rtt;
            _next = (_next + 1) % Window;
            if (_count < Window)
                _count++;

            int best = 0;
            for (int i = 1; i < _count; i++)
            {
                if (_rtts[i] < _rtts[best])
                    best = i;
            }
            _target = _offsets[best];
            if (!_has || Math.Abs(_target - Advance(localReceived)) > SnapSec)
            {
                if (_has)
                    Snaps++;
                _offset = _target;
                _has = true;
            }
            _lastLocal = localReceived;
        }

        /// <summary>The host clock now (<paramref name="local"/>: this machine's clock).</summary>
        public double HostNow(double local) => local + Advance(local);

        private double Advance(double local)
        {
            if (!_has)
                return 0;
            double dt = local - _lastLocal;
            if (dt > 0)
            {
                double step = SlewPerSec * dt;
                double d = _target - _offset;
                if (d > step) d = step;
                else if (d < -step) d = -step;
                _offset += d;
                _lastLocal = local;
            }
            return _offset;
        }
    }
}
