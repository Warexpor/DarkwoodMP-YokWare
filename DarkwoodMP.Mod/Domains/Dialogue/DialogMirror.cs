using System.Collections;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Listening in on another player's dialogue. One player talks to an NPC (the dialogue lock
    /// keeps it to one); a second player who tries to talk to that NPC now joins as a listener
    /// instead of being turned away, and sees the talking player's dialogue window as it is: the
    /// same portrait (and its changes), the same lines writing out, the same options, the same
    /// highlighted choice. Only the talking player acts; the listener can leave with Esc. The
    /// trading screen is not shown (the listener keeps the portrait until the talk goes on).
    /// <para>
    /// The talking player reports every screen of its window to the host as built (each element's
    /// text, place, colour, sprite, typing speed), so the listener rebuilds exactly that, without
    /// evaluating anything itself: its own bag, flags or location never change what it sees, and
    /// nothing it watches runs an outcome on its side (choices reach the world from the talking
    /// player as before). The host keeps each talk's latest screen for a player joining midway.
    /// </para>
    /// </summary>
    internal static class DialogMirror
    {
        private const string PrefabRoot = "UI/Dialogue/";

        // ---- shared helpers -------------------------------------------------------------------

        private static readonly AccessTools.FieldRef<DialogueWindow, int> CurrentBoard =
            AccessTools.FieldRefAccess<DialogueWindow, int>("currentBoard");
        private static readonly AccessTools.FieldRef<DialogueWindow, float> BoardStartedAt =
            AccessTools.FieldRefAccess<DialogueWindow, float>("timeStartedDisplayingBoard");
        private static readonly AccessTools.FieldRef<WritingText, float> WriteSpeed =
            AccessTools.FieldRefAccess<WritingText, float>("writeSpeed");

        private static DialogueWindow Window => Singleton<UI>.Instance != null ? Singleton<UI>.Instance.dialogueWindow : null;

        internal static void Reset()
        {
            // Session end: a listener's window goes with it.
            if (SpectatorActive)
            {
                try { ForceCloseView(); }
                catch { /* UI mid-teardown */ }
            }
            SpectatorActive = false;
            Closing = false;
            Driving = false;
            _viewReady = false;
            _pendingJoin = false;
            DeniedNpc = null;
            _joinSentAt = 0f;
            _lastHighlightSent = -2;
            _npc = null;
            _npcRef = default;
            _ownerId = -1;
            _pendingScreen = null;
            _pendingDone = false;
            _pendingSelect = -1;
            _pendingPortrait = -1;
            _portraitOverride = -1;
            _renderFrame = -1;
            _sessions.Clear();
            _ownerOpen = false;
            _ownerDoneSent = false;
        }

        private static string KindName(byte kind)
        {
            switch (kind)
            {
                case DialogMirrorElement.KindDialogueOption: return "DialogueOption";
                case DialogMirrorElement.KindExclamationMark: return "ExclamationMark";
                case DialogMirrorElement.KindItemIcon: return "DialogueItemIcon";
                case DialogMirrorElement.KindShowItemBtn: return "ShowItemBtn";
                case DialogMirrorElement.KindDecisionBtn: return "DecisionBtn";
                case DialogMirrorElement.KindText: return "Text";
                case DialogMirrorElement.KindDescText: return "DescText";
                default: return null;
            }
        }

        private static int KindOf(string name)
        {
            switch (name)
            {
                case "DialogueOption": return DialogMirrorElement.KindDialogueOption;
                case "ExclamationMark": return DialogMirrorElement.KindExclamationMark;
                case "DialogueItemIcon": return DialogMirrorElement.KindItemIcon;
                case "ShowItemBtn": return DialogMirrorElement.KindShowItemBtn;
                case "DecisionBtn": return DialogMirrorElement.KindDecisionBtn;
                case "Text": return DialogMirrorElement.KindText;
                case "DescText": return DialogMirrorElement.KindDescText;
                default: return -1;
            }
        }

        private static void Stamp(ref DialogMirrorMessage m, NpcRef npc)
        {
            m.NpcName = npc.Name ?? "";
            m.HasPos = npc.HasPos;
            m.PosX = npc.Pos.x;
            m.PosY = npc.Pos.y;
            m.PosZ = npc.Pos.z;
            m.Dream = npc.Dream;
        }

        private static NpcRef RefOf(DialogMirrorMessage m) => new NpcRef
        {
            Name = m.NpcName,
            HasPos = m.HasPos,
            Pos = new Vector3(m.PosX, m.PosY, m.PosZ),
            Dream = m.Dream
        };

        // ---- talking player: report the window --------------------------------------------------

        private static bool _ownerOpen; // reset-in: Reset
        private static bool _ownerDoneSent; // reset-in: Reset

        /// <summary>This window is the local player's own conversation (not a replay, not a listener's view).</summary>
        internal static bool OwnerCapturing(DialogueWindow dw)
        {
            if (dw == null || dw.npc == null || SpectatorActive)
                return false;
            if (DialogHostApplyGuard.DialogueApplyActive || LanNetworkManager.IsApplyingRemoteState)
                return false;
            return NetGuard.Connected(out _);
        }

        private static void OwnerSend(DialogMirrorMessage m)
        {
            if (!NetGuard.Connected(out LanNetworkManager net))
                return;
            m.OwnerId = net.LocalPlayerId;
            if (net.Role == NetworkRole.Host)
                OnOwnerEvent(net, net.LocalPlayerId, m);
            else
                net.Send(NetMessageType.DialogMirror, w => m.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>initiateDialogue went ahead (the lock let this player talk).</summary>
        internal static void OwnerOpened(DialogueWindow dw)
        {
            if (!OwnerCapturing(dw))
                return;
            _ownerOpen = true;
            _ownerDoneSent = false;
            var m = new DialogMirrorMessage { Kind = DialogMirrorMessage.KindOpen, Index = (int)dw.npc.portraitType };
            Stamp(ref m, NpcRef.Of(dw.npc));
            OwnerSend(m);
        }

        internal static void OwnerClosed()
        {
            if (!_ownerOpen)
                return;
            _ownerOpen = false;
            OwnerSend(new DialogMirrorMessage { Kind = DialogMirrorMessage.KindClose });
        }

        internal static void OwnerSimple(DialogueWindow dw, byte kind, int index = 0, bool flag = false)
        {
            if (!_ownerOpen || !OwnerCapturing(dw))
                return;
            OwnerSend(new DialogMirrorMessage { Kind = kind, Index = index, Flag = flag });
        }

        /// <summary>The children a populate / board call is about to replace (Destroy is deferred).</summary>
        internal static HashSet<int> ChildIds(Transform container)
        {
            var set = new HashSet<int>();
            if (container == null)
                return set;
            for (int i = 0; i < container.childCount; i++)
                set.Add(container.GetChild(i).gameObject.GetInstanceID());
            return set;
        }

        /// <summary>The options or show-item list just built (children not in <paramref name="before"/>).</summary>
        internal static void OwnerPanel(DialogueWindow dw, byte kind, Transform container, HashSet<int> before)
        {
            if (!_ownerOpen || !OwnerCapturing(dw) || container == null)
                return;
            _ownerDoneSent = false;
            _lastHighlightSent = -2;
            var list = new List<DialogMirrorElement>(8);
            for (int i = 0; i < container.childCount && list.Count < DialogMirrorMessage.MaxElements; i++)
            {
                Transform t = container.GetChild(i);
                if (before != null && before.Contains(t.gameObject.GetInstanceID()))
                    continue;
                if (TryCapture(dw, t, out DialogMirrorElement e))
                    list.Add(e);
            }
            var m = new DialogMirrorMessage { Kind = kind, Elements = list.ToArray() };
            PositionMe pm = container.GetComponent<PositionMe>();
            if (pm != null)
            {
                m.OffsetX = pm.offset.x;
                m.OffsetY = pm.offset.y;
            }
            OwnerSend(m);
        }

        /// <summary>A board vanilla just displayed (its elements were added after <paramref name="before"/>).</summary>
        internal static void OwnerBoard(DialogueWindow dw, HashSet<int> before)
        {
            if (!_ownerOpen || !OwnerCapturing(dw) || dw.currentDialogue == null)
                return;
            _ownerDoneSent = false;
            _lastHighlightSent = -2;
            var list = new List<DialogMirrorElement>(6);
            bool started = false;
            bool first = true;
            for (int i = 0; i < dw.currentBoardElements.Count && list.Count < DialogMirrorMessage.MaxElements; i++)
            {
                Transform t = dw.currentBoardElements[i];
                if (t == null || t.parent != dw.dialogue)
                    continue;
                if (before != null && before.Contains(t.gameObject.GetInstanceID()))
                    continue;
                if (!TryCapture(dw, t, out DialogMirrorElement e))
                    continue;
                if (first)
                {
                    WritingText wt = t.GetComponent<WritingText>();
                    started = wt != null && wt.writing;
                    first = false;
                }
                list.Add(e);
            }
            var m = new DialogMirrorMessage
            {
                Kind = DialogMirrorMessage.KindBoard,
                DialogueName = dw.currentDialogue.fullName ?? "",
                Index = CurrentBoard(dw),
                Flag = started,
                Elements = list.ToArray()
            };
            PositionMe pm = dw.dialogue.GetComponent<PositionMe>();
            if (pm != null)
            {
                m.OffsetX = pm.offset.x;
                m.OffsetY = pm.offset.y;
            }
            OwnerSend(m);
        }

        private static int _lastHighlightSent = -2; // reset-in: Reset

        /// <summary>A button of the talking player's window was highlighted or unhighlighted.</summary>
        internal static void OwnerHighlightChanged(Button changed)
        {
            DialogueWindow dw = Window;
            if (!_ownerOpen || !OwnerCapturing(dw) || dw.menuOptions.IndexOf(changed) < 0)
                return;
            int index = -1;
            for (int i = 0; i < dw.menuOptions.Count; i++)
            {
                if (dw.menuOptions[i] != null && dw.menuOptions[i].rolledOver)
                {
                    index = i;
                    break;
                }
            }
            if (index == _lastHighlightSent)
                return;
            _lastHighlightSent = index;
            OwnerSimple(dw, DialogMirrorMessage.KindSelect, index);
        }

        /// <summary>The board's lines are all written (vanilla finishedElement / speedUpBoard).</summary>
        internal static void OwnerBoardDone(DialogueWindow dw)
        {
            if (_ownerDoneSent || !dw.boardFinished || !dw.displayingDialogue)
                return;
            _ownerDoneSent = true;
            OwnerSimple(dw, DialogMirrorMessage.KindBoardDone);
        }

        private static bool TryCapture(DialogueWindow dw, Transform t, out DialogMirrorElement e)
        {
            e = default;
            int kind = KindOf(t.gameObject.name);
            if (kind < 0)
                return false;
            Vector3 p = t.localPosition;
            e.Kind = (byte)kind;
            e.X = p.x;
            e.Y = p.y;
            e.Z = p.z;
            e.Menu = -1;
            Color c = Color.white;
            WritingText wt = t.GetComponent<WritingText>();
            tk2dTextMesh tm = t.GetComponent<tk2dTextMesh>();
            tk2dBaseSprite sp = t.GetComponent<tk2dBaseSprite>();
            if (wt != null)
            {
                e.Text = wt.destWriteText ?? "";
                e.WriteSpeed = WriteSpeed(wt);
                e.Interval = wt.dialogueCallbackInterval;
                if (wt.textMesh != null)
                    c = wt.textMesh.color;
            }
            else if (tm != null)
            {
                e.Text = tm.text ?? "";
                c = tm.color;
            }
            if (sp != null)
            {
                e.Sprite = sp.CurrentSprite != null ? sp.CurrentSprite.name : "";
                if (tm == null && wt == null)
                    c = sp.color;
            }
            e.R = c.r;
            e.G = c.g;
            e.B = c.b;
            e.A = c.a;
            DialogueButton db = t.GetComponent<DialogueButton>();
            if (db != null)
                e.Target = db.destDialogueName ?? "";
            Button b = t.GetComponent<Button>();
            if (b != null)
                e.Menu = (short)dw.menuOptions.IndexOf(b);
            return true;
        }

        /// <summary>The board about to display changes the portrait (vanilla's own outcome condition).</summary>
        internal static bool BoardPortraitChange(DialogueWindow dw, out int portrait, out bool overlay)
        {
            portrait = -1;
            overlay = false;
            CharacterDialogue.Dialogue d = dw != null ? dw.currentDialogue : null;
            if (d == null || d.boards == null)
                return false;
            int next = CurrentBoard(dw) + 1;
            if (next < 0 || next >= d.boards.Count || d.boards[next] == null || !d.boards[next].hasOutcome
                || d.boards[next].outcomes == null)
                return false;
            foreach (CharacterDialogue.Dialogue.Board.Outcome o in d.boards[next].outcomes)
            {
                if (o == null || (d.alreadyShown && !o.multipleFire))
                    continue;
                bool plain = o.type == CharacterDialogue.Dialogue.Board.Outcome.Type.changePortrait;
                bool anim = o.type == CharacterDialogue.Dialogue.Board.Outcome.Type.changePortraitWithOverlayAnim;
                if (!plain && !anim)
                    continue;
                bool met;
                try { met = o.requirementsMet(); }
                catch { met = true; }
                if (!met)
                    continue;
                portrait = (int)o.portraitType;
                overlay = anim;
                return true;
            }
            return false;
        }

        // ---- host: talks, their latest screen, their listeners ----------------------------------

        private sealed class Session
        {
            public NpcRef Npc;
            public int Portrait;
            public DialogMirrorMessage? Screen;
            public bool Done;
            public int Select = -1;
            public readonly HashSet<int> Listeners = new HashSet<int>();
        }

        private static readonly Dictionary<int, Session> _sessions = new Dictionary<int, Session>(); // reset-in: Reset
        private static readonly List<int> _scratchIds = new List<int>(4); // process-scoped: scratch, cleared before each use

        /// <summary>Host: a message from a peer (or this machine's own listener / talk).</summary>
        internal static void Handle(LanNetworkManager net, DialogMirrorMessage m)
        {
            if (net == null)
                return;
            if (net.Role == NetworkRole.Host)
            {
                int sender = net.CurrentReceivePlayerId;
                if (sender <= 0)
                    return;
                if (m.Kind == DialogMirrorMessage.KindJoin)
                    HostJoin(net, sender, RefOf(m));
                else if (m.Kind == DialogMirrorMessage.KindLeave)
                    HostLeave(sender);
                else if (m.Kind < DialogMirrorMessage.KindJoin)
                    OnOwnerEvent(net, sender, m);
                return;
            }
            Apply(m);
        }

        private static void OnOwnerEvent(LanNetworkManager net, int ownerId, DialogMirrorMessage m)
        {
            m.OwnerId = ownerId;
            if (m.Kind == DialogMirrorMessage.KindOpen)
            {
                // A new talk of this player: whoever watched its last one is done.
                if (_sessions.TryGetValue(ownerId, out Session old))
                    CloseListeners(net, ownerId, old);
                _sessions[ownerId] = new Session { Npc = RefOf(m), Portrait = m.Index };
                return;
            }
            if (!_sessions.TryGetValue(ownerId, out Session s))
                return;
            switch (m.Kind)
            {
                case DialogMirrorMessage.KindBoard:
                case DialogMirrorMessage.KindOptions:
                case DialogMirrorMessage.KindItems:
                case DialogMirrorMessage.KindTrade:
                    s.Screen = m;
                    s.Done = false;
                    s.Select = -1;
                    break;
                case DialogMirrorMessage.KindTextStart:
                    if (s.Screen.HasValue && s.Screen.Value.Kind == DialogMirrorMessage.KindBoard)
                    {
                        DialogMirrorMessage b = s.Screen.Value;
                        b.Flag = true;
                        s.Screen = b;
                    }
                    break;
                case DialogMirrorMessage.KindSpeedup:
                case DialogMirrorMessage.KindBoardDone:
                    s.Done = true;
                    break;
                case DialogMirrorMessage.KindSelect:
                    s.Select = m.Index;
                    break;
                case DialogMirrorMessage.KindPortrait:
                    s.Portrait = m.Index;
                    break;
                case DialogMirrorMessage.KindClose:
                    CloseListeners(net, ownerId, s);
                    _sessions.Remove(ownerId);
                    return;
            }
            foreach (int id in s.Listeners)
                Deliver(net, id, m);
        }

        private static void CloseListeners(LanNetworkManager net, int ownerId, Session s)
        {
            if (s.Listeners.Count == 0)
                return;
            var close = new DialogMirrorMessage { Kind = DialogMirrorMessage.KindClose, OwnerId = ownerId };
            _scratchIds.Clear();
            _scratchIds.AddRange(s.Listeners);
            s.Listeners.Clear();
            for (int i = 0; i < _scratchIds.Count; i++)
                Deliver(net, _scratchIds[i], close);
        }

        private static void HostJoin(LanNetworkManager net, int listenerId, NpcRef npc)
        {
            int owner = NpcDialogueLock.GetOwner(npc);
            if (owner < 0 || owner == listenerId || !_sessions.TryGetValue(owner, out Session s) || !s.Npc.Matches(npc))
            {
                var refused = new DialogMirrorMessage { Kind = DialogMirrorMessage.KindRefused, OwnerId = owner };
                Stamp(ref refused, npc);
                Deliver(net, listenerId, refused);
                return;
            }
            HostLeave(listenerId);
            // The listener of a talk cannot itself be talking (its own talk would hold another NPC).
            s.Listeners.Add(listenerId);
            ModLog.Event(LogCat.Session, $"[DialogMirror] p{listenerId} listens in on p{owner} at {s.Npc}");

            var open = new DialogMirrorMessage { Kind = DialogMirrorMessage.KindOpen, OwnerId = owner, Index = s.Portrait };
            Stamp(ref open, s.Npc);
            Deliver(net, listenerId, open);
            if (s.Screen.HasValue)
                Deliver(net, listenerId, s.Screen.Value);
            if (s.Done)
                Deliver(net, listenerId, new DialogMirrorMessage { Kind = DialogMirrorMessage.KindBoardDone, OwnerId = owner });
            if (s.Select >= 0)
                Deliver(net, listenerId, new DialogMirrorMessage { Kind = DialogMirrorMessage.KindSelect, OwnerId = owner, Index = s.Select });
        }

        private static void HostLeave(int listenerId)
        {
            foreach (var kv in _sessions)
                kv.Value.Listeners.Remove(listenerId);
        }

        /// <summary>Host: a peer left. Its talk ends for its listeners; it stops listening.</summary>
        internal static void HostPeerLeft(LanNetworkManager net, int playerId)
        {
            if (_sessions.TryGetValue(playerId, out Session s))
            {
                CloseListeners(net, playerId, s);
                _sessions.Remove(playerId);
            }
            HostLeave(playerId);
        }

        private static void Deliver(LanNetworkManager net, int playerId, DialogMirrorMessage m)
        {
            if (playerId == net.LocalPlayerId)
            {
                Apply(m);
                return;
            }
            net.SendToPlayer(playerId, NetMessageType.DialogMirror, w => m.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        // ---- listener: the read-only view ------------------------------------------------------

        /// <summary>The local dialogue window is a listener's view (from its opening until it closed).</summary>
        internal static bool SpectatorActive; // reset-in: Reset
        /// <summary>The listener's own close is running (no exit dialogue, no close triggers, no save).</summary>
        internal static bool Closing; // reset-in: Reset
        /// <summary>The mirror itself is moving the highlight / buttons (the listener's mouse is not).</summary>
        internal static bool Driving; // reset-in: Reset

        private static bool _pendingJoin; // reset-in: Reset
        private static float _joinSentAt; // reset-in: Reset
        private static NPC _npc; // reset-in: Reset
        private static NpcRef _npcRef; // reset-in: Reset
        private static int _ownerId = -1; // reset-in: Reset
        private static bool _viewReady; // reset-in: Reset
        private static DialogMirrorMessage? _pendingScreen; // reset-in: Reset
        private static bool _pendingDone; // reset-in: Reset
        private static int _pendingSelect = -1; // reset-in: Reset
        private static int _pendingPortrait = -1; // reset-in: Reset
        private static int _portraitOverride = -1; // reset-in: Reset
        private static int _renderFrame = -1; // reset-in: Reset

        /// <summary>This player's own talk the host turned down (someone else got there first).</summary>
        internal static NPC DeniedNpc; // reset-in: Reset

        /// <summary>
        /// Client: the host denied this player's talk (both started it at once). Close it as it
        /// stands (nothing of it runs: no welcome while it was still opening, no exit dialogue, no
        /// close triggers), then listen in on the other player's talk.
        /// </summary>
        internal static void OnOwnTalkDenied(NPC talked)
        {
            DialogueWindow dw = Window;
            if (dw == null || talked == null)
            {
                Say("Someone is already talking to them…");
                return;
            }
            DeniedNpc = talked;
            dw.StartCoroutine(CloseDenied(dw, talked));
        }

        private static IEnumerator CloseDenied(DialogueWindow dw, NPC talked)
        {
            // Still opening: vanilla close() refuses while tweening, and the opening would carry on
            // by itself afterwards. Let it finish opening (its welcome is held back), then close.
            float until = Time.realtimeSinceStartup + 4f;
            while (dw.npc == talked && (!dw.opened || dw.tweening) && Time.realtimeSinceStartup < until)
                yield return null;
            if (dw.npc == talked && dw.opened && !dw.tweening)
            {
                PrepareClose(dw);
                dw.close();
            }
            DeniedNpc = null;
            if (!TryStartSpectating(talked))
                Say("Someone is already talking to them…");
        }

        /// <summary>
        /// The local player tried to talk to an NPC someone else is talking to: ask to listen in.
        /// False when it cannot (the caller falls back to vanilla, i.e. "someone is talking").
        /// </summary>
        internal static bool TryStartSpectating(NPC npc)
        {
            if (npc == null || npc.characterDialogue == null || SpectatorActive)
                return false;
            if (!NetGuard.Connected(out LanNetworkManager net))
                return false;
            if (_pendingJoin && Time.unscaledTime - _joinSentAt < 4f)
                return true;
            Player p = Player.Instance;
            if (p == null || !p.alive)
                return false;
            _pendingJoin = true;
            _joinSentAt = Time.unscaledTime;
            _npc = npc;
            _npcRef = NpcRef.Of(npc);
            var m = new DialogMirrorMessage { Kind = DialogMirrorMessage.KindJoin };
            Stamp(ref m, _npcRef);
            ModLog.Event(LogCat.Session, $"[DialogMirror] asking to listen in at {_npcRef}");
            if (net.Role == NetworkRole.Host)
                HostJoin(net, net.LocalPlayerId, _npcRef);
            else
                net.Send(NetMessageType.DialogMirror, w => m.Serialize(w), DeliveryMethod.ReliableOrdered);
            return true;
        }

        private static void Apply(DialogMirrorMessage m)
        {
            if (m.Kind == DialogMirrorMessage.KindRefused)
            {
                if (_pendingJoin)
                {
                    _pendingJoin = false;
                    Say("They're not talking anymore.");
                }
                return;
            }
            if (m.Kind == DialogMirrorMessage.KindOpen)
            {
                if (!_pendingJoin || SpectatorActive || _npc == null)
                    return;
                _pendingJoin = false;
                _ownerId = m.OwnerId;
                BeginView(m.Index);
                return;
            }
            if (!SpectatorActive || m.OwnerId != _ownerId)
                return;
            DialogueWindow dw = Window;
            if (dw == null)
                return;

            switch (m.Kind)
            {
                case DialogMirrorMessage.KindBoard:
                case DialogMirrorMessage.KindOptions:
                case DialogMirrorMessage.KindItems:
                case DialogMirrorMessage.KindTrade:
                    _pendingScreen = m;
                    _pendingDone = false;
                    _pendingSelect = -1;
                    if (_viewReady)
                        RenderPending(dw);
                    break;
                case DialogMirrorMessage.KindTextStart:
                    if (!_viewReady)
                    {
                        if (_pendingScreen.HasValue && _pendingScreen.Value.Kind == DialogMirrorMessage.KindBoard)
                        {
                            DialogMirrorMessage b = _pendingScreen.Value;
                            b.Flag = true;
                            _pendingScreen = b;
                        }
                    }
                    else
                        StartText(dw);
                    break;
                case DialogMirrorMessage.KindSpeedup:
                case DialogMirrorMessage.KindBoardDone:
                    if (_viewReady)
                        FinishBoard(dw);
                    else
                        _pendingDone = true;
                    break;
                case DialogMirrorMessage.KindSelect:
                    if (_viewReady)
                        Select(dw, m.Index);
                    else
                        _pendingSelect = m.Index;
                    break;
                case DialogMirrorMessage.KindPortrait:
                    if (_viewReady)
                        dw.StartCoroutine(ChangePortrait(dw, m.Index, m.Flag));
                    else
                        _pendingPortrait = m.Index;
                    break;
                case DialogMirrorMessage.KindClose:
                    StopView(sendLeave: false);
                    break;
            }
        }

        private static void BeginView(int portrait)
        {
            DialogueWindow dw = Window;
            if (dw == null || _npc == null)
                return;
            SpectatorActive = true;
            _viewReady = false;
            _pendingScreen = null;
            _pendingDone = false;
            _pendingSelect = -1;
            _pendingPortrait = -1;
            _portraitOverride = portrait;
            dw.StartCoroutine(OpenWhenIdle(dw));
        }

        /// <summary>The window may still be closing (a denied talk of this player's own): open after it.</summary>
        private static IEnumerator OpenWhenIdle(DialogueWindow dw)
        {
            float until = Time.realtimeSinceStartup + 4f;
            while (SpectatorActive && Time.realtimeSinceStartup < until
                   && (dw.opened || dw.tweening || (Player.Instance != null && Player.Instance.inDialogue)))
                yield return null;
            if (!SpectatorActive)
                yield break;
            if (_npc == null || dw.opened || dw.tweening || Player.Instance == null || Player.Instance.inDialogue)
            {
                SpectatorActive = false;
                SendLeave();
                yield break;
            }
            ModLog.Event(LogCat.Session, $"[DialogMirror] listening in on p{_ownerId} at {_npcRef}");
            Player.Instance.closePopups();
            dw.initiateDialogue(_npc);
        }

        /// <summary>setPortrait Prefix: the listener's view opens on the talking player's current portrait.</summary>
        internal static void ApplyPortraitOverride(DialogueWindow dw)
        {
            if (!SpectatorActive || _portraitOverride < 0 || dw == null || dw.npc == null)
                return;
            dw.npc.portraitType = (CharacterDialogue.PortraitType)_portraitOverride;
            _portraitOverride = -1;
        }

        /// <summary>checkIfWantToDisplayWelcome Prefix: the view opened; show the talking player's screen.</summary>
        internal static void OnViewOpened(DialogueWindow dw)
        {
            _viewReady = true;
            dw.options.gameObject.SetActive(false);
            dw.showItems.gameObject.SetActive(false);
            dw.dialogue.gameObject.SetActive(false);
            if (_pendingPortrait >= 0)
            {
                int p = _pendingPortrait;
                _pendingPortrait = -1;
                dw.StartCoroutine(ChangePortrait(dw, p, false));
            }
            if (_pendingScreen.HasValue)
                RenderPending(dw);
        }

        private static void RenderPending(DialogueWindow dw)
        {
            DialogMirrorMessage m = _pendingScreen.Value;
            bool done = _pendingDone;
            int select = _pendingSelect;
            _pendingDone = false;
            _pendingSelect = -1;
            try
            {
                switch (m.Kind)
                {
                    case DialogMirrorMessage.KindBoard:
                        RenderBoard(dw, m);
                        break;
                    case DialogMirrorMessage.KindOptions:
                        RenderPanel(dw, dw.options, m, DialogueWindow.CurrentMenu.main);
                        break;
                    case DialogMirrorMessage.KindItems:
                        RenderPanel(dw, dw.showItems, m, DialogueWindow.CurrentMenu.showItems);
                        break;
                    case DialogMirrorMessage.KindTrade:
                        // The trading screen is the talking player's own; the listener keeps the portrait.
                        dw.menuOptions.Clear();
                        dw.currentBoardElements.Clear();
                        dw.displayingDialogue = false;
                        dw.options.gameObject.SetActive(false);
                        dw.showItems.gameObject.SetActive(false);
                        dw.dialogue.gameObject.SetActive(false);
                        break;
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Session, "[DialogMirror] render: " + ex.Message);
            }
            _renderFrame = Time.frameCount;
            if (done)
                FinishBoard(dw);
            if (select >= 0)
                Select(dw, select);
        }

        private static GameObject Spawn(byte kind, DialogMirrorElement e, Transform parent)
        {
            string name = KindName(kind);
            if (name == null)
                return null;
            GameObject go = Core.AddPrefab(PrefabRoot + name, new Vector3(e.X, e.Y, e.Z), Quaternion.Euler(90f, 0f, 0f), parent.gameObject);
            if (go != null)
                go.transform.localScale = Vector3.one;
            return go;
        }

        private static void RenderPanel(DialogueWindow dw, Transform container, DialogMirrorMessage m, DialogueWindow.CurrentMenu menu)
        {
            dw.menuOptions.Clear();
            dw.currentlySelectedMenuOption = 0;
            dw.currentBoardElements.Clear();
            dw.displayingDialogue = false;
            dw.currentDialogue = null;
            dw.currentMenu = menu;
            dw.dialogue.gameObject.SetActive(false);
            dw.options.gameObject.SetActive(container == dw.options);
            dw.showItems.gameObject.SetActive(container == dw.showItems);
            container.DestroyChildren();

            var buttons = new SortedDictionary<int, Button>();
            if (m.Elements != null)
            {
                for (int i = 0; i < m.Elements.Length; i++)
                {
                    DialogMirrorElement e = m.Elements[i];
                    GameObject go = Spawn(e.Kind, e, container);
                    if (go == null)
                        continue;
                    var color = new Color(e.R, e.G, e.B, e.A);
                    tk2dTextMesh tm = go.GetComponent<tk2dTextMesh>();
                    if (tm != null)
                    {
                        tm.text = e.Text ?? "";
                        tm.color = color;
                    }
                    tk2dBaseSprite sp = go.GetComponent<tk2dBaseSprite>();
                    if (sp != null && !string.IsNullOrEmpty(e.Sprite))
                    {
                        try { sp.SetSprite(e.Sprite); }
                        catch { /* sprite not in this collection */ }
                        if (tm == null)
                            sp.color = color;
                    }
                    DialogueButton db = go.GetComponent<DialogueButton>();
                    if (db != null)
                        db.destDialogueName = e.Target ?? "";
                    Button b = go.GetComponent<Button>();
                    if (b != null && e.Menu >= 0)
                        buttons[e.Menu] = b;
                }
            }
            dw.menuOptions.AddRange(buttons.Values);
            PositionMe pm = container.GetComponent<PositionMe>();
            if (pm != null)
            {
                pm.offset = new Vector2(m.OffsetX, m.OffsetY);
                pm.init();
            }
        }

        private static void RenderBoard(DialogueWindow dw, DialogMirrorMessage m)
        {
            dw.menuOptions.Clear();
            dw.currentlySelectedMenuOption = 0;
            dw.currentTextLine = 0;
            dw.displayingDialogue = true;
            dw.currentMenu = DialogueWindow.CurrentMenu.main;
            dw.showItems.gameObject.SetActive(false);
            dw.options.gameObject.SetActive(false);
            dw.dialogue.gameObject.SetActive(true);
            dw.dialogue.DestroyChildren();
            dw.currentBoardElements.Clear();
            dw.currentDialogue = FindDialogue(dw.npc, m.DialogueName);

            var buttons = new SortedDictionary<int, Button>();
            bool decisions = false;
            if (m.Elements != null)
            {
                for (int i = 0; i < m.Elements.Length; i++)
                {
                    DialogMirrorElement e = m.Elements[i];
                    if (e.Kind != DialogMirrorElement.KindText && e.Kind != DialogMirrorElement.KindDescText
                        && e.Kind != DialogMirrorElement.KindDecisionBtn)
                        continue;
                    GameObject go = Spawn(e.Kind, e, dw.dialogue);
                    if (go == null)
                        continue;
                    WritingText wt = go.GetComponent<WritingText>();
                    if (wt != null)
                    {
                        wt.destWriteText = e.Text ?? "";
                        if (e.WriteSpeed > 0f)
                            WriteSpeed(wt) = e.WriteSpeed;
                        wt.dialogueCallbackInterval = e.Interval;
                        if (wt.textMesh != null)
                        {
                            wt.textMesh.color = new Color(e.R, e.G, e.B, e.A);
                            wt.textMesh.text = "";
                        }
                    }
                    if (e.Kind == DialogMirrorElement.KindDecisionBtn)
                    {
                        decisions = true;
                        DialogueButton db = go.GetComponent<DialogueButton>();
                        if (db != null)
                            db.destDialogueName = e.Target ?? "";
                        Button b = go.GetComponent<Button>();
                        if (b != null && e.Menu >= 0)
                            buttons[e.Menu] = b;
                    }
                    dw.currentBoardElements.Add(go.transform);
                }
            }
            dw.menuOptions.AddRange(buttons.Values);
            dw.boardFinished = false;
            dw.needsDecision = decisions;
            CurrentBoard(dw) = m.Index;
            BoardStartedAt(dw) = Time.realtimeSinceStartup;
            PositionMe pm = dw.dialogue.GetComponent<PositionMe>();
            if (pm != null)
            {
                pm.offset = new Vector2(m.OffsetX, m.OffsetY);
                pm.init();
            }
            if (m.Flag)
                StartText(dw);
        }

        private static CharacterDialogue.Dialogue FindDialogue(NPC npc, string fullName)
        {
            if (npc == null || npc.characterDialogue == null || string.IsNullOrEmpty(fullName))
                return null;
            List<CharacterDialogue.Dialogue> list = npc.characterDialogue.dialogues;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].fullName == fullName)
                    return list[i];
            return null;
        }

        private static void StartText(DialogueWindow dw)
        {
            if (!dw.displayingDialogue || dw.currentBoardElements.Count == 0 || dw.currentBoardElements[0] == null)
                return;
            WritingText wt = dw.currentBoardElements[0].GetComponent<WritingText>();
            if (wt != null)
                wt.initialize();
        }

        private static void FinishBoard(DialogueWindow dw)
        {
            if (!dw.displayingDialogue || dw.boardFinished)
                return;
            Driving = true;
            try { Traverse.Create(dw).Method("speedUpBoard").GetValue(); }
            catch (System.Exception ex) { ModLog.Warn(LogCat.Session, "[DialogMirror] finish board: " + ex.Message); }
            finally { Driving = false; }
        }

        private static void Select(DialogueWindow dw, int index)
        {
            // Buttons built this frame have not run Start (it takes their base colour from the text):
            // highlighting them now would make the highlight their base colour.
            if (Time.frameCount == _renderFrame)
            {
                dw.StartCoroutine(SelectNextFrame(dw, index));
                return;
            }
            Driving = true;
            try
            {
                for (int i = 0; i < dw.menuOptions.Count; i++)
                {
                    Button b = dw.menuOptions[i];
                    if (b == null || i == index)
                        continue;
                    if (b.rolledOver)
                        b.rollout();
                }
                if (index >= 0 && index < dw.menuOptions.Count && dw.menuOptions[index] != null)
                {
                    dw.menuOptions[index].rollover();
                    dw.currentlySelectedMenuOption = index;
                }
            }
            catch { /* buttons mid-rebuild */ }
            finally { Driving = false; }
        }

        private static IEnumerator SelectNextFrame(DialogueWindow dw, int index)
        {
            yield return null;
            yield return null;
            if (SpectatorActive && _viewReady)
                Select(dw, index);
        }

        /// <summary>Vanilla's changePortrait / changePortraitWithOverlayAnim presentation, without its board advance.</summary>
        private static IEnumerator ChangePortrait(DialogueWindow dw, int portrait, bool overlay)
        {
            Singleton<UI>.Instance.tweenBlackScreenTop(overlay ? new Color(1f, 1f, 1f, 1f) : new Color(0f, 0f, 0f, 1f), 1f);
            yield return new WaitForSecondsRealtime(1.5f);
            if (!SpectatorActive || dw == null || dw.npc == null)
                yield break;
            Renderer r = dw.portrait.GetComponent<Renderer>();
            if (r != null && r.material.mainTexture != null)
                Resources.UnloadAsset(r.material.mainTexture);
            dw.npc.portraitType = (CharacterDialogue.PortraitType)portrait;
            if (dw.npc.characterDialogue != null)
                dw.npc.characterDialogue.portraitType = (CharacterDialogue.PortraitType)portrait;
            System.Action done = () =>
            {
                VideoPlayer vp = dw.portrait.GetComponent<VideoPlayer>();
                if (vp != null)
                    vp.Play();
                Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 1f);
            };
            if (AccessTools.Method(typeof(DialogueWindow), "setPortrait")?.Invoke(dw, new object[] { done }) is IEnumerator it)
                dw.StartCoroutine(it);
        }

        /// <summary>The listener leaves (Esc), or the talk ended for it.</summary>
        internal static void StopView(bool sendLeave)
        {
            if (!SpectatorActive)
                return;
            if (sendLeave)
                SendLeave();
            DialogueWindow dw = Window;
            if (dw == null)
            {
                SpectatorActive = false;
                return;
            }
            dw.StartCoroutine(CloseWhenIdle(dw));
        }

        private static IEnumerator CloseWhenIdle(DialogueWindow dw)
        {
            // Opening or a portrait tween: vanilla close() refuses while tweening.
            float until = Time.realtimeSinceStartup + 4f;
            while (SpectatorActive && dw.tweening && Time.realtimeSinceStartup < until)
                yield return null;
            if (!SpectatorActive)
                yield break;
            if (dw.npc == null || !dw.opened)
            {
                // Never opened (still waiting to): nothing to close.
                SpectatorActive = false;
                yield break;
            }
            dw.close();
        }

        private static void ForceCloseView()
        {
            DialogueWindow dw = Window;
            if (dw != null && dw.npc != null && dw.opened && !dw.tweening)
                dw.close();
        }

        /// <summary>close Prefix while this is a listener's view: a close with nothing of the talk's.</summary>
        internal static void PrepareClose(DialogueWindow dw)
        {
            Closing = true;
            dw.currentMenu = DialogueWindow.CurrentMenu.main;
            dw.displayingDialogue = false;
            dw.dontSaveOnExit = true;
            dw.wantToCook = false;
            dw.dreamToStart = "";
            dw.pauseAfterClosing = false;
        }

        /// <summary>close Postfix: the view is gone (or close() refused and it stays).</summary>
        internal static void AfterClose(DialogueWindow dw, bool sendLeave)
        {
            Closing = false;
            if (dw != null && dw.npc != null)
                return;
            bool was = SpectatorActive;
            SpectatorActive = false;
            _viewReady = false;
            _pendingScreen = null;
            if (was && sendLeave)
                SendLeave();
        }

        private static void SendLeave()
        {
            if (!NetGuard.Connected(out LanNetworkManager net))
                return;
            if (net.Role == NetworkRole.Host)
            {
                HostLeave(net.LocalPlayerId);
                return;
            }
            var m = new DialogMirrorMessage { Kind = DialogMirrorMessage.KindLeave };
            net.Send(NetMessageType.DialogMirror, w => m.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>A button of the listener's view (its mouse and clicks do nothing there).</summary>
        internal static bool IsViewButton(Button b)
        {
            DialogueWindow dw = Window;
            return b != null && dw != null && b.transform.IsChildOf(dw.transform);
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
