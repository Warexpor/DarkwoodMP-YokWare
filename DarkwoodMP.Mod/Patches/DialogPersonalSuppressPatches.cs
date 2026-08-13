using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Audit C2: when host replays a client dialog node via displayDialogue,
    /// do not give/remove host bag items. Journal identity is shared world.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "addItemTypeToPlayer")]
    public static class DialogSuppressGiveItemPatch
    {
        private static bool Prefix()
        {
            if (!DialogHostApplyGuard.SuppressPersonalRewards)
                return true;
            if (!DialogApplyPolicy.ShouldSuppressPersonalInventoryMutation(true))
                return true;
            return false;
        }
    }

    /// <summary>
    /// removeItem path uses getItemInPlayer + removeAmount. Suppress removeAmount
    /// on host bag while applying remote dialog outcomes.
    /// </summary>
    [HarmonyPatch(typeof(InvItemClass), "removeAmount")]
    public static class DialogSuppressRemoveAmountPatch
    {
        private static bool Prefix()
        {
            if (!DialogHostApplyGuard.SuppressPersonalRewards)
                return true;
            return false;
        }
    }

    /// <summary>
    /// Host must not spam giveItem / reputation popup UI from remote dialog apply.
    /// InvItem and personal journal popups are suppressed; Reputation may still show
    /// (shared NPC state) — skip InvItem only to avoid host "got free item" feedback.
    /// </summary>
    [HarmonyPatch(typeof(Journal), "showJournalInfoPopup")]
    public static class DialogSuppressJournalPopupPatch
    {
        private static bool Prefix(string _type)
        {
            if (!DialogHostApplyGuard.SuppressPersonalRewards)
                return true;
            if (string.IsNullOrEmpty(_type))
                return false;
            // Personal give feedback — never on host for remote outcomes.
            if (_type == "InvItem" || _type == "QuestItem" || _type == "Note"
                || _type == "Key" || _type == "JournalEntry" || _type == "Reputation")
                return false;
            return true;
        }
    }
}
