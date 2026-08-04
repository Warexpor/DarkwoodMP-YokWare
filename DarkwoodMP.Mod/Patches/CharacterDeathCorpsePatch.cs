using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client-only: replicates corpse-interaction setup that vanilla does in
    /// processAnims() → setDeathCollider()/destroyComponents() when the death
    /// animation ends. On the client, Character.Update is AI-suppressed so
    /// processAnims never runs — without this, corpses never become searchable.
    ///
    /// MUST NOT run on host: setting isActive=false makes Character.Update
    /// early-out before processAnims, freezing the death pose (host killer bug).
    /// Host keeps vanilla death anim → setDeathCollider at clip end.
    ///
    /// Item + isActive=false are deferred to TickClientCorpseSetup so death anim
    /// can play first (immediate isActive=false froze the pose mid-clip).
    /// </summary>
    [HarmonyPatch(typeof(Character), "die")]
    public static class CharacterDeathCorpsePatch
    {
        private static void Postfix(Character __instance)
        {
            // Player.die() is a separate method (not override), so this won't fire for players.
            if (__instance == null) return;

            var net = ModRuntime.Network;
            // Offline / host: vanilla processAnims owns death presentation + corpse.
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;

            ClientEntityInterpolationService.NoteClientDeathForCorpse(__instance);

            if (__instance.inventory != null)
                __instance.inventory.invType = Inventory.InvType.deathDrop;

            // Transfer NPC deathInventory (die2 path can miss when AI Update is blocked).
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
