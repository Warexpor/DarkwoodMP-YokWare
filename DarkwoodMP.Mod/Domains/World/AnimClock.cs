using DWMPHorde.Logging;
using DWMPHorde.Networking;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The shared animation clock: what time it is for the world's looping and scheduled
    /// animations (<see cref="AnimPhase"/>, <see cref="AnimSchedule"/>). Vanilla advances each
    /// animation from the moment that machine created it, and only while it is on screen, so two
    /// machines showed the same fire, tree or twitching corpse at different points of its cycle.
    /// <para>
    /// The host's clock is its process uptime minus the time the world stood in a shared pause
    /// (held still during one). A client estimates the host's uptime from ping/pong round trips
    /// (<see cref="ClockSync"/>, half the round trip added) and takes the pause figures from each
    /// pong and from a beat the host sends on every pause or resume. Offline, the clock is this
    /// machine's own, held while the game is paused; the switch to the host's clock (and any jump of
    /// it) moves <see cref="Epoch"/>, so animations re-align at once instead of drifting over.
    /// </para>
    /// </summary>
    internal static class AnimClock
    {
        private const double FastPingSec = 0.25;
        private const double PingSec = 2.0;
        private const int FastPings = 6;

        // This machine's own clock (host and offline): process-wide like the uptime it counts.
        private static double _pausedTotal; // process-scoped: this machine's clock, not a session's
        private static double _pauseStart = -1; // process-scoped: this machine's clock, not a session's

        // The host's clock, on a client.
        private static readonly ClockSync _sync = new ClockSync(); // reset-in: Reset
        private static double _hostPausedTotal; // reset-in: Reset
        private static double _hostPauseStart = -1; // reset-in: Reset
        private static double _hostStateAt = double.NegativeInfinity; // reset-in: Reset
        private static bool _hostSeen; // reset-in: Reset
        private static double _nextPing; // reset-in: Reset
        private static int _pings; // reset-in: Reset
        private static bool _hostBeatPaused; // reset-in: Reset

        private static int _mode = -1; // process-scoped: which clock Now read last (epoch edge detection)
        private static int _lastSnaps; // process-scoped: ClockSync snaps already counted into Epoch

        /// <summary>Moves whenever the clock source changes or jumps: aligned animations snap to it.</summary>
        internal static int Epoch { get; private set; }

        internal static void Reset()
        {
            _sync.Reset();
            _hostPausedTotal = 0;
            _hostPauseStart = -1;
            _hostStateAt = double.NegativeInfinity;
            _hostSeen = false;
            _nextPing = 0;
            _pings = 0;
            _hostBeatPaused = false;
            _shared = false;
        }

        private static double Local => Time.unscaledTimeAsDouble;

        private static bool ConnectedClient(out LanNetworkManager net)
        {
            net = ModRuntime.Network;
            return net != null && net.IsConnected && net.Role == NetworkRole.Client;
        }

        private static bool UseHostClock => _hostSeen && _sync.HasEstimate && ConnectedClient(out _);

        /// <summary>
        /// In a session, with the shared clock in hand: animations follow it. Offline (or a client
        /// still waiting for its first pong) they run as vanilla. Worked out once a frame
        /// (<see cref="Tick"/>): thousands of animators ask.
        /// </summary>
        internal static bool Shared => _shared;

        private static bool _shared; // reset-in: Reset
        private static double _frameNow; // process-scoped: Now cached for one frame
        private static int _frameNowAt = -1; // process-scoped: frame of _frameNow

        /// <summary><see cref="Now"/>, read once per frame (the animators of one frame share it).</summary>
        internal static double FrameNow
        {
            get
            {
                int frame = Time.frameCount;
                if (frame != _frameNowAt)
                {
                    _frameNowAt = frame;
                    _frameNow = Now;
                }
                return _frameNow;
            }
        }

        /// <summary>Seconds on the animation clock.</summary>
        internal static double Now
        {
            get
            {
                if (UseHostClock)
                {
                    if (_hostPauseStart >= 0)
                        return _hostPauseStart - _hostPausedTotal;
                    return _sync.HostNow(Local) - _hostPausedTotal;
                }
                return _pauseStart >= 0 ? _pauseStart - _pausedTotal : Local - _pausedTotal;
            }
        }

        /// <summary>Every frame (the network manager's Update, unscaled).</summary>
        internal static void Tick(LanNetworkManager net)
        {
            bool connected = net != null && net.IsConnected;
            bool client = connected && net.Role == NetworkRole.Client;
            bool host = connected && net.Role == NetworkRole.Host;
            double now = Local;

            // This machine's own clock stops for a pause: the shared world pause in a session,
            // vanilla's pause offline. A client's own clock is not the one in use once synced.
            bool paused = host ? PauseMenuSync.WorldPaused : !client && Time.timeScale == 0f;
            if (paused && _pauseStart < 0)
                _pauseStart = now;
            else if (!paused && _pauseStart >= 0)
            {
                _pausedTotal += now - _pauseStart;
                _pauseStart = -1;
            }

            if (host && paused != _hostBeatPaused)
            {
                _hostBeatPaused = paused;
                var beat = Stamp(WorldClockMessage.KindBeat, 0);
                net.SendToAll(NetMessageType.WorldClock, w => beat.Serialize(w), DeliveryMethod.ReliableOrdered);
            }

            if (client && net.IsHandshakeComplete && now >= _nextPing)
            {
                _pings++;
                _nextPing = now + (_pings <= FastPings ? FastPingSec : PingSec);
                var ping = new WorldClockMessage { Kind = WorldClockMessage.KindPing, ClientSent = now };
                net.Send(NetMessageType.WorldClock, w => ping.Serialize(w), DeliveryMethod.Unreliable);
            }

            _shared = host || (client && UseHostClock);
            int mode = UseHostClock ? 1 : 0;
            if (mode != _mode || (mode == 1 && _sync.Snaps != _lastSnaps))
            {
                if (mode == 1)
                    ModLog.Event(LogCat.World, "[AnimClock] following the host's clock (offset "
                        + _sync.Offset.ToString("F3") + " s" + (mode == _mode ? ", jumped" : "") + ")");
                else if (_mode == 1)
                    ModLog.Event(LogCat.World, "[AnimClock] back on this machine's own clock");
                _mode = mode;
                _lastSnaps = _sync.Snaps;
                Epoch++;
            }
        }

        private static WorldClockMessage Stamp(byte kind, double clientSent) => new WorldClockMessage
        {
            Kind = kind,
            ClientSent = clientSent,
            HostTime = Local,
            PausedTotal = _pausedTotal,
            PauseStart = _pauseStart
        };

        internal static void Handle(LanNetworkManager net, WorldClockMessage msg)
        {
            if (net == null)
                return;
            if (msg.Kind == WorldClockMessage.KindPing)
            {
                int from = net.CurrentReceivePlayerId;
                if (net.Role != NetworkRole.Host || from <= 0)
                    return;
                var pong = Stamp(WorldClockMessage.KindPong, msg.ClientSent);
                net.SendToPlayer(from, NetMessageType.WorldClock, w => pong.Serialize(w), DeliveryMethod.Unreliable);
                return;
            }
            if (net.Role != NetworkRole.Client)
                return;
            if (msg.Kind == WorldClockMessage.KindPong)
            {
                _sync.AddSample(msg.ClientSent, msg.HostTime, Local);
                _hostSeen = true;
            }
            // A late pong must not undo a newer beat's pause figures.
            if (msg.HostTime >= _hostStateAt)
            {
                _hostStateAt = msg.HostTime;
                _hostPausedTotal = msg.PausedTotal;
                _hostPauseStart = msg.PauseStart;
            }
        }
    }
}
