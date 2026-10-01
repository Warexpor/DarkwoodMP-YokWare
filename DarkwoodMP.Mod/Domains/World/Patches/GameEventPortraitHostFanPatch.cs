using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla <c>GameEvent</c> CharacterModify.portraitType writes
    /// <see cref="NPC.portraitType"/> (and optionally
    /// <c>characterDialogue.portraitType</c>) on resolved targets inside
    /// <c>fire</c> — locals, not a property setter. Clients gate dialogue
    /// options and portrait video on local <c>npc.portraitType</c>. Soft-match
    /// miss on GameEventsFired leaves a stale face; host fans the result on
    /// existing <see cref="ReputationSyncMessage"/> (portrait trailer).
    /// </summary>
    [HarmonyPatch]
    public static class GameEventPortraitHostFanPatch
    {
        private static FieldInfo _gameEventField;

        // Per-MoveNext state travels in __state (null = not tracked): a GameEvent step can start
        // another GameEvent coroutine whose MoveNext runs inside this one, and statics would be
        // overwritten.
        private sealed class State
        {
            public CharacterDialogue.PortraitType Expected;
            public bool ApplyDialogue;
            public readonly List<int> Ids = new List<int>(64);
            public readonly List<CharacterDialogue.PortraitType> Before =
                new List<CharacterDialogue.PortraitType>(64);
        }

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

        private static void Prefix(object __instance, out State __state)
        {
            __state = null;
            // Host-only fan-out: nothing to track offline / as a client.
            var hostNet = LanNetworkManager.Instance;
            if (hostNet == null || hostNet.Role != NetworkRole.Host || !hostNet.IsConnected)
                return;
            GameEvent ge = GetGameEvent(__instance);
            if (ge == null) return;
            if (ge.type != GameEvent.Type.modifyCharacter) return;
            if (ge.characterModifyType != GameEvent.CharacterModify.portraitType)
                return;

            var st = new State
            {
                Expected = ge.portraitType,
                ApplyDialogue = ge.activeModifier
            };
            __state = st;

            // trueTargets is a MoveNext local (no yield cross) — snapshot scene
            // NPCs and detect which faces the write changed.
            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
            for (int i = 0; i < all.Length; i++)
            {
                NPC n = all[i];
                if (n == null) continue;
                st.Ids.Add(n.GetInstanceID());
                st.Before.Add(n.portraitType);
            }
        }

        private static void Postfix(object __instance, State __state)
        {
            if (__state == null) return;

            var net = LanNetworkManager.Instance;
            // Host-only. Clients applying GameEventsFired write portraits under
            // NetworkApplyGuard — do not echo (avoids loop / spam).
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
            var flags = Singleton<Flags>.Instance;

            for (int i = 0; i < all.Length; i++)
            {
                NPC n = all[i];
                if (n == null) continue;
                int id = n.GetInstanceID();
                int idx = __state.Ids.IndexOf(id);
                if (idx < 0) continue;
                CharacterDialogue.PortraitType was = __state.Before[idx];
                if (n.portraitType == was) continue;
                if (n.portraitType != __state.Expected) continue;

                string npcName = n.name;
                if (string.IsNullOrEmpty(npcName)) continue;

                int rep = 0;
                int attackedId = 0;
                bool dead = false;
                int deadId = 0;
                if (flags != null)
                {
                    Flags.NPCState st = flags.getNPCState(npcName);
                    if (st != null)
                    {
                        rep = st.reputation;
                        attackedId = st.attackedID;
                        dead = st.dead;
                        deadId = st.deadID;
                    }
                }

                Vector3 pos = n.transform.position;
                NpcAttackedIdSync.BroadcastFromHost(
                    npcName, rep, attackedId,
                    hasDead: true, dead: dead, deadId: deadId,
                    hasPortrait: true,
                    portraitType: (int)__state.Expected,
                    applyDialoguePortrait: __state.ApplyDialogue,
                    posX: pos.x, posY: pos.y, posZ: pos.z);
                ModRuntime.LegacyInfo(
                    $"[RepSync] GameEvent portrait host fan '{npcName}': {was} → {__state.Expected}");
            }
        }
    }
}
