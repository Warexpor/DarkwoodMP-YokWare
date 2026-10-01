using System.Collections.Generic;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host tick timers used by <see cref="LanNetworkManager"/> Update, and the shadow registry.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        private float _physicsSendTimer;
        internal int PhysicsRecvLogCounter;
        private const float PhysicsSendInterval = 0.1f;

        private float _timeSyncTimer;
        /// <summary>
        /// Host clock fan-out. A short interval keeps CurrentTime moving while
        /// DoUpdateTime is disabled on clients.
        /// so day/night lighting lagged visibly. 0.5s keeps peers tight without flooding.
        /// </summary>
        private const float TimeSyncInterval = 0.5f;

        /// <summary>Host: shadow creatures by network id.</summary>
        internal ShadowRegistry Shadows { get; } = new ShadowRegistry();
        private float _shadowBroadcastTimer;
        private const float ShadowBroadcastInterval = 0.3f;
    }
}
