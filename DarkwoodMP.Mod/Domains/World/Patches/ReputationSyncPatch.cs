using System.Reflection;
using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Reputation handling:
    /// - Story / village NPCs: shared, live <see cref="ReputationSync"/> + join bulk.
    /// - Morning traders (<see cref="Character.isNightTrader"/>): per-player; no live or bulk overwrite.
    /// </summary>
    [HarmonyPatch(typeof(NPC), "set_reputation", new[] { typeof(int) })]
    public static class ReputationSyncPatch
    {
        private static bool Prefix(NPC __instance, object[] __args)
        {
            int value = (int)__args[0];

            if (__instance == null)
                return true;

            // Client board: do not mutate shared NPC reputation (host applies once).
            if (DWMPHorde.Sync.DialogClientWorldDefer.Active
                && DialogApplyPolicy.ShouldDeferSharedReputation(
                    ReputationSyncUtil.IsPerPlayerReputationNpc(__instance)))
                return false;

            if (LanNetworkManager.IsApplyingRemoteState
                && !DWMPHorde.Sync.DialogHostApplyGuard.Active)
                return true;

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return true;

            if (ReputationSyncUtil.IsPerPlayerReputationNpc(__instance))
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
        private static FieldInfo _gameEventField;
        private static string _npcName;
        private static int _repBefore;
        private static bool _track;

        private static bool Prepare() => TargetMethod() != null;

        private static MethodBase TargetMethod()
        {
            System.Type[] nested = typeof(GameEvent).GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < nested.Length; i++)
            {
                System.Type t = nested[i];
                if (t.Name.IndexOf("fire", System.StringComparison.Ordinal) < 0)
                    continue;
                if (!typeof(System.Collections.IEnumerator).IsAssignableFrom(t))
                    continue;
                MethodInfo m = AccessTools.Method(t, "MoveNext");
                if (m != null)
                    return m;
            }
            return null;
        }

        private static GameEvent GetGameEvent(object stateMachine)
        {
            if (stateMachine == null) return null;
            if (_gameEventField == null)
            {
                FieldInfo[] fields = stateMachine.GetType().GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].FieldType == typeof(GameEvent))
                    {
                        _gameEventField = fields[i];
                        break;
                    }
                }
            }
            return _gameEventField != null
                ? _gameEventField.GetValue(stateMachine) as GameEvent
                : null;
        }

        private static void Prefix(object __instance)
        {
            _track = false;
            _npcName = null;
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

            _npcName = ge.Value;
            _repBefore = st.reputation;
            _track = true;
        }

        private static void Postfix(object __instance)
        {
            if (!_track) return;
            _track = false;
            string npcName = _npcName;
            int before = _repBefore;
            _npcName = null;

            var net = LanNetworkManager.Instance;
            // Host-only. Clients applying GameEventsFired write Flags locally under
            // NetworkApplyGuard — do not echo ReputationSync (avoids re-entry / spam).
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null || string.IsNullOrEmpty(npcName)) return;
            Flags.NPCState st = flags.getNPCState(npcName);
            if (st == null || st.reputation == before) return;

            // Include attackedID/dead trailers so a rep-only packet cannot wipe 0.8.93/94 marks.
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
        /// True for morning hideout traders whose standing must stay per-player.
        /// Prefers <see cref="Character.isNightTrader"/>; name fallbacks if the GO is unloaded.
        /// </summary>
        public static bool IsPerPlayerReputationNpc(NPC npc)
        {
            if (npc == null) return false;
            Character ch = npc.GetComponent<Character>();
            if (ch != null)
                return ch.isNightTrader;
            return IsPerPlayerReputationNpcName(npc.name);
        }

        public static bool IsPerPlayerReputationNpcName(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return false;

            // Prefer live Character flag when the NPC is in the scene.
            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i].name != npcName) continue;
                Character ch = all[i].GetComponent<Character>();
                if (ch != null)
                    return ch.isNightTrader;
            }

            return DialogApplyPolicy.IsPerPlayerReputationNpcName(npcName);
        }
    }
}
