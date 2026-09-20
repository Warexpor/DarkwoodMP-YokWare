using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Stable multiplayer id for a death bag. Survives position/Y snap differences
    /// that made float position keys unreliable for spawn/loot matching.
    /// </summary>
    public sealed class DeathBagNetworkId : MonoBehaviour
    {
        public string BagId;
        public bool InWater;

        public static DeathBagNetworkId Ensure(GameObject go, string bagId, bool inWater = false)
        {
            if (go == null || string.IsNullOrEmpty(bagId)) return null;
            var id = go.GetComponent<DeathBagNetworkId>();
            if (id == null)
                id = go.AddComponent<DeathBagNetworkId>();
            id.BagId = bagId;
            id.InWater = inWater;
            return id;
        }

        public static string GetBagId(GameObject go)
        {
            if (go == null) return null;
            var id = go.GetComponent<DeathBagNetworkId>();
            return id != null ? id.BagId : null;
        }

        public static string GetOrAssignBagId(GameObject go, bool inWater = false)
        {
            if (go == null) return null;
            var id = go.GetComponent<DeathBagNetworkId>();
            if (id != null && !string.IsNullOrEmpty(id.BagId))
                return id.BagId;
            string bagId = System.Guid.NewGuid().ToString("N");
            Ensure(go, bagId, inWater);
            return bagId;
        }
    }
}
