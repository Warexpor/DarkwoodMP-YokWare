using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>First-enter pad resync for saw / feeder / lure on outside pads.</summary>
    internal sealed partial class StationNetHandlers
    {
        /// <summary>
        /// Host→peer: saw / feeder / lure states under/near the pad.
        /// Pending station queues are FIFO-capped (16) before virgin-pad spawn.
        /// </summary>
        internal int SendStationsNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = 2500f * 2500f;
            int sent = 0;

            Saw[] saws = WorldQueryHelper.GetCachedSceneComponents<Saw>();
            for (int i = 0; i < saws.Length; i++)
            {
                Saw saw = saws[i];
                if (saw == null || saw.transform == null) continue;
                if (!IsUnderOrNearLocation(saw.transform, root, anchor, maxDistSqr))
                    continue;
                var msg = Sync.SawSyncHelpers.BuildMessage(saw);
                _net.SendBulkOrAll(NetMessageType.SawState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            Feeder[] feeders = WorldQueryHelper.GetCachedSceneComponents<Feeder>();
            for (int i = 0; i < feeders.Length; i++)
            {
                Feeder f = feeders[i];
                if (f == null || f.transform == null) continue;
                if (!IsUnderOrNearLocation(f.transform, root, anchor, maxDistSqr))
                    continue;
                Vector3 p = f.transform.position;
                var msg = new FeederStateMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    Active = f.Active
                };
                _net.SendBulkOrAll(NetMessageType.FeederState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            Lure[] lures = WorldQueryHelper.GetCachedSceneComponents<Lure>();
            for (int i = 0; i < lures.Length; i++)
            {
                Lure lure = lures[i];
                if (lure == null || lure.transform == null) continue;
                if (!IsUnderOrNearLocation(lure.transform, root, anchor, maxDistSqr))
                    continue;
                Vector3 p = lure.transform.position;
                var msg = new LureStateMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    Health = lure.health
                };
                _net.SendBulkOrAll(NetMessageType.LureState, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            return sent;
        }

        private static bool IsUnderOrNearLocation(
            Transform t, Transform root, Vector3 anchor, float maxDistSqr)
        {
            if (t == null) return false;
            if (root != null && (t == root || t.IsChildOf(root)))
                return true;
            if (root != null)
            {
                float dxRoot = t.position.x - root.position.x;
                float dzRoot = t.position.z - root.position.z;
                if (dxRoot * dxRoot + dzRoot * dzRoot <= maxDistSqr)
                    return true;
            }
            float dx = t.position.x - anchor.x;
            float dz = t.position.z - anchor.z;
            return dx * dx + dz * dz <= maxDistSqr;
        }
    }
}
