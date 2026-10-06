using System.Collections.Generic;
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
    /// A client's talk asks the host before it goes on to such a board (<see cref="NetMessageType.DialogHandInClaim"/>):
    /// the client runs its own boards (and gets their personal reward, the Wolf's pistol for the egg)
    /// before the host replays them, so two hand-ins within one round trip used to reward both
    /// players. The host grants the first claim and holds the item for that player until the talk
    /// hands it over, ends (the client releases it) or the hold runs out; anyone else is refused.
    /// </summary>
    internal static class DialogHandInArbiter
    {
        internal const string GoneText = "Someone already handed that over.";

        /// <summary>How long the host holds a granted item for a talk that never reports back.</summary>
        private const float HoldSec = 20f;
        /// <summary>A claim the host has not answered in this long is asked again on the next board.</summary>
        private const float ClaimRetrySec = 5f;

        private struct Hold
        {
            public int PlayerId;
            public float Until;
        }

        private static float _lastNoticeAt; // reset-in: Reset
        private static int _lastNoticeTo; // reset-in: Reset
        /// <summary>Host: item type → the player a granted claim holds it for.</summary>
        private static readonly Dictionary<string, Hold> _holds = new Dictionary<string, Hold>(); // reset-in: Reset
        /// <summary>Client: the claim on its way (talk key, id, when sent, items).</summary>
        private static string _pendingKey; // reset-in: Reset
        private static int _pendingId; // reset-in: Reset
        private static float _pendingAt; // reset-in: Reset
        private static int _nextClaimId; // reset-in: Reset
        /// <summary>Client: the talk the host granted, and the items it may hand over.</summary>
        private static string _grantedKey; // reset-in: Reset
        private static string[] _grantedTypes; // reset-in: Reset

        public static void Reset()
        {
            _lastNoticeAt = 0f;
            _lastNoticeTo = 0;
            _holds.Clear();
            _pendingKey = null;
            _pendingId = 0;
            _pendingAt = 0f;
            _nextClaimId = 0;
            _grantedKey = null;
            _grantedTypes = null;
        }

        /// <summary>
        /// The journal item the next board that takes one would remove, when it is no longer there
        /// (null: nothing to hand in, or it is still held). A board removing several variants
        /// (the Wolf takes every version of the church box) is fine while any of them is held.
        /// </summary>
        internal static string GoneHandIn(DialogueWindow dw, bool countLocalBag)
        {
            List<string> types = HandInTypes(dw, countLocalBag, out bool held);
            return types == null || held ? null : types[0];
        }

        /// <summary>
        /// The shared journal items the next board that takes one would remove (null: none), and
        /// whether any of them is still held.
        /// </summary>
        private static List<string> HandInTypes(DialogueWindow dw, bool countLocalBag, out bool anyHeld)
        {
            anyHeld = false;
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
                List<string> types = null;
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
                    if (types == null)
                        types = new List<string>();
                    if (!types.Contains(o.Value))
                        types.Add(o.Value);
                    if (Held(o.Value, countLocalBag))
                        anyHeld = true;
                }
                if (types != null)
                    return types;
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
        /// is refused and the peer told; the local speaker goes back to the NPC's main options. A
        /// client's talk waits for the host's grant before it goes on.
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
                int actor = GeFireActorContext.PeekOr(net.CurrentReceivePlayerId);
                List<string> types = HandInTypes(dw, countLocalBag: false, out bool held);
                if (types == null)
                    return true;
                if (held && !HeldForOther(types, actor))
                    return true;
                string npcName = dw.npc != null ? dw.npc.name : "";
                ModLog.Event(LogCat.World,
                    $"[HandIn] refused p{actor}'s '{dw.currentDialogue?.fullName}' at {npcName}: {types[0]} already handed over");
                try { Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw); }
                catch { /* ignore */ }
                Notify(net, actor, npcName, types[0]);
                return false;
            }

            if (LanNetworkManager.IsApplyingRemoteState || Player.Instance == null || !Player.Instance.inDialogue)
                return true;
            List<string> mine = HandInTypes(dw, countLocalBag: true, out bool mineHeld);
            if (mine == null)
                return true;
            bool gone = !mineHeld || (net.Role == NetworkRole.Host && HeldForOther(mine, net.LocalPlayerId));
            if (gone)
            {
                ModLog.Event(LogCat.World,
                    $"[HandIn] '{dw.currentDialogue?.fullName}' at {(dw.npc != null ? dw.npc.name : "")}: {mine[0]} is gone, back to the options");
                BackToOptions(dw, GoneText);
                return false;
            }
            if (net.Role != NetworkRole.Client)
                return true;
            return ClientClaim(net, dw, mine);
        }

        private static string TalkKey(DialogueWindow dw)
            => (dw.npc != null ? dw.npc.name : "") + "|" + (dw.currentDialogue != null ? dw.currentDialogue.fullName : "");

        /// <summary>Client: true once the host granted this talk the items; otherwise asks (once) and holds the board.</summary>
        private static bool ClientClaim(LanNetworkManager net, DialogueWindow dw, List<string> types)
        {
            string key = TalkKey(dw);
            if (key == _grantedKey)
                return true;
            float now = Time.unscaledTime;
            if (key == _pendingKey && now - _pendingAt < ClaimRetrySec)
                return false;
            _pendingKey = key;
            _pendingId = ++_nextClaimId;
            _pendingAt = now;
            var msg = new DialogHandInClaimMessage
            {
                Kind = DialogHandInClaimMessage.KindClaim,
                ClaimId = _pendingId,
                NpcName = dw.npc != null ? dw.npc.name : "",
                Types = types.ToArray()
            };
            net.Broadcast(NetMessageType.DialogHandInClaim, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.World, $"[HandIn] asking the host before handing {types[0]} to {msg.NpcName}");
            return false;
        }

        /// <summary>Host: one of the items is held by a granted claim of another player.</summary>
        private static bool HeldForOther(List<string> types, int playerId)
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < types.Count; i++)
            {
                if (_holds.TryGetValue(types[i], out Hold h) && h.PlayerId != playerId && now < h.Until)
                    return true;
            }
            return false;
        }

        /// <summary>Claim, grant, deny or release of a hand-in (<see cref="NetMessageType.DialogHandInClaim"/>).</summary>
        internal static void HandleClaim(LanNetworkManager net, DialogHandInClaimMessage msg)
        {
            if (net == null)
                return;
            if (net.Role == NetworkRole.Host)
            {
                int id = net.CurrentReceivePlayerId;
                if (id <= 0 || msg.Types == null)
                    return;
                var types = new List<string>(msg.Types);
                if (msg.Kind == DialogHandInClaimMessage.KindRelease)
                {
                    ReleaseHolds(id, types);
                    return;
                }
                if (msg.Kind != DialogHandInClaimMessage.KindClaim || types.Count == 0)
                    return;
                bool anyHeld = false;
                for (int i = 0; i < types.Count && !anyHeld; i++)
                    anyHeld = Held(types[i], countLocalBag: false);
                bool grant = anyHeld && !HeldForOther(types, id);
                if (grant)
                {
                    float until = Time.unscaledTime + HoldSec;
                    for (int i = 0; i < types.Count; i++)
                        _holds[types[i]] = new Hold { PlayerId = id, Until = until };
                }
                ModLog.Event(LogCat.World,
                    $"[HandIn] p{id} asks to hand {types[0]} to {msg.NpcName}: {(grant ? "granted" : "refused, already handed over")}");
                var reply = new DialogHandInClaimMessage
                {
                    Kind = grant ? DialogHandInClaimMessage.KindGrant : DialogHandInClaimMessage.KindDeny,
                    ClaimId = msg.ClaimId,
                    NpcName = msg.NpcName,
                    Types = msg.Types
                };
                net.SendToPlayer(id, NetMessageType.DialogHandInClaim, w => reply.Serialize(w), DeliveryMethod.ReliableOrdered);
                return;
            }

            if (net.Role != NetworkRole.Client || msg.ClaimId != _pendingId || _pendingKey == null)
                return;
            string key = _pendingKey;
            _pendingKey = null;
            DialogueWindow dw = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.dialogueWindow : null;
            bool sameTalk = dw != null && dw.opened && dw.displayingDialogue && dw.currentDialogue != null && TalkKey(dw) == key;
            if (msg.Kind == DialogHandInClaimMessage.KindDeny)
            {
                ModLog.Event(LogCat.World, $"[HandIn] host refused handing {FirstType(msg)} to {msg.NpcName}: someone else already did");
                if (sameTalk)
                    BackToOptions(dw, GoneText);
                else
                    Say(GoneText);
                return;
            }
            if (msg.Kind != DialogHandInClaimMessage.KindGrant)
                return;
            _grantedKey = key;
            _grantedTypes = msg.Types;
            if (!sameTalk)
                return; // the talk ended meanwhile: TickClient releases the hold
            try { Traverse.Create(dw).Method("displayNextBoard").GetValue(); }
            catch (System.Exception ex) { ModLog.Warn(LogCat.World, "[HandIn] resume after grant: " + ex.Message); }
        }

        private static string FirstType(DialogHandInClaimMessage msg)
            => msg.Types != null && msg.Types.Length > 0 ? msg.Types[0] : "";

        private static void ReleaseHolds(int playerId, List<string> types)
        {
            for (int i = 0; i < types.Count; i++)
            {
                if (_holds.TryGetValue(types[i], out Hold h) && h.PlayerId == playerId)
                    _holds.Remove(types[i]);
            }
        }

        /// <summary>Host: a peer left; its holds end with it.</summary>
        internal static void HostPeerLeft(int playerId)
        {
            List<string> drop = null;
            foreach (KeyValuePair<string, Hold> kv in _holds)
            {
                if (kv.Value.PlayerId == playerId)
                    (drop ??= new List<string>()).Add(kv.Key);
            }
            if (drop != null)
                foreach (string t in drop)
                    _holds.Remove(t);
        }

        /// <summary>
        /// Client: a granted talk that ended without handing the item over gives it back to the
        /// others at once instead of after the host's hold runs out.
        /// </summary>
        internal static void TickClient(LanNetworkManager net)
        {
            if (_grantedKey == null || net == null || net.Role != NetworkRole.Client)
                return;
            DialogueWindow dw = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.dialogueWindow : null;
            if (dw != null && dw.opened && dw.currentDialogue != null && TalkKey(dw) == _grantedKey)
                return;
            string[] types = _grantedTypes;
            _grantedKey = null;
            _grantedTypes = null;
            if (types == null || types.Length == 0)
                return;
            bool stillHeld = false;
            for (int i = 0; i < types.Length && !stillHeld; i++)
                stillHeld = Held(types[i], countLocalBag: false);
            if (!stillHeld)
                return; // handed over: the journal change reaches the host on its own
            var msg = new DialogHandInClaimMessage
            {
                Kind = DialogHandInClaimMessage.KindRelease,
                NpcName = "",
                Types = types
            };
            net.Broadcast(NetMessageType.DialogHandInClaim, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
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
                BackToOptions(dw, GoneText);
                return;
            }
            Say(GoneText);
        }

        private static void BackToOptions(DialogueWindow dw, string say)
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
            if (say != null)
                Say(say);
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
