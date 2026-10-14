using DWMPHorde.Logging;
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
        private static bool Has(string name, string token)
            => name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Prefix(UnityEngine.Object obj)
        {
            // Offline (mod loaded, no session) must stay vanilla; Network is non-null there.
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            // Suppress during trap placement (the inventory item destruction should not trigger removal)
            if (Sync.TrapPlacementPatch.InsideTrapPlacement) return;
            // getDroppedItem owns WOR via SendPickup / FinishWorldPickupClaim (host-auth).
            if (WorldPickupWireGuard.IsActive) return;

            GameObject go = obj as GameObject;
            if (go == null) return;

            // Case-insensitive token tests on the name as is (no lowered copy per destroy).
            string name = go.name;

            // Group name checks by component type to reduce false positives
            bool isTrap = Has(name, "trap") || Has(name, "bear") || Has(name, "snap") || Has(name, "animal");
            Trigger trigComp = go.GetComponent<Trigger>();
            if (trigComp != null && (trigComp.isBearTrap || trigComp.isChainTrap || trigComp.isMutatedTrap))
                isTrap = true;
            bool isDestructible = Has(name, "barrel") || Has(name, "tank") || Has(name, "glass") || Has(name, "chain");
            bool isHarvestable = Has(name, "mushroom") || Has(name, "_exp") || Has(name, "bio_");

            if (!isTrap && !isDestructible && !isHarvestable)
                return;

            // Component validation: verify the object type matches
            if (isTrap && trigComp == null && go.GetComponent<Character>() == null)
                return;
            if (isDestructible && go.GetComponent<Explodes>() == null && go.GetComponent<Item>() == null)
                return;
            if (isHarvestable && go.GetComponent<Item>() == null)
                return;

            if (Has(name, "audioobject"))
                return;

            // Skip ProxyItem (placement preview) destruction; it is not a real world trap.
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
            // Co-op rescue: local destroy of an occupied trap frees the stuck body here
            // (peer is freed via WorldObjectRemoved → DestroyObjectByPos).
            if (isTrap)
                LocalBearTrap.ReleaseIfRemoved(go, p);
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

            ModLog.Event(LogCat.World, "[TrapDestroy] sent removed \"" + go.name + "\" at " + key
                + " bear=" + (trigComp != null && trigComp.isBearTrap));
        }
    }
}
