using System.Collections;
using System.Collections.Generic;
using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A save written inside a dream (a join's world share used to force-save the host
    /// mid-dream) loads with the player standing on the dream's pad slot, the dream not resumed
    /// (only a save taken going in carries <c>wantToDream</c>) and the dream's inventory in the
    /// bags. Starting the next dream there threw in <c>Dreams.startDreaming</c>. After such a load
    /// the player is woken the way vanilla <c>Dreams</c> ends a dream: the pre-dream inventory,
    /// effects and time back, at the place the dream started.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Load))]
    public static class StrandedDreamSaveRepair
    {
        private const float PadHalfSlot = 12500f;

        private static void Postfix(ref IEnumerator __result) => __result = ThenRepair(__result);

        private static IEnumerator ThenRepair(IEnumerator load)
        {
            while (load.MoveNext())
                yield return load.Current;
            Repair();
        }

        private static void Repair()
        {
            Dreams dreams = Dreams.Instance;
            Player player = Player.Instance;
            OutsideLocations ol = Singleton<OutsideLocations>.Instance;
            if (dreams == null || player == null || ol == null || dreams.wantToDream || dreams.dreaming)
                return;
            // Outside locations (a cellar, a bunker) live on the same slots: those load as entered.
            if (ol.playerInOutsideLocation)
                return;
            Vector3 pos = player._transform.position;
            if (!InAnySlot(ol, pos) || InAnySlot(ol, dreams.positionCopy) || dreams.positionCopy == Vector3.zero)
                return;

            ModLog.Warn(LogCat.Dream, "[DreamRepair] save was taken inside a dream: player at " + pos
                + ", waking at " + dreams.positionCopy);
            player.stopWhatImDoing();
            player.Hotbar.clear();
            player.Inventory.clear();
            if (dreams.inventorySlotsCopy.Count > 0)
            {
                Inventory.moveSlots(dreams.inventorySlotsCopy, player.Inventory.slots);
                Inventory.moveSlots(dreams.hotbarSlotsCopy, player.Hotbar.slots);
                player.Hotbar.hide();
                player.Hotbar.show();
            }
            if (dreams.effectsCopy != null)
            {
                dreams.effectsCopy.loadValues(player.effects);
                dreams.effectsCopy.effects.Clear();
            }
            Singleton<Controller>.Instance.CurrentTime = (int)dreams.timeCopy;
            Singleton<Rain>.Instance?.unhide();
            player.teleportTo(dreams.positionCopy, Quaternion.Euler(90f, 0f, 0f));
            Singleton<WorldGrid>.Instance?.refreshPosition(player._transform.position, instant: true, force: true);
            player.whereAmI.checkWhereAmI();
            Singleton<Controller>.Instance.updateAmbientLight();
        }

        private static bool InAnySlot(OutsideLocations ol, Vector3 pos)
        {
            List<Vector3> slots = Traverse.Create(ol).Field("locationPositions").GetValue<List<Vector3>>();
            if (slots == null)
                return false;
            for (int i = 0; i < slots.Count; i++)
            {
                Vector3 d = pos - slots[i];
                if (Mathf.Abs(d.x) < PadHalfSlot && Mathf.Abs(d.z) < PadHalfSlot)
                    return true;
            }
            return false;
        }
    }
}
