using System.Collections.Generic;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Per-peer bookkeeping for one transport link. <see cref="LanNetworkManager"/> replaces the whole
    /// object whenever the transport stops (network stop, host migration, soft reconnect), so nothing
    /// here can survive into the next link by being left out of a reset list.
    /// </summary>
    internal sealed class LinkState
    {
        /// <summary>Host: peers whose Handshake was accepted. Client: the host id.</summary>
        public readonly HashSet<int> Handshaked = new HashSet<int>();

        /// <summary>At least one peer completed the handshake (gates the gameplay send loop).</summary>
        public bool HandshakeComplete;

        /// <summary>Host: peers refused during their drop grace (traffic dropped, fan-out skips them).</summary>
        public readonly HashSet<int> Rejected = new HashSet<int>();

        /// <summary>Host: joiners downloading, applying or loading the world (gameplay flood muted).</summary>
        public readonly HashSet<int> LoadingWorld = new HashSet<int>();

        /// <summary>Host: peers that reconnected already in the world (no world share).</summary>
        public readonly HashSet<int> CoopReconnect = new HashSet<int>();

        /// <summary>Host: peer → time the late-join bulk was queued (0 = pending immediate settle).</summary>
        public readonly Dictionary<int, float> AwaitingLateJoinBulk = new Dictionary<int, float>();

        /// <summary>Host: peer → next heavy late-join bulk phase still to send.</summary>
        public readonly Dictionary<int, int> PendingHeavyLateJoinBulk = new Dictionary<int, int>();

        /// <summary>Host: peer → failed attempts at the current heavy bulk phase.</summary>
        public readonly Dictionary<int, int> HeavyPhaseFailures = new Dictionary<int, int>();

        /// <summary>Highest PlayerState sequence seen per sender on this link.</summary>
        public readonly Dictionary<int, uint> LastPlayerStateSequence = new Dictionary<int, uint>();

        /// <summary>Highest unreliable PhysicsState sequence seen per sender on this link.</summary>
        public readonly Dictionary<int, uint> LastPhysicsStateSequence = new Dictionary<int, uint>();

        /// <summary>Highest reliable PhysicsState sequence seen per sender on this link.</summary>
        public readonly Dictionary<int, uint> LastReliablePhysicsStateSequence = new Dictionary<int, uint>();
    }

    /// <summary>
    /// Per-session network state that outlives a single link (it survives a migration or soft
    /// reconnect) but not the session: <see cref="LanNetworkManager.StopNetwork"/> replaces it.
    /// </summary>
    internal sealed class SessionState
    {
        /// <summary>The current transport link.</summary>
        public LinkState Link = new LinkState();

        /// <summary>Remote player → outside location (pad) it is in.</summary>
        public readonly Dictionary<int, string> RemoteOutsideLocation = new Dictionary<int, string>();

        /// <summary>Host: last PlayerLightState / PlayerAnimLibrary body per client, replayed to late joiners.</summary>
        public readonly Dictionary<long, byte[]> StickyPlayerPayloads = new Dictionary<long, byte[]>();

        /// <summary>Host: player id → install-scoped LAN stable client key.</summary>
        public readonly Dictionary<int, string> StableKeyByPlayer = new Dictionary<int, string>();

        /// <summary>Host: a waiting title client was told the host world is shareable.</summary>
        public bool HostWasShareableForWaitingClients;

        /// <summary>Host: told that the world it hosted from the pause menu must be loaded again.</summary>
        public bool HostToldToReload;

        /// <summary>Host: the HostWorldReady edge was sent for the current world.</summary>
        public bool HostWorldReadyEmitted;

        /// <summary>Client: the host reported (or implied) its world is ready to share.</summary>
        public bool ClientHostWorldReady;
    }
}
