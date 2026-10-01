using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using DWMPHorde.Players;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{

    /// <summary>
    /// Intercepts Inventory-slot drop — Player.spawnDroppedInvItem(InvItemClass).
    /// Adds a GUID and sends a spawn message to the remote peer.
    /// </summary>
    [HarmonyPatch(typeof(Player), "spawnDroppedInvItem", typeof(InvItemClass))]
    public static class PlayerDropItemPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player __instance, Transform __result)
        {
            if (__result == null) return;
            InvItemClass item = DroppedItemSyncHelpers.GetItemFromSpawned(__result);
            if (item == null) return;
            string prefab = __instance.inWater ? "Items/DroppedItem_water" : "Items/DroppedItem";
            DroppedItemSyncHelpers.SendDrop(__result, item, prefab);
        }
    }

    /// <summary>
    /// Intercepts the alternate drop path — Player.spawnDroppedInvItemm(bool, string, int).
    /// </summary>
    [HarmonyPatch(typeof(Player), "spawnDroppedInvItemm", typeof(bool), typeof(string), typeof(int))]
    public static class PlayerDropItemmPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player __instance, Transform __result)
        {
            if (__result == null) return;
            InvItemClass item = DroppedItemSyncHelpers.GetItemFromSpawned(__result);
            if (item == null) return;
            string prefab = __instance.inWater ? "Items/DroppedItem_water" : "Items/DroppedItem";
            DroppedItemSyncHelpers.SendDrop(__result, item, prefab);
        }
    }

    /// <summary>
    /// When a player picks up a networked dropped item, notify the remote peer
    /// to destroy its copy.
    /// </summary>
    [HarmonyPatch(typeof(Item), "getDroppedItem")]
    public static class PlayerPickupDroppedItemPatch
    {
        private struct PrefixState
        {
            public bool BeganTrapGuard;
            public bool BeganWireGuard;
            public bool DeferredWorldClaim;
            public bool DeferredGuidClaim;
            public string Guid;
            public Vector3 Pos;
            public string SendName;
            public string ItemType;
            public string RecipeFor;
            public int Amount;
            public float Durability;
            public int Ammo;
            public int PreCount;
        }

        [HarmonyPrefix]
        private static bool Prefix(Item __instance, ref PrefixState __state)
        {
            __state = default;
            try
            {
                ModRuntime.LegacyInfo("[PickupPrefix] getDroppedItem called on " + __instance.name);

                // Co-op rescue: sprung beartrap becomes isDroppedItem; picking it up must free
                // anyone still stuck (local here + peer DestroyObjectByPos) and grant loot.
                bool isTrap = false;
                if (ModRuntime.Network != null && ModRuntime.Network.IsConnected && __instance != null)
                {
                    GameObject go = __instance.gameObject;
                    string name = go != null && go.name != null ? go.name.ToLowerInvariant() : "";
                    string itemName = __instance.name != null ? __instance.name.ToLowerInvariant() : "";
                    isTrap = TrapNetworkId.IsWorldTrap(go) || TrapNetworkId.IsOccupancyTrap(go)
                        || TrapNameHelper.IsTrap(name) || TrapNameHelper.IsTrap(itemName);
                    if (isTrap)
                    {
                        Vector3 pos = __instance.transform.position;
                        WorldPhysicsSyncService.ReleaseLocalBearTrapIfNear(pos);
                        // Mark so container RemoveItem for the trap's junk slot is not sent —
                        // host would miss the inventory (already destroying) and deny/refund.
                        TrapPickupGuard.Begin(__instance);
                        __state.BeganTrapGuard = true;
                    }
                }

                // Already taken by peer (or us) — destroy ghost, do not grant item again.
                var ident = __instance.GetComponent<DroppedItemIdentifier>();
                bool hasGuid = ident != null && !string.IsNullOrEmpty(ident.Id);
                if (hasGuid && LanNetworkManager.IsDropGuidConsumed(ident.Id))
                {
                    ModRuntime.LegacyInfo("[PickupPrefix] guid already consumed: " + ident.Id);
                    UnityEngine.Object.Destroy(__instance.gameObject);
                    if (__state.BeganTrapGuard)
                        TrapPickupGuard.End(__instance);
                    return false;
                }

                // GUID drop: host-auth claim after successful transfer (mirror world unique).
                if (hasGuid
                    && ModRuntime.Network != null && ModRuntime.Network.IsConnected)
                {
                    DroppedItemSyncHelpers.CaptureWorldPickupItemMeta(__instance,
                        out string gType, out int gAmt, out float gDur, out int gAmmo,
                        out string gRecipeFor);
                    __state.DeferredGuidClaim = true;
                    __state.Guid = ident.Id;
                    __state.ItemType = gType;
                    __state.RecipeFor = gRecipeFor;
                    __state.Amount = gAmt;
                    __state.Durability = gDur;
                    __state.Ammo = gAmmo;
                    __state.PreCount = string.IsNullOrEmpty(gType)
                        ? -1
                        : CountForClaim(gType, gRecipeFor);
                    WorldPickupWireGuard.Begin();
                    __state.BeganWireGuard = true;
                    return true; // no wire yet — Postfix after successful destroy
                }

                // Non-GUID world unique / placed pickup: session claim before grant.
                if (!hasGuid)
                {
                    DroppedItemSyncHelpers.ResolveWorldPickupClaim(__instance,
                        out Vector3 cPos, out string cName, out bool cTrap);
                    isTrap = isTrap || cTrap;
                    if (!isTrap
                        && WorldPhysicsSyncService.IsWorldPickupConsumed(cPos.x, cPos.y, cPos.z, cName))
                    {
                        ModRuntime.LegacyInfo("[PickupPrefix] world pickup already consumed: "
                            + cName + " at " + cPos);
                        UnityEngine.Object.Destroy(__instance.gameObject);
                        if (__state.BeganTrapGuard)
                            TrapPickupGuard.End(__instance);
                        return false;
                    }

                    if (!isTrap
                        && ModRuntime.Network != null && ModRuntime.Network.IsConnected)
                    {
                        // Capture meta before transfer empties the slot; claim after success.
                        DroppedItemSyncHelpers.CaptureWorldPickupItemMeta(__instance,
                            out string itemType, out int amount, out float dur, out int ammo,
                            out string recipeFor);
                        __state.DeferredWorldClaim = true;
                        __state.Pos = cPos;
                        __state.SendName = cName;
                        __state.ItemType = itemType;
                        __state.RecipeFor = recipeFor;
                        __state.Amount = amount;
                        __state.Durability = dur;
                        __state.Ammo = ammo;
                        __state.PreCount = string.IsNullOrEmpty(itemType)
                            ? -1
                            : CountForClaim(itemType, recipeFor);
                        WorldPickupWireGuard.Begin();
                        __state.BeganWireGuard = true;
                        return true; // no wire yet — Postfix after successful destroy
                    }
                }

                WorldPickupWireGuard.Begin();
                __state.BeganWireGuard = true;
                DroppedItemSyncHelpers.SendPickup(__instance);
                return true;
            }
            catch
            {
                // Prefix must leave the guard up through the original + Postfix on
                // success; only clear here if we throw before Postfix can End.
                if (__state.BeganTrapGuard)
                    TrapPickupGuard.End(__instance);
                if (__state.BeganWireGuard)
                    WorldPickupWireGuard.End();
                throw;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(Item __instance, PrefixState __state)
        {
            // TrapPickupGuard / WorldPickupWireGuard cleared in Finalizer
            // (covers throw before/during Postfix).

            if (!__state.DeferredGuidClaim && !__state.DeferredWorldClaim)
                return;

            // Vanilla destroys only when slots[0].transferItemAllToPlayer() returned true — and
            // Object.Destroy is deferred, so the item is still alive here. Every true path
            // empties slot 0 (removeAmount → clear); a failed / partial transfer leaves it filled.
            if (string.IsNullOrEmpty(__state.ItemType) || !TransferredAll(__instance))
                return;

            if (__state.DeferredGuidClaim)
            {
                DroppedItemSyncHelpers.FinishGuidPickupClaim(
                    __state.Guid, __state.ItemType, __state.Amount,
                    __state.Durability, __state.Ammo, __state.PreCount, __state.RecipeFor);
                return;
            }

            if (!__state.DeferredWorldClaim)
                return;

            DroppedItemSyncHelpers.FinishWorldPickupClaim(
                __state.Pos, __state.SendName, __state.ItemType, __state.Amount,
                __state.Durability, __state.Ammo, __state.PreCount, __state.RecipeFor);
        }

        /// <summary>True when the dropped item's single slot was fully moved into the player's bags.</summary>
        private static bool TransferredAll(Item item)
        {
            if (item == null) return true; // already destroyed by something that took it
            Inventory bag = item.GetComponent<Inventory>();
            if (bag == null || bag.slots == null || bag.slots.Count == 0) return true;
            return InvItemClass.isNull(bag.slots[0].invItem);
        }

        /// <summary>Bag count the pickup's refund compares against (recipe-aware).</summary>
        private static int CountForClaim(string itemType, string recipeFor)
            => string.IsNullOrEmpty(recipeFor)
                ? ContainerSyncHelpers.CountPlayerItem(itemType, false)
                : ContainerSyncHelpers.CountPlayerItem(recipeFor, true);

        // Finalizer (not Postfix): getDroppedItem throw after Prefix Begin leaves
        // TrapPickupGuard / WorldPickupWireGuard sticky → RemoveItem suppress for that
        // trap inv and WorldObjectRemoved mute forever.
        [HarmonyFinalizer]
        private static void Finalizer(Item __instance, PrefixState __state)
        {
            if (__state.BeganTrapGuard)
                TrapPickupGuard.End(__instance);
            if (__state.BeganWireGuard)
                WorldPickupWireGuard.End();
        }
    }

    /// <summary>
    /// While getDroppedItem runs, Object.Destroy must not fan WorldObjectRemoved —
    /// FinishWorldPickupClaim / SendPickup owns the wire (avoids Mode0 beat host-auth).
    /// </summary>
    internal static class WorldPickupWireGuard
    {
        private static int _depth; // process-scoped: call-scoped, unwound by its Finalizer/finally
        public static bool IsActive => _depth > 0;
        public static void Begin() => _depth++;
        public static void End()
        {
            if (_depth > 0) _depth--;
        }
    }

    /// <summary>
    /// While getDroppedItem runs on a sprung trap, suppress container RemoveItem sync
    /// for that inventory so the granted loot is not refunded by ContainerTakeDenied.
    /// </summary>
    internal static class TrapPickupGuard
    {
        private static int _depth; // process-scoped: call-scoped, unwound by its Finalizer/finally
        private static Inventory _inv; // process-scoped: call-scoped, unwound by its Finalizer/finally

        public static bool IsActive => _depth > 0;

        public static bool IsGuarded(Inventory inv)
        {
            // Exact inventory only — null _inv must not suppress RemoveItem for all containers.
            return _depth > 0 && inv != null && _inv != null && inv == _inv;
        }

        public static void Begin(Item item)
        {
            _depth++;
            if (item != null)
                _inv = item.GetComponent<Inventory>();
        }

        public static void End(Item item)
        {
            if (_depth > 0) _depth--;
            if (_depth <= 0)
            {
                _depth = 0;
                _inv = null;
            }
        }
    }

    /// <summary>
    /// Helper for identifying trap GameObjects by name.
    /// </summary>
    internal static class TrapNameHelper
    {
        public static bool IsTrap(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.Contains("trap") || name.Contains("bear") || name.Contains("snap") || name.Contains("animal");
        }
    }
}
