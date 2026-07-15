using System.Collections.Generic;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// While host applies a remote peer's dialog outcome, suppress personal
    /// inventory/journal mutations on host Player.Instance (audit C2).
    /// World flags / events / NPC state still run through displayDialogue.
    /// Also snapshots/restores host journal personal dicts so removeItem journal
    /// branches (itemsDict/keysDict/notesDict.Remove) do not strip host keys.
    ///
    /// Vanilla displayNextBoard may call DialogueWindow.close (startDream / transport)
    /// and HandleDialogOutcome also closed after apply — that path always black-fades
    /// + Save(doJson) → SaveSync to all peers. Active guard = silent UI (no fade/save).
    /// </summary>
    public static class DialogHostApplyGuard
    {
        private static int _depth;

        // Personal journal snapshots (restored on End so host bag stays clean).
        private static Dictionary<string, Journal.Item> _snapItems;
        private static Dictionary<string, Journal.Key> _snapKeys;
        private static Dictionary<string, Journal.Note> _snapNotes;
        private static bool _hasJournalSnap;

        public static bool SuppressPersonalRewards => _depth > 0;

        /// <summary>True while host is applying a client's dialog outcome (no host UI session).</summary>
        public static bool Active => _depth > 0;

        public static void BeginWorldOnly()
        {
            _depth++;
            if (_depth == 1)
                SnapshotPersonalJournal();
        }

        public static void EndWorldOnly()
        {
            if (_depth == 1)
                RestorePersonalJournal();
            if (_depth > 0)
                _depth--;
        }

        public static void Reset()
        {
            _depth = 0;
            ClearSnap();
        }

        private static void SnapshotPersonalJournal()
        {
            ClearSnap();
            try
            {
                var journal = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.journal : null;
                if (journal == null) return;

                if (journal.itemsDict != null)
                    _snapItems = new Dictionary<string, Journal.Item>(journal.itemsDict);
                if (journal.keysDict != null)
                    _snapKeys = new Dictionary<string, Journal.Key>(journal.keysDict);
                if (journal.notesDict != null)
                    _snapNotes = new Dictionary<string, Journal.Note>(journal.notesDict);
                _hasJournalSnap = true;
            }
            catch
            {
                ClearSnap();
            }
        }

        private static void RestorePersonalJournal()
        {
            if (!_hasJournalSnap) return;
            try
            {
                var journal = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.journal : null;
                if (journal == null) return;

                // Re-add anything removeItem stripped from host journal during world-only apply.
                if (_snapItems != null && journal.itemsDict != null)
                {
                    foreach (var kvp in _snapItems)
                    {
                        if (!journal.itemsDict.ContainsKey(kvp.Key))
                            journal.itemsDict[kvp.Key] = kvp.Value;
                    }
                }
                if (_snapKeys != null && journal.keysDict != null)
                {
                    foreach (var kvp in _snapKeys)
                    {
                        if (!journal.keysDict.ContainsKey(kvp.Key))
                            journal.keysDict[kvp.Key] = kvp.Value;
                    }
                }
                if (_snapNotes != null && journal.notesDict != null)
                {
                    foreach (var kvp in _snapNotes)
                    {
                        if (!journal.notesDict.ContainsKey(kvp.Key))
                            journal.notesDict[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch
            {
                // ignore restore failures
            }
            finally
            {
                ClearSnap();
            }
        }

        private static void ClearSnap()
        {
            _snapItems = null;
            _snapKeys = null;
            _snapNotes = null;
            _hasJournalSnap = false;
        }
    }
}
