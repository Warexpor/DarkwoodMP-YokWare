using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Players
{
    /// <summary>
    /// Cross-path debounce for proxy FF damage. getHit is authoritative and always
    /// relays (multi-pellet safe). Collision/hitscan safety-nets skip when getHit
    /// already relayed this frame for the same attacker/victim pair.
    /// </summary>
    internal static class ProxyCombatRelay
    {
        private struct PairKey
        {
            public int AttackerId;
            public int VictimId;
        }

        private static readonly HashSet<PairKey> GetHitMarkedThisFrame = new HashSet<PairKey>();
        private static int _lastFrame = -1;

        private static void EnsureFrame()
        {
            int frame = Time.frameCount;
            if (frame != _lastFrame)
            {
                GetHitMarkedThisFrame.Clear();
                _lastFrame = frame;
            }
        }

        /// <summary>
        /// Marks getHit relay for this pair. Always returns true — every pellet allowed.
        /// </summary>
        public static bool TryMarkGetHitRelay(int attackerId, int victimId)
        {
            EnsureFrame();
            GetHitMarkedThisFrame.Add(new PairKey { AttackerId = attackerId, VictimId = victimId });
            return true;
        }

        /// <summary>
        /// Safety-net paths may relay only if getHit has not already marked this pair this frame.
        /// </summary>
        public static bool TryConsumeSafetyNet(int attackerId, int victimId)
        {
            EnsureFrame();
            return !GetHitMarkedThisFrame.Contains(new PairKey { AttackerId = attackerId, VictimId = victimId });
        }

        public static int ResolveAttackerPlayerId(Transform attackerTransform, int localPlayerId)
        {
            if (attackerTransform != null)
            {
                var atkProxy = attackerTransform.GetComponentInParent<RemotePlayerProxy>();
                if (atkProxy != null)
                    return atkProxy.PlayerId;
            }

            return localPlayerId;
        }
    }
}
