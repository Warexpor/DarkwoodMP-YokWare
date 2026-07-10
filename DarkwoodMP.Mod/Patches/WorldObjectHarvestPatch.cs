using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Detects when a world GameObject (trap, harvestable, barrel, etc.) is destroyed
    /// directly (e.g. right-click removed, disarmed, harvested) and notifies the peer
    /// so the matching object there is also destroyed, preventing dupes.
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object) })]
    public static class ObjectDestroyTrapPatch
    {
        private static void Prefix(UnityEngine.Object obj)
        {
            if (ModRuntime.Network == null) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            // Suppress during trap placement (the inventory item destruction should not trigger removal)
            if (Sync.TrapPlacementPatch.InsideTrapPlacement) return;

            GameObject go = obj as GameObject;
            if (go == null) return;

            string name = go.name.ToLowerInvariant();

            // Group name checks by component type to reduce false positives
            bool isTrap = name.Contains("trap") || name.Contains("bear") || name.Contains("snap") || name.Contains("animal");
            bool isDestructible = name.Contains("barrel") || name.Contains("tank") || name.Contains("glass") || name.Contains("chain");
            bool isHarvestable = name.Contains("mushroom") || name.Contains("_exp") || name.Contains("bio_");

            if (!isTrap && !isDestructible && !isHarvestable)
                return;

            // Component validation: verify the object type matches
            if (isTrap && go.GetComponent<Trigger>() == null && go.GetComponent<Character>() == null)
                return;
            if (isDestructible && go.GetComponent<Explodes>() == null && go.GetComponent<Item>() == null)
                return;
            if (isHarvestable && go.GetComponent<Item>() == null)
                return;

            if (name.Contains("audioobject"))
                return;

            // Skip ProxyItem (placement preview) destruction — not a real world trap
            if (go.GetComponent<ProxyItem>() != null)
                return;

            // Explodes (mushrooms, barrels, tanks): ExplosionTrigger + SpawnExplosionVisual
            // own the FX. Sending WorldObjectRemoved first snaps the peer object away
            // before FX can run (no boom anim / debris). Let explosion path destroy.
            if (go.GetComponent<Explodes>() != null || go.GetComponentInParent<Explodes>() != null)
                return;

            // Skip objects that are children of the player (held inventory visuals)
            Player localPlayer = Player.Instance;
            if (localPlayer != null && go.transform.IsChildOf(localPlayer.transform))
                return;

            Vector3 p = go.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            ModRuntime.Network.SendWorldObjectRemoved(new WorldObjectRemovedMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                ObjectName = go.name
            });

            ModRuntime.LegacyInfo("[TrapDestroy] sent removed \"" + go.name + "\" at " + key);
        }
    }
}
