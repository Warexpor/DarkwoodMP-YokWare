using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Journal bulk / item / workbench handlers composed for 0.8.</summary>
    internal sealed partial class JournalNetHandlers
    {
        private readonly LanNetworkManager _net;

        private bool _hasPendingJournalBulk;
        private JournalBulkSyncMessage _pendingJournalBulk;
        private bool _needsJournalWorldCleanup;

        internal JournalNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingJournal()
        {
            _hasPendingJournalBulk = false;
            _pendingJournalBulk = default;
            _needsJournalWorldCleanup = false;
        }

        internal void HandleWorkbenchLevel(WorkbenchLevelMessage msg)
        {
            if (_net.Role == NetworkRole.Client)
                return;

            // Host rebroadcasts the authoritative level below; never relay the raw client value.
            _net.SuppressRelay();

            // A client sends this only after it paid for one upgrade (vanilla workbenchLevel++).
            // Ahead of the host: take it. Not ahead: someone upgraded the same level at the same
            // time, and both paid, so this one is the next level (taking the max lost a level).
            int current = Singleton<Controller>.Instance != null
                ? Singleton<Controller>.Instance.workbenchLevel : 0;
            int level = msg.Level > current ? msg.Level : current + 1;
            if (level != current)
                ApplyWorkbenchLevel(level);
            _net.BulkSyncHandlers.SendWorkbenchLevelSync();
        }

        internal void ApplyWorkbenchLevel(int level)
        {
            if (Singleton<Controller>.Instance == null) return;

            int prevLevel = Singleton<Controller>.Instance.workbenchLevel;
            if (prevLevel == level)
            {
                // Still refresh open UI in case recipes are stale.
            }
            else
            {
                Singleton<Controller>.Instance.workbenchLevel = level;
                ModRuntime.LegacyInfo($"[Workbench] Level synced from {prevLevel} to {level}");
            }

            // If the workbench inventory is currently open, refresh the display
            // so the player sees the updated level and recipes immediately.
            try
            {
                if (Player.Instance != null && Player.Instance.openedItemInventory != null)
                {
                    Workbench wb = Player.Instance.openedItemInventory.GetComponent<Workbench>();
                    if (wb == null)
                        wb = Player.Instance.openedItemInventory.transform.parent?.GetComponent<Workbench>();

                    if (wb != null)
                    {
                        wb.currentLevel = level;
                        wb.refreshWorkbenchUpgrade();
                        if (wb.workbenchInventory != null)
                            wb.workbenchInventory.refreshRecipes();
                    }
                }
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[Network] swallowed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Vanilla marks a page picked up or written while dreaming (<c>inDream</c>) and clears it from
        /// the journal when the dream ends. The sender says which kind its page is: a shared dream's
        /// page is a dream page here too (or it outlived the dream on every other peer), and a world
        /// page stays one even while this player is in a dream of its own (the host's prologue lost
        /// the pages a peer found meanwhile).
        /// </summary>
        private static bool PickedInDream(JournalItemMessage msg) => msg.InDream;

        internal void HandleJournalItem(JournalItemMessage msg)
        {
            Journal journal = Singleton<UI>.Instance?.journal;
            if (journal == null) return;
            // A page of a dream this player is not in (the sender's own): not this journal's.
            if (msg.InDream && !DreamSyncManager.IsDreamActive)
                return;

            switch (msg.Kind)
            {
                case JournalItemKind.Note:
                    if (!journal.notesDict.ContainsKey(msg.Type))
                    {
                        Journal.Note note = new Journal.Note();
                        note.type = msg.Type;
                        note.inDream = PickedInDream(msg);
                        note.timePickedUp = Singleton<Controller>.Instance != null
                            ? Singleton<Controller>.Instance.CurrentTime : 0;
                        journal.notesDict.Add(msg.Type, note);
                        journal.showJournalInfoPopup("Note", msg.Type);
                    }
                    break;
                case JournalItemKind.Key:
                    if (!journal.keysDict.ContainsKey(msg.Type))
                    {
                        Journal.Key key = new Journal.Key();
                        key.type = msg.Type;
                        key.inDream = PickedInDream(msg);
                        journal.keysDict.Add(msg.Type, key);
                        journal.showJournalInfoPopup("Key", msg.Type);
                    }
                    break;
                case JournalItemKind.QuestItem:
                    if (!journal.itemsDict.ContainsKey(msg.Type))
                    {
                        Journal.Item item = new Journal.Item();
                        item.type = msg.Type;
                        item.inDream = PickedInDream(msg);
                        journal.itemsDict.Add(msg.Type, item);
                        journal.showJournalInfoPopup("InvItem", msg.Type);
                    }
                    break;
                case JournalItemKind.JournalEntry:
                {
                    bool added = journal.journalEntriesDict != null && !journal.journalEntriesDict.ContainsKey(msg.Type);
                    journal.addJournalEntry(msg.Type, noPopup: false);
                    if (added && journal.journalEntriesDict.TryGetValue(msg.Type, out Journal.JournalEntry entry))
                        entry.inDream = msg.InDream;
                    break;
                }
                case JournalItemKind.Remove:
                    if (journal.keysDict != null && journal.keysDict.ContainsKey(msg.Type))
                        journal.keysDict.Remove(msg.Type);
                    if (journal.notesDict != null && journal.notesDict.ContainsKey(msg.Type))
                        journal.notesDict.Remove(msg.Type);
                    if (journal.itemsDict != null && journal.itemsDict.ContainsKey(msg.Type))
                        journal.itemsDict.Remove(msg.Type);
                    break;
                case JournalItemKind.Location:
                    // Journal Locations tab (Location.discoverMe). Map pins sync separately.
                    // No popup — discoverMe already showed on the discovering peer.
                    if (journal.locationsDict != null && !string.IsNullOrEmpty(msg.Type)
                        && !journal.locationsDict.ContainsKey(msg.Type))
                        journal.locationsDict.Add(msg.Type, msg.Type);
                    break;
                default:
                    ModRuntime.Log?.LogWarning($"[Journal] Unhandled JournalItemKind: {msg.Kind}");
                    break;
            }

            // World-object cleanup: destroy the physical journal object on this peer
            // so the world reflects that the item was already picked up.
            JournalNetHandlers.DestroyWorldJournalObject(msg.Kind, msg.Type);
        }

        /// <summary>
        /// Finds and destroys the physical world object (JournalNoteReference,
        /// KeyReference, or QuestItemReference) matching the given journal item,
        /// so the host's world reflects that the remote player already took it.
        /// Item path + InvItem-only path (keys/notes on InvItem without Item used to
        /// survive peer pickup and stay dual-pickable).
        /// </summary>
        internal static void DestroyWorldJournalObject(JournalItemKind kind, string type)
        {
            if (string.IsNullOrEmpty(type)) return;

            switch (kind)
            {
                case JournalItemKind.Note:
                    {
                        JournalNoteReference[] allNotes =
                            WorldQueryHelper.GetCachedSceneComponents<JournalNoteReference>();
                        for (int i = 0; i < allNotes.Length; i++)
                        {
                            if (allNotes[i] == null) continue;
                            if (allNotes[i].dontDestroy) continue;
                            var note = Singleton<JournalDatabase>.Instance?.getNote(allNotes[i].noteName);
                            if (note != null && note.type == type)
                                DestroyJournalWorldGo(allNotes[i]);
                        }
                        break;
                    }
                case JournalItemKind.Key:
                    {
                        KeyReference[] allKeys =
                            WorldQueryHelper.GetCachedSceneComponents<KeyReference>();
                        for (int i = 0; i < allKeys.Length; i++)
                        {
                            if (allKeys[i] != null && allKeys[i].type == type)
                                DestroyJournalWorldGo(allKeys[i]);
                        }
                        break;
                    }
                case JournalItemKind.QuestItem:
                    {
                        QuestItemReference[] allQuest =
                            WorldQueryHelper.GetCachedSceneComponents<QuestItemReference>();
                        for (int i = 0; i < allQuest.Length; i++)
                        {
                            if (allQuest[i] != null && allQuest[i].type == type)
                                DestroyJournalWorldGo(allQuest[i]);
                        }
                        break;
                    }
                case JournalItemKind.JournalEntry:
                    // Story journal entries have no world pickup object to despawn.
                    break;
                case JournalItemKind.Location:
                    // Journal location names have no world pickup object to despawn.
                    break;
                default:
                    // Avoid per-frame spam: log once per kind value.
                    ModRuntime.Log?.LogWarning($"[Journal] Unhandled destroy kind: {kind}");
                    break;
            }
        }

        /// <summary>
        /// Destroy a scene journal pickup GO. Prefer parent Item root so mesh+colliders go;
        /// InvItem-only scene keys/notes (no Item) destroy the reference GO. Scene-valid
        /// guard skips database prefabs (FindObjectsOfType never returns those anyway).
        /// </summary>
        internal static void DestroyJournalWorldGo(Component journalRef)
        {
            if (journalRef == null) return;
            GameObject go = journalRef.gameObject;
            if (go == null) return;
            try
            {
                if (!go.scene.IsValid() || !go.scene.isLoaded)
                    return;
            }
            catch { return; }

            Item item = journalRef.GetComponent<Item>() ?? journalRef.GetComponentInParent<Item>();
            GameObject target = item != null ? item.gameObject : go;
            try
            {
                UnityEngine.Object.Destroy(target);
            }
            catch { /* destroyed Unity object */ }
        }
    }
}
