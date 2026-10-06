using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// One shared journal item, two NPCs who want it. The sister's key goes to the Wolf or the
    /// Musician, the egg to the Wolf or Piotrek: whoever gets it decides the story. The journal is
    /// shared, so two players talking to the two NPCs at once both had the item on their list and
    /// both handed it over (both outcomes' flags and world events ran on the host).
    /// Vanilla only lists an item you hold; this keeps that true at the moment a board that takes
    /// it runs: on the speaker (back to the main options when it is gone) and on the host replaying
    /// a peer's board (refused, the speaker is told).
    /// </summary>
    internal static class DialogHandInArbiter
    {
        internal const string GoneText = "Someone already handed that over.";

        private static float _lastNoticeAt; // reset-in: Reset
        private static int _lastNoticeTo; // reset-in: Reset

        public static void Reset()
        {
            _lastNoticeAt = 0f;
            _lastNoticeTo = 0;
        }

        /// <summary>
        /// The journal item the next board that takes one would remove, when it is no longer there
        /// (null: nothing to hand in, or it is still held). A board removing several variants
        /// (the Wolf takes every version of the church box) is fine while any of them is held.
        /// </summary>
        internal static string GoneHandIn(DialogueWindow dw, bool countLocalBag)
        {
            CharacterDialogue.Dialogue d = dw != null ? dw.currentDialogue : null;
            if (d == null || d.boards == null)
                return null;
            int next;
            try { next = Traverse.Create(dw).Field("currentBoard").GetValue<int>() + 1; }
            catch { return null; }

            for (int b = Mathf.Max(0, next); b < d.boards.Count; b++)
            {
                CharacterDialogue.Dialogue.Board board = d.boards[b];
                if (board == null || !board.hasOutcome || board.outcomes == null)
                    continue;
                string firstGone = null;
                bool anyJournal = false;
                for (int i = 0; i < board.outcomes.Count; i++)
                {
                    CharacterDialogue.Dialogue.Board.Outcome o = board.outcomes[i];
                    if (o == null || o.type != CharacterDialogue.Dialogue.Board.Outcome.Type.removeItem
                        || string.IsNullOrEmpty(o.Value))
                        continue;
                    // Vanilla runs an outcome once per node unless it is multipleFire.
                    if (d.alreadyShown && !o.multipleFire)
                        continue;
                    if (!IsJournalItem(o.Value) || !RequirementsMet(o))
                        continue;
                    anyJournal = true;
                    if (Held(o.Value, countLocalBag))
                        return null;
                    if (firstGone == null)
                        firstGone = o.Value;
                }
                if (anyJournal)
                    return firstGone;
            }
            return null;
        }

        private static bool RequirementsMet(CharacterDialogue.Dialogue.Board.Outcome o)
        {
            try { return o.requirementsMet(); }
            catch { return true; }
        }

        private static bool IsJournalItem(string type)
        {
            JournalDatabase db = Singleton<JournalDatabase>.Instance;
            if (db == null)
                return false;
            try
            {
                return db.getKey(type) != null || db.getNote(type) != null || db.getItem(type) != null;
            }
            catch { return false; }
        }

        private static bool Held(string type, bool countLocalBag)
        {
            Journal j = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.journal : null;
            if (j != null && ((j.keysDict != null && j.keysDict.ContainsKey(type))
                || (j.notesDict != null && j.notesDict.ContainsKey(type))
                || (j.itemsDict != null && j.itemsDict.ContainsKey(type))))
                return true;
            // The speaker's own bag (vanilla removeItem looks there first); never the host's bag
            // for a peer's board.
            if (countLocalBag && Player.Instance != null && Player.Instance.Inventory != null)
            {
                try { return Player.Instance.Inventory.getItemInPlayer(type) != null; }
                catch { return false; }
            }
            return false;
        }

        /// <summary>
        /// displayNextBoard Prefix: false when the board must not run. A peer's board on the host
        /// is refused and the peer told; the local speaker goes back to the NPC's main options.
        /// </summary>
        internal static bool AllowBoard(DialogueWindow dw)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || dw == null)
                return true;

            if (DialogHostApplyGuard.DialogueApplyActive)
            {
                if (net.Role != NetworkRole.Host)
                    return true;
                string gone = GoneHandIn(dw, countLocalBag: false);
                if (gone == null)
                    return true;
                string npcName = dw.npc != null ? dw.npc.name : "";
                int actor = GeFireActorContext.PeekOr(net.CurrentReceivePlayerId);
                ModLog.Event(LogCat.World,
                    $"[HandIn] refused p{actor}'s '{dw.currentDialogue?.fullName}' at {npcName}: {gone} already handed over");
                try { Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw); }
                catch { /* ignore */ }
                Notify(net, actor, npcName, gone);
                return false;
            }

            if (LanNetworkManager.IsApplyingRemoteState || Player.Instance == null || !Player.Instance.inDialogue)
                return true;
            string mine = GoneHandIn(dw, countLocalBag: true);
            if (mine == null)
                return true;
            ModLog.Event(LogCat.World,
                $"[HandIn] '{dw.currentDialogue?.fullName}' at {(dw.npc != null ? dw.npc.name : "")}: {mine} is gone, back to the options");
            BackToOptions(dw);
            return false;
        }

        private static void Notify(LanNetworkManager net, int playerId, string npcName, string itemType)
        {
            if (playerId <= 0 || playerId == net.LocalPlayerId)
                return;
            // Every later board of the refused talk is refused too; one notice is enough.
            float now = Time.unscaledTime;
            if (playerId == _lastNoticeTo && now - _lastNoticeAt < 3f)
                return;
            _lastNoticeTo = playerId;
            _lastNoticeAt = now;
            var msg = new DialogHandInGoneMessage { NpcName = npcName ?? "", ItemType = itemType ?? "" };
            net.SendToPlayer(playerId, NetMessageType.DialogHandInGone, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Client: the host refused a hand-in this player made with that NPC.</summary>
        internal static void HandleOnClient(LanNetworkManager net, DialogHandInGoneMessage msg)
        {
            if (net == null || net.Role != NetworkRole.Client)
                return;
            ModLog.Event(LogCat.World, $"[HandIn] host refused handing {msg.ItemType} to {msg.NpcName}: someone else already did");
            DialogueWindow dw = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.dialogueWindow : null;
            if (dw != null && dw.opened && dw.npc != null && dw.npc.name == msg.NpcName && dw.displayingDialogue)
            {
                BackToOptions(dw);
                return;
            }
            Say(GoneText);
        }

        private static void BackToOptions(DialogueWindow dw)
        {
            try
            {
                dw.currentDialogue = null;
                dw.currentMenu = DialogueWindow.CurrentMenu.main;
                Traverse.Create(dw).Method("showMainOptions").GetValue();
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.World, "[HandIn] back to options: " + ex.Message);
                try { dw.close(); } catch { /* ignore */ }
            }
            Say(GoneText);
        }

        private static void Say(string text)
        {
            if (Player.Instance == null)
                return;
            Patches.PersonalFlavorHud.BeginBypass();
            try { Player.Instance.displayMessage(text); }
            catch { /* HUD mid-teardown */ }
            finally { Patches.PersonalFlavorHud.EndBypass(); }
        }
    }
}
