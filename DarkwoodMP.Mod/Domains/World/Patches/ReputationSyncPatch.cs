using System.Reflection;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using DWMPHorde.Sync;
using UnityEngine;
using DWMPHorde.Harmony;
using DWMPHorde.Logging;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Reputation handling:
    /// - Every NPC but the morning traders: shared, live <see cref="ReputationSync"/> + join bulk.
    ///   That includes the ones who trade (the Wolf, Piotrek, the Doctor): a client's trade moves
    ///   the standing through the host (<see cref="TradeCommit"/>), not by a broadcast from here.
    /// - The morning traders (the Trader, the Three): per-player; no live or bulk overwrite, and
    ///   the host replaying a peer's dialogue does not move its own standing with them.
    /// </summary>
    [HarmonyPatch(typeof(NPC), "set_reputation", new[] { typeof(int) })]
    public static class ReputationSyncPatch
    {
        private static bool Prefix(NPC __instance, object[] __args)
        {
            int value = (int)__args[0];

            if (__instance == null)
                return true;

            // Host replaying a peer's board: a morning trader's standing is the speaker's, which
            // its own board already changed. The host's stayed behind it.
            if (HostApplyGuard.Active && NetGuard.ConnectedHost(out _)
                && ReputationSyncUtil.IsPerPlayerReputationNpc(__instance))
                return false;

            // Client board: do not mutate shared NPC reputation (host applies once).
            if (DWMPHorde.Sync.DialogClientWorldDefer.Active
                && DialogApplyPolicy.ShouldDeferSharedReputation(
                    ReputationSyncUtil.IsPerPlayerReputationNpc(__instance)))
                return false;

            if (LanNetworkManager.IsApplyingRemoteState
                && !DWMPHorde.Sync.HostApplyGuard.Active)
                return true;

            if (!NetGuard.Connected(out var net))
                return true;

            if (ReputationSyncUtil.IsPerPlayerReputationNpc(__instance))
                return true;

            // A client's own trade: the host adds the trade's change to the party's standing and
            // tells everyone (TradeCommit). This client's number is only its guess until then,
            // and sent as the truth it undid a trade another player made in the same moment.
            if (TradeSyncAcceptPatch.Accepting && net.Role == NetworkRole.Client)
                return true;

            string npcName = __instance.name;
            if (string.IsNullOrEmpty(npcName))
                return true;

            ModRuntime.LegacyInfo($"[RepSync] broadcasting shared rep '{npcName}': {value}");

            var msg = new ReputationSyncMessage
            {
                NpcName = npcName,
                Reputation = value
            };

            net.Broadcast(NetMessageType.ReputationSync, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            return true;
        }
    }

    /// <summary>
    /// Vanilla <c>GameEvent</c> CharacterModify.reputation writes
    /// <see cref="Flags.NPCState.reputation"/> directly — never
    /// <c>NPC.set_reputation</c> — so the live ReputationSync postfix never runs.
    /// Clients still read standing in trade UI (<c>acceptTrade</c> /
    /// <c>refreshReputation</c>). Soft-match miss on GameEventsFired leaves stale
    /// Flags; host fans existing ReputationSync (with attackedID/dead trailers).
    /// </summary>
    [HarmonyPatch]
    public static class GameEventReputationHostFanPatch
    {
        private static FieldInfo _gameEventField; // process-scoped: reflection cache

        // Per-MoveNext state travels in __state: a GameEvent step can start another GameEvent
        // coroutine whose MoveNext runs inside this one, and statics would be overwritten.
        private struct State
        {
            public bool Track;
            public string NpcName;
            public int RepBefore;
        }

        private static bool Prepare() => TargetMethod() != null;

        private static MethodBase TargetMethod() =>
            HarmonyCoroutineUtil.FindMoveNext(typeof(GameEvent), "fire", new[] { typeof(GameObject) });

        private static GameEvent GetGameEvent(object stateMachine)
        {
            if (stateMachine == null) return null;
            if (_gameEventField == null)
                _gameEventField = HarmonyCoroutineUtil.FindThisField(stateMachine.GetType(), typeof(GameEvent));
            return _gameEventField != null
                ? _gameEventField.GetValue(stateMachine) as GameEvent
                : null;
        }

        private static void Prefix(object __instance, out State __state)
        {
            __state = default;
            // Host-only fan-out: nothing to track offline / as a client.
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;
            GameEvent ge = GetGameEvent(__instance);
            if (ge == null) return;
            if (ge.type != GameEvent.Type.modifyCharacter) return;
            if (ge.characterModifyType != GameEvent.CharacterModify.reputation) return;
            if (string.IsNullOrEmpty(ge.Value)) return;
            if (ReputationSyncUtil.IsPerPlayerReputationNpcName(ge.Value)) return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;
            Flags.NPCState st = flags.getNPCState(ge.Value);
            // Vanilla only writes when the NPCState already exists.
            if (st == null) return;

            __state.NpcName = ge.Value;
            __state.RepBefore = st.reputation;
            __state.Track = true;
        }

        private static void Postfix(object __instance, State __state)
        {
            if (!__state.Track) return;
            string npcName = __state.NpcName;
            int before = __state.RepBefore;

            var net = ModRuntime.Network;
            // Host-only. Clients applying GameEventsFired write Flags locally under
            // NetworkApplyGuard — do not echo ReputationSync (avoids re-entry / spam).
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null || string.IsNullOrEmpty(npcName)) return;
            Flags.NPCState st = flags.getNPCState(npcName);
            if (st == null || st.reputation == before) return;

            // Include attackedID/dead so a reputation-only update cannot wipe those marks.
            NpcAttackedIdSync.BroadcastFromHost(
                npcName, st.reputation, st.attackedID,
                hasDead: true, dead: st.dead, deadId: st.deadID);
            ModRuntime.LegacyInfo(
                $"[RepSync] GameEvent host fan '{npcName}': {before} → {st.reputation}");
        }
    }

    /// <summary>Shared helpers for model-C reputation filtering.</summary>
    internal static class ReputationSyncUtil
    {
        /// <summary>
        /// True for the morning traders, whose standing stays per-player
        /// (<see cref="DialogApplyPolicy.IsPerPlayerReputationNpcName"/>). By name, not by
        /// <see cref="Character.isNightTrader"/>: vanilla sets that on the underground soldier
        /// too, a story trader the party shares.
        /// </summary>
        public static bool IsPerPlayerReputationNpc(NPC npc)
            => npc != null && DialogApplyPolicy.IsPerPlayerReputationNpcName(npc.name);

        public static bool IsPerPlayerReputationNpcName(string npcName)
            => DialogApplyPolicy.IsPerPlayerReputationNpcName(npcName);

        /// <summary>
        /// A trader's standing changed from outside (another player's trade, a refused one): the
        /// trade window open on that trader shows it and prices against it.
        /// </summary>
        internal static void RefreshOpenTrade(string npcName)
        {
            var ui = Singleton<UI>.Instance;
            DialogueWindow dw = ui != null ? ui.dialogueWindow : null;
            if (dw == null || !dw.opened || dw.npc == null || dw.npc.name != npcName
                || dw.currentMenu != DialogueWindow.CurrentMenu.trade || dw.npc.inventory == null)
                return;
            try { dw.npc.inventory.refreshReputation(); }
            catch (System.Exception ex) { ModLog.Swallowed(ex); /* the window is closing */ }
        }
    }
}
