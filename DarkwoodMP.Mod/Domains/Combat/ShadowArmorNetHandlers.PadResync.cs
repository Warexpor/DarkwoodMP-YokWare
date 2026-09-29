using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>First-enter pad resync for damaged ShadowArmor on pad props.</summary>
    internal sealed partial class ShadowArmorNetHandlers
    {
        /// <summary>
        /// Host→peer: damaged ShadowArmor under/near the pad (chests / prop armor).
        /// Pending FIFO 32 misses virgin-pad targets the same as locks.
        /// </summary>
        internal int SendShadowArmorStatesNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = 2500f * 2500f;

            ShadowArmor[] all = WorldQueryHelper.GetCachedSceneComponents<ShadowArmor>();
            int sent = 0;
            const int maxSend = 64;
            for (int i = 0; i < all.Length && sent < maxSend; i++)
            {
                ShadowArmor armor = all[i];
                if (armor == null || armor.transform == null) continue;

                float maxHp = armor.maxHealth > 0f ? armor.maxHealth : armor.health;
                if (!(maxHp > 0f && armor.health < maxHp))
                    continue;
                if (!IsUnderOrNearLocation(armor.transform, root, anchor, maxDistSqr))
                    continue;

                var msg = ShadowArmorSyncHelpers.BuildMessage(armor, destroyed: false);
                _net.SendBulkOrAll(NetMessageType.ShadowArmorState, w => msg.Serialize(w),
                    targetPlayerId);
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
