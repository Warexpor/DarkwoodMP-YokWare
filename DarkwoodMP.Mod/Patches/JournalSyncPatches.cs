using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Shared helper methods for journal item synchronization (notes,
    /// keys, quest items, journal entries) between host and clients.
    /// </summary>
    internal static class JournalSyncHelpers
    {
        internal static void SendJournalItem(JournalItemKind kind, string type)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (string.IsNullOrEmpty(type)) return;
            var msg = new JournalItemMessage { Kind = kind, Type = type };
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            // Broadcast: host → all peers (Send is first-peer-only and breaks 3+).
            // Client → host only; host Forwardable rebroadcasts to the rest.
            net.Broadcast(NetMessageType.JournalItem, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>
    /// Syncs journal note pickups to connected clients.
    /// </summary>
    [HarmonyPatch(typeof(JournalNoteReference), "pickup")]
    public static class JournalNotePickupPatch
    {
        private static void Postfix(JournalNoteReference __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (Singleton<JournalDatabase>.Instance == null) return;
            JournalNote.Note note = Singleton<JournalDatabase>.Instance.getNote(__instance.noteName);
            if (note == null || string.IsNullOrEmpty(note.type)) return;
            JournalSyncHelpers.SendJournalItem(JournalItemKind.Note, note.type);
        }
    }

    /// <summary>
    /// Syncs key pickups to connected clients.
    /// </summary>
    [HarmonyPatch(typeof(KeyReference), "pickup")]
    public static class JournalKeyPickupPatch
    {
        private static void Postfix(KeyReference __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            JournalSyncHelpers.SendJournalItem(JournalItemKind.Key, __instance.type);
        }
    }

    /// <summary>
    /// Syncs quest item pickups to connected clients.
    /// </summary>
    [HarmonyPatch(typeof(QuestItemReference), "pickup")]
    public static class JournalQuestItemPickupPatch
    {
        private static void Postfix(QuestItemReference __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            JournalSyncHelpers.SendJournalItem(JournalItemKind.QuestItem, __instance.type);
        }
    }

    /// <summary>
    /// Syncs journal entry additions to connected clients (e.g. story
    /// progression entries).
    /// </summary>
    [HarmonyPatch(typeof(Journal), "addJournalEntry", new[] { typeof(string), typeof(bool) })]
    public static class JournalEntryPatch
    {
        private static void Postfix(object[] __args)
        {
            string type = (string)__args[0];
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            JournalSyncHelpers.SendJournalItem(JournalItemKind.JournalEntry, type);
        }
    }

    /// <summary>
    /// Syncs workbench upgrade level after a craft that advances it.
    /// Absolute level via Broadcast (3+) + Forwardable from clients.
    /// </summary>
    [HarmonyPatch(typeof(CraftingRecipes), "doCraft")]
    public static class WorkbenchUpgradePatch
    {
        private static int _levelBeforeCraft = -1;

        private static void Prefix()
        {
            _levelBeforeCraft = Singleton<Controller>.Instance != null
                ? Singleton<Controller>.Instance.workbenchLevel
                : -1;
        }

        private static void Postfix()
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (Singleton<Controller>.Instance == null) return;

            int level = Singleton<Controller>.Instance.workbenchLevel;
            // Only emit when upgrade craft actually advanced the shared workbench.
            if (_levelBeforeCraft >= 0 && level == _levelBeforeCraft)
                return;

            var msg = new WorkbenchLevelMessage { Level = level };
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            ModRuntime.LegacyInfo($"[Workbench] level { _levelBeforeCraft } → {level} (broadcast)");
            net.Broadcast(NetMessageType.WorkbenchLevel, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }
    }
}
