using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// First-enter outside-pad resync for InteractiveItem isOn + constructed sites.
    /// Late-join pending (FIFO 64) often ages out before the virgin pad exists.
    /// </summary>
    internal sealed partial class LockNetHandlers
    {
        /// <summary>
        /// Host→peer: InteractiveItem currently isOn under/near the pad (msg 62 set-state).
        /// Vanilla GE <c>switchItemOnOff</c> toggles <see cref="Item"/> only — not
        /// InteractiveItem — so fired GE bulk does not cover levers. Wells that host
        /// healed off stay omitted (same as join bulk: only isOn).
        /// </summary>
        internal int SendInteractivesNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = 2500f * 2500f;

            InteractiveItem[] items = WorldQueryHelper.GetCachedSceneComponents<InteractiveItem>();
            int sent = 0;
            const int maxSend = 64;
            for (int i = 0; i < items.Length && sent < maxSend; i++)
            {
                InteractiveItem ii = items[i];
                if (ii == null || !ii.isOn || ii.transform == null) continue;
                if (!ii.gameObject.scene.IsValid()) continue;
                if (!IsUnderOrNearLocation(ii.transform, root, anchor, maxDistSqr))
                    continue;

                Vector3 pos = ii.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(pos.x * 10f) / 10f,
                    Mathf.Round(pos.y * 10f) / 10f,
                    Mathf.Round(pos.z * 10f) / 10f);
                _net.SendToPlayer(targetPlayerId, NetMessageType.InteractiveItemSwitch,
                    w => new InteractiveItemSwitchMessage
                    {
                        PosX = key.x,
                        PosY = key.y,
                        PosZ = key.z,
                        IsOn = true
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                sent++;
            }

            return sent;
        }

        /// <summary>
        /// Host→peer: constructed sites under/near the pad. Apply skips if already
        /// <c>constructed</c> (idempotent).
        /// </summary>
        internal int SendConstructedSitesNearLocationTo(int targetPlayerId, Location loc)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0 || loc == null)
                return 0;

            Transform root = loc.transform;
            Vector3 anchor = loc.playerSpawn != null
                ? loc.playerSpawn.transform.position
                : (root != null ? root.position : Vector3.zero);
            const float maxDistSqr = 2500f * 2500f;

            int sent = 0;
            const int maxSend = 64;
            foreach (var kvp in _constructedSites)
            {
                if (sent >= maxSend) break;
                var msg = kvp.Value;
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (!IsNearLocationAnchor(pos, root, anchor, maxDistSqr))
                    continue;
                _net.SendToPlayer(targetPlayerId, NetMessageType.ConstructibleConstruction,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
                sent++;
            }

            Constructible[] all = WorldQueryHelper.GetCachedSceneComponents<Constructible>();
            for (int i = 0; i < all.Length && sent < maxSend; i++)
            {
                Constructible c = all[i];
                if (c == null || !c.constructed || c.transform == null) continue;
                if (!IsUnderOrNearLocation(c.transform, root, anchor, maxDistSqr))
                    continue;
                Vector3 p = c.transform.position;
                Vector3 key = new Vector3(
                    Mathf.Round(p.x * 10f) / 10f,
                    Mathf.Round(p.y * 10f) / 10f,
                    Mathf.Round(p.z * 10f) / 10f);
                string id = ConstructibleSiteKey(key);
                if (_constructedSites.ContainsKey(id)) continue;
                var msg = new ConstructibleMessage
                {
                    PosX = key.x,
                    PosY = key.y,
                    PosZ = key.z,
                    UseIngredients = false,
                    OptionIndex = c.chosenOption
                };
                _constructedSites[id] = msg;
                _net.SendToPlayer(targetPlayerId, NetMessageType.ConstructibleConstruction,
                    w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
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
            return IsNearLocationAnchor(t.position, root, anchor, maxDistSqr);
        }

        private static bool IsNearLocationAnchor(
            Vector3 pos, Transform root, Vector3 anchor, float maxDistSqr)
        {
            if (root != null)
            {
                float dxRoot = pos.x - root.position.x;
                float dzRoot = pos.z - root.position.z;
                if (dxRoot * dxRoot + dzRoot * dzRoot <= maxDistSqr)
                    return true;
            }
            float dx = pos.x - anchor.x;
            float dz = pos.z - anchor.z;
            return dx * dx + dz * dz <= maxDistSqr;
        }
    }
}
