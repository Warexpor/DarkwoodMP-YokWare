using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Replicates the corpse-interaction setup that vanilla does in
    /// processAnims() -> setDeathCollider()/destroyComponents() when the
    /// death animation clip is detected. On the client, processAnims() is
    /// blocked by the AI-disable Update patch, so this never runs — corpses
    /// never become searchable.
    ///
    /// By running immediately after die() (which already calls die2()), we
    /// ensure the Item component and deathDrop inventory type are present
    /// regardless of whether processAnims() ever fires.
    /// </summary>
    [HarmonyPatch(typeof(Character), "die")]
    public static class CharacterDeathCorpsePatch
    {
        private static void Postfix(Character __instance)
        {
            // Only needed when the local AI-skip patches block processAnims().
            // Player.die() is a separate method (not override), so this won't fire for players.
            if (__instance == null) return;

            // Equivalent of setDeathCollider() line 5451-5458:
            // Add Item component if missing (so the corpse can be searched).
            if (__instance.GetComponent<Item>() == null)
            {
                Item item = __instance.gameObject.AddComponent<Item>();
                item.name = __instance.name.ToLower() + "_corpse";
                if (__instance.searched)
                    item.searched = true;
            }

            // Equivalent of destroyComponents() line 5538-5541:
            // Mark the inventory as a death drop so it opens as a container.
            if (__instance.inventory != null)
                __instance.inventory.invType = Inventory.InvType.deathDrop;

            // Equivalent of destroyComponents2() line 5547:
            // Deactivate the character so its Update() returns early
            // (harmless on client where Update is blocked, but keeps state clean).
            __instance.isActive = false;

            // Transfer NPC deathInventory items into the corpse's inventory,
            // mirroring Character.die2() behavior. On clients where AI-skip
            // patches block die2()'s Update-driven effects, this ensures
            // the corpse has the correct death loot.
            NPC npc = __instance.GetComponent<NPC>();
            if (npc != null)
            {
                int chapter = Singleton<WorldGenerator>.Instance?.chapterID ?? 1;
                Inventory deathInv = chapter == 2 && npc.deathInventoryChapter2 != null
                    ? npc.deathInventoryChapter2 : npc.deathInventory;
                if (deathInv != null && __instance.inventory != null)
                {
                    for (int i = 0; i < deathInv.slots.Count && __instance.inventory.slots.Count < 64; i++)
                    {
                        var ds = deathInv.slots[i];
                        if (ds != null && !InvItemClass.isNull(ds.invItem))
                        {
                            __instance.inventory.addSlot();
                            var slot = __instance.inventory.getNextFreeSlot();
                            if (slot != null)
                            {
                                var item = slot.createItem(ds.invItem.type, ds.invItem.amount);
                                if (item != null)
                                {
                                    item.durability = ds.invItem.durability;
                                    item.ammo = ds.invItem.ammo;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
