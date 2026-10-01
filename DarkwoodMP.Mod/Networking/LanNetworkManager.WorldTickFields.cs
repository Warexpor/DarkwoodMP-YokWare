using System.Collections.Generic;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host tick timers and shadow tracking fields used by <see cref="LanNetworkManager"/> Update
    /// and BulkSync reset. Kept on the partial class so accessors in the main file stay valid.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        private float _physicsSendTimer;
        internal int _physicsRecvLogCounter;
        private const float PhysicsSendInterval = 0.1f;

        private float _timeSyncTimer;
        /// <summary>
        /// Host clock fan-out. A short interval keeps CurrentTime moving while
        /// DoUpdateTime is disabled on clients.
        /// so day/night lighting lagged visibly. 0.5s keeps peers tight without flooding.
        /// </summary>
        private const float TimeSyncInterval = 0.5f;

        internal short _nextShadowId;
        internal readonly Dictionary<short, ShadowCreature> _shadowTracked = new Dictionary<short, ShadowCreature>();
        private float _shadowBroadcastTimer;
        private const float ShadowBroadcastInterval = 0.3f;

        public short GetNextShadowId()
        {
            _nextShadowId++;
            if (_nextShadowId >= 9999) _nextShadowId = 1;
            return _nextShadowId;
        }

        public void RegisterShadow(short id, ShadowCreature sc)
        {
            _shadowTracked[id] = sc;
        }

        public void UnregisterShadow(short id)
        {
            // Emit a final dead update so clients play Death1 and drop the lookup
            // before we forget the id (BroadcastShadowStates used to drop silently).
            if (_shadowTracked.TryGetValue(id, out ShadowCreature sc) && sc != null)
            {
                Vector3 p = sc.transform.position;
                SendShadowStateUpdate(new ShadowStateUpdateMessage
                {
                    ShadowId = id,
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    RotY = sc.transform.rotation.eulerAngles.y,
                    DistanceToPlayer = sc.distanceToPlayer,
                    Flags = 2 // dead
                });
            }
            _shadowTracked.Remove(id);
        }
    }
}
