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

        internal void HandleJournalBulkSync(JournalBulkSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;

            // Always queue while still on title or loading. The journal exists as a stub and
            // addJournalEntry NREs (user log: Journal.DMD addJournalEntry on title join).
            if (!LanNetworkManager.ClientCanApplyWorldBulk())
            {
                _pendingJournalBulk = msg;
                _hasPendingJournalBulk = true;
                ModLog.Event(LogCat.Session, "Journal bulk queued until client is in-world");
                return;
            }

            try
            {
                Journal journal = Singleton<UI>.Instance?.journal;
                if (journal == null || journal.notesDict == null || journal.keysDict == null
                    || journal.itemsDict == null || journal.journalEntriesDict == null)
                {
                    _pendingJournalBulk = msg;
                    _hasPendingJournalBulk = true;
                    return;
                }

                ApplyJournalBulkSync(msg);
            }
            catch (Exception ex)
            {
                _pendingJournalBulk = msg;
                _hasPendingJournalBulk = true;
                ModLog.Warn(LogCat.Session, "Journal bulk deferred after error: " + ex.Message);
            }
        }

        internal void ApplyJournalBulkSync(JournalBulkSyncMessage msg)
        {
            if (!LanNetworkManager.ClientCanApplyWorldBulk())
                return;

            Journal journal = Singleton<UI>.Instance?.journal;
            if (journal == null || journal.notesDict == null || journal.keysDict == null
                || journal.itemsDict == null || journal.journalEntriesDict == null)
                return;

            if (msg.NoteTypes != null)
            {
                for (int i = 0; i < msg.NoteTypes.Length; i++)
                {
                    string type = msg.NoteTypes[i];
                    if (string.IsNullOrEmpty(type) || journal.notesDict.ContainsKey(type)) continue;
                    Journal.Note note = new Journal.Note();
                    note.type = type;
                    note.timePickedUp = Singleton<Controller>.Instance != null
                        ? Singleton<Controller>.Instance.CurrentTime : 0;
                    journal.notesDict.Add(type, note);
                }
            }

            if (msg.KeyTypes != null)
            {
                for (int i = 0; i < msg.KeyTypes.Length; i++)
                {
                    string type = msg.KeyTypes[i];
                    if (string.IsNullOrEmpty(type) || journal.keysDict.ContainsKey(type)) continue;
                    Journal.Key key = new Journal.Key();
                    key.type = type;
                    journal.keysDict.Add(type, key);
                }
            }

            if (msg.QuestItemTypes != null)
            {
                for (int i = 0; i < msg.QuestItemTypes.Length; i++)
                {
                    string type = msg.QuestItemTypes[i];
                    if (string.IsNullOrEmpty(type) || journal.itemsDict.ContainsKey(type)) continue;
                    Journal.Item item = new Journal.Item();
                    item.type = type;
                    journal.itemsDict.Add(type, item);
                }
            }

            if (msg.JournalEntryTypes != null)
            {
                for (int i = 0; i < msg.JournalEntryTypes.Length; i++)
                {
                    string type = msg.JournalEntryTypes[i];
                    if (string.IsNullOrEmpty(type)) continue;
                    if (journal.journalEntriesDict.ContainsKey(type)) continue;
                    try
                    {
                        // Needs the full in-game UI and Controller; never call it on the title screen.
                        journal.addJournalEntry(type, noPopup: true);
                    }
                    catch (Exception ex)
                    {
                        ModLog.Warn(LogCat.Session,
                            "addJournalEntry skipped for '" + type + "': " + ex.Message);
                    }
                }
            }

            // Late join: remove world pickups already claimed by the host journal.
            _needsJournalWorldCleanup = true;
            TryJournalWorldCleanup();
            ModRuntime.LegacyInfo(
                $"[BulkSync] Journal applied notes={msg.NoteTypes?.Length ?? 0} keys={msg.KeyTypes?.Length ?? 0} " +
                $"quest={msg.QuestItemTypes?.Length ?? 0} entries={msg.JournalEntryTypes?.Length ?? 0}");
        }

        /// <summary>
        /// Apply journal bulk queued while on title, and despawn collected world pickups
        /// once the client scene has spawned them.
        /// </summary>
        internal void TryFlushPendingJournal()
        {
            if (_net.Role != NetworkRole.Client)
                return;

            if (_hasPendingJournalBulk && LanNetworkManager.ClientCanApplyWorldBulk()
                && Singleton<UI>.Instance?.journal != null)
            {
                var pending = _pendingJournalBulk;
                try
                {
                    _hasPendingJournalBulk = false;
                    _pendingJournalBulk = default;
                    ApplyJournalBulkSync(pending);
                    // If still not actually applied (gate / null), re-queue
                    if (!LanNetworkManager.ClientCanApplyWorldBulk())
                    {
                        _pendingJournalBulk = pending;
                        _hasPendingJournalBulk = true;
                    }
                }
                catch (Exception ex)
                {
                    _pendingJournalBulk = pending;
                    _hasPendingJournalBulk = true;
                    ModLog.Warn(LogCat.Session, "Journal flush retry later: " + ex.Message);
                }
            }

            if (_needsJournalWorldCleanup && LanNetworkManager.ClientCanApplyWorldBulk())
                TryJournalWorldCleanup();
        }

        /// <summary>
        /// Destroy ground notes/keys/quest items already present in the local journal
        /// so late joiners do not see (or re-pick) claimed pickups.
        /// </summary>
        internal void TryJournalWorldCleanup()
        {
            Journal journal = Singleton<UI>.Instance?.journal;
            if (journal == null) return;

            // Wait until a world context exists; menu has no pickups to clean.
            if (Player.Instance == null && Singleton<Controller>.Instance == null)
                return;

            if (journal.notesDict != null)
            {
                foreach (var type in journal.notesDict.Keys)
                    JournalNetHandlers.DestroyWorldJournalObject(JournalItemKind.Note, type);
            }
            if (journal.keysDict != null)
            {
                foreach (var type in journal.keysDict.Keys)
                    JournalNetHandlers.DestroyWorldJournalObject(JournalItemKind.Key, type);
            }
            if (journal.itemsDict != null)
            {
                foreach (var type in journal.itemsDict.Keys)
                    JournalNetHandlers.DestroyWorldJournalObject(JournalItemKind.QuestItem, type);
            }

            // One pass after Player/Controller exists is enough for currently loaded
            // locations; live JournalItem messages cover further pickups. Objects in
            // not-yet-streamed chunks are cleaned when the peer re-interacts or when
            // a later live message arrives. This is rare for same-session late join.
            _needsJournalWorldCleanup = false;
        }

        internal void SendJournalBulkSync() => SendJournalBulkSyncTo(-1);

        internal void SendJournalBulkSyncTo(int targetPlayerId)
        {
            Journal journal = Singleton<UI>.Instance?.journal;
            if (journal == null || journal.notesDict == null || journal.keysDict == null
                || journal.itemsDict == null || journal.journalEntriesDict == null)
                return;

            var msg = new JournalBulkSyncMessage();

            var notes = journal.notesDict.Keys;
            msg.NoteTypes = new string[notes.Count];
            int idx = 0;
            foreach (var key in notes)
                msg.NoteTypes[idx++] = key;

            var keys = journal.keysDict.Keys;
            msg.KeyTypes = new string[keys.Count];
            idx = 0;
            foreach (var key in keys)
                msg.KeyTypes[idx++] = key;

            var questItems = journal.itemsDict.Keys;
            msg.QuestItemTypes = new string[questItems.Count];
            idx = 0;
            foreach (var key in questItems)
                msg.QuestItemTypes[idx++] = key;

            var journalEntries = journal.journalEntriesDict.Keys;
            msg.JournalEntryTypes = new string[journalEntries.Count];
            idx = 0;
            foreach (var key in journalEntries)
                msg.JournalEntryTypes[idx++] = key;

            _net.SendBulkOrAll(NetMessageType.JournalBulkSync, w => msg.Serialize(w), targetPlayerId);
        }



        /// <summary>
        /// Peer started/finished vaulting (jumpThroughWindow). Only that player's
        /// The proxy ignores Jumpable collisions, unlike every remote body.
        /// PlayerState JumpWindow clip also disables that proxy's colliders.
        /// </summary>
        private static readonly Collider[] VaultJumpableBuf = new Collider[48];
        /// <summary>
        /// Vault windows sit next to the body; a map-wide OverlapSphere was an
        /// alloc/FPS cliff on every VaultState. Interest is local to the vault.
        /// </summary>
        private const float VaultJumpableRadius = 16f;

        internal void HandleVaultState(VaultStateMessage msg)
        {
            int playerId = msg.PlayerId > 0 ? msg.PlayerId : _net.CurrentReceivePlayerId;
            if (playerId <= 0)
                return;

            RemotePlayerProxy proxy = _net.GetProxy(playerId);
            if (proxy == null)
            {
                _net.EnsureRemoteProxy(playerId);
                proxy = _net.GetProxy(playerId);
            }
            if (proxy == null)
                return;

            int jumpableLayer = LayerMask.NameToLayer("Jumpable");
            int layerMask = jumpableLayer >= 0 ? (1 << jumpableLayer) : 16384;

            var proxyCols = proxy.CachedColliders;
            if (proxyCols == null || proxyCols.Length == 0)
                proxyCols = proxy.GetComponentsInChildren<Collider>(true);
            int n = Physics.OverlapSphereNonAlloc(
                proxy.transform.position, VaultJumpableRadius, VaultJumpableBuf, layerMask);
            for (int p = 0; p < proxyCols.Length; p++)
            {
                Collider proxyCol = proxyCols[p];
                if (proxyCol == null) continue;
                for (int j = 0; j < n; j++)
                {
                    Collider jumpable = VaultJumpableBuf[j];
                    if (jumpable == null) continue;
                    Physics.IgnoreCollision(proxyCol, jumpable, msg.IsVaulting);
                }
            }
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Vault] player {playerId} Jumpable collision {(msg.IsVaulting ? "ignored" : "restored")} near={n}");
        }

        /// <summary>Broadcast the host's current weather (rain/fog/lightning) state to all clients.</summary>
    
    }
}
