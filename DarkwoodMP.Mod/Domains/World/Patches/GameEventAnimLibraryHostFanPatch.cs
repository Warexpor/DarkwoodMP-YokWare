using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla <c>GameEvent</c> CharacterModify.animationLibraryOverride writes
    /// <see cref="Character.animationLibraryOverride"/> on resolved targets inside
    /// <c>fire</c> — locals, not a networked property. The setter loads
    /// <c>Resources.Load(path)</c> into <c>animator.Library</c>, which is what every
    /// peer draws for that NPC body. Soft-match miss on GameEventsFired leaves the
    /// old sprite library; host fans the result on existing
    /// <see cref="ReputationSyncMessage"/> (anim-library trailer).
    /// </summary>
    [HarmonyPatch]
    public static class GameEventAnimLibraryHostFanPatch
    {
        private static FieldInfo _gameEventField;

        // Per-MoveNext state travels in __state (null = not tracked): a GameEvent step can start
        // another GameEvent coroutine whose MoveNext runs inside this one, and statics would be
        // overwritten.
        private sealed class State
        {
            public string Expected;
            public readonly List<int> Ids = new List<int>(64);
            public readonly List<string> Before = new List<string>(64);
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
            if (ge.characterModifyType != GameEvent.CharacterModify.animationLibraryOverride)
                return;

            // Vanilla assigns GameEvent.Value into the property setter.
            var st = new State { Expected = ge.Value ?? "" };
            __state = st;

            // trueTargets is a MoveNext local (no yield cross) — snapshot scene
            // Characters and detect which libraries the write changed.
            Character[] all = WorldQueryHelper.GetCachedSceneComponents<Character>();
            for (int i = 0; i < all.Length; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                st.Ids.Add(c.GetInstanceID());
                st.Before.Add(c.animationLibraryOverride ?? "");
            }
        }

        private static void Postfix(object __instance, State __state)
        {
            if (__state == null) return;

            var net = LanNetworkManager.Instance;
            // Host-only. Clients applying GameEventsFired write libraries under
            // NetworkApplyGuard — do not echo (avoids loop / spam).
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            Character[] all = WorldQueryHelper.GetCachedSceneComponents<Character>();
            var flags = Singleton<Flags>.Instance;
            string expected = __state.Expected ?? "";

            for (int i = 0; i < all.Length; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                int id = c.GetInstanceID();
                int idx = __state.Ids.IndexOf(id);
                if (idx < 0) continue;
                string was = __state.Before[idx] ?? "";
                string now = c.animationLibraryOverride ?? "";
                if (now == was) continue;
                if (now != expected) continue;

                // Prefer NPC.name (Flags / FindNpcByNameNear); fall back to GO name.
                string npcName = c.npc != null ? c.npc.name : c.name;
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

                Vector3 pos = c.transform.position;
                NpcAttackedIdSync.BroadcastFromHost(
                    npcName, rep, attackedId,
                    hasDead: true, dead: dead, deadId: deadId,
                    hasAnimLibrary: true,
                    animLibraryName: expected,
                    posX: pos.x, posY: pos.y, posZ: pos.z);
                ModRuntime.LegacyInfo(
                    $"[RepSync] GameEvent animLibrary host fan '{npcName}': '{was}' → '{expected}'");
            }
        }
    }
}
