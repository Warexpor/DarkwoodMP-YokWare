using System.Collections;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A joiner's first steps into the hideout: the host runs the hideout's wake-up one-shot
    /// (<c>GainRecipes_med_cottage_tree_01</c>: recipes, the wooden chicken, journal entries
    /// act1_entry1/2/2a, "rise and shine") for it as its stand-in walks in, and the joiner replays
    /// it a moment before the host's journal reaches it. Its addJournalItem step then added pages
    /// the shared journal already holds, each with a "received" popup (and the chicken's note
    /// opened). While the client is still waiting for the host's journal
    /// (<see cref="JournalNetHandlers.QuietJoinCatchUp"/>) that step adds its pages quietly, as
    /// the journal bulk does; recipes and the message are not touched.
    /// </summary>
    [HarmonyPatch(typeof(GameEvent), nameof(GameEvent.fire))]
    public static class JoinJournalQuietPatch
    {
        private static bool Prefix(GameEvent __instance, ref IEnumerator __result)
        {
            if (__instance == null || __instance.type != GameEvent.Type.addJournalItem)
                return true;
            if (!JournalNetHandlers.QuietJoinCatchUp)
                return true;
            __result = QuietAdd(__instance);
            return false;
        }

        private static IEnumerator QuietAdd(GameEvent ge)
        {
            float wait = ge.delayIsInGameTimeUnits && Singleton<Controller>.Instance != null
                ? ge.delay * Singleton<Controller>.Instance.timeChangeInterval
                : ge.delay;
            yield return new WaitForSeconds(wait);
            if (ge.disabled || ge.prefabList == null || Player.Instance == null)
                yield break;
            Journal journal = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.journal : null;
            for (int i = 0; i < ge.prefabList.Count; i++)
            {
                GameObject go = ge.prefabList[i] as GameObject;
                if (go == null)
                    continue;
                InvItem inv = go.GetComponent<InvItem>();
                if (inv != null)
                    Player.Instance.Inventory.addJournalItem(inv.type, showImmediately: false, noPopup: true);
                JournalEntry entry = go.GetComponent<JournalEntry>();
                if (entry != null && journal != null)
                    journal.addJournalEntry(entry.name, noPopup: true);
            }
            ModRuntime.LegacyInfo("[Journal] joiner catching up: one-shot pages added quietly");
        }
    }
}
