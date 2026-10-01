using System.Collections;
using DWMPHorde.Harmony;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Skip vanilla GameEvent types that mutate <c>Player.Instance</c> when the
    /// local body is not the actor: host remote world-only apply, or client
    /// GameEventsFired apply stamped for another peer. Journal / setActive /
    /// worldFlag still run. GameEvent.fire is an IEnumerator — return Empty, not null.
    /// </summary>
    [HarmonyPatch(typeof(GameEvent), nameof(GameEvent.fire))]
    public static class GameEventPersonalActorPatch
    {
        /// <summary>Set while client ApplyGameEventsFired runs for a non-local actor.</summary>
        public static bool SuppressPersonalForLocalPlayer; // process-scoped: call-scoped, unwound by its Finalizer/finally

        private static bool Prefix(GameEvent __instance, ref IEnumerator __result)
        {
            if (__instance == null) return true;

            // Chapter jump is a world event owned by the host, whoever the actor is.
            // Host runs it (Save + generateChapter -> ChapterTransition + share for all).
            // A connected client never runs it: its generateChapter would only send a
            // ChapterTransition request the host rejects, and the host reload already
            // brings the client along.
            if (IsChapterJump(__instance))
            {
                if (!IsConnectedClient()) return true;
                __result = HarmonyCoroutineUtil.Empty();
                return false;
            }

            if (!ShouldSuppressPersonal()) return true;
            if (!IsPersonalPlayerTargeted(__instance)) return true;

            __result = HarmonyCoroutineUtil.Empty();
            return false;
        }

        /// <summary>
        /// Vanilla transportPlayerToObject + activeModifier: Save, then
        /// <c>Controller.generateChapter(intValue)</c> (ch1 to ch2). Not a teleport.
        /// </summary>
        internal static bool IsChapterJump(GameEvent ge)
            => ge != null
               && ge.type == GameEvent.Type.transportPlayerToObject
               && ge.activeModifier;

        private static bool IsConnectedClient()
            => ModRuntime.Network != null
               && ModRuntime.Network.IsConnected
               && ModRuntime.Network.Role == NetworkRole.Client;

        private static bool ShouldSuppressPersonal()
        {
            if (DialogHostApplyGuard.SuppressPersonalRewards)
                return true;
            return SuppressPersonalForLocalPlayer;
        }

        /// <summary>
        /// Vanilla types that grant/remove bag items, recipes, or move the local
        /// player. addJournalItem is shared world identity — not listed.
        /// There is no dedicated heal/damage/experience GameEvent type.
        /// </summary>
        internal static bool IsPersonalPlayerTargeted(GameEvent ge)
        {
            if (ge == null) return false;
            switch (ge.type)
            {
                case GameEvent.Type.addOrRemoveInvItem:
                    // activeModifier → Player.Instance inventory; else target Inventories.
                    return ge.activeModifier;
                case GameEvent.Type.addRecipes:
                    return true;
                case GameEvent.Type.transportPlayerToObject:
                    // activeModifier = chapter jump (world event, see IsChapterJump).
                    return !ge.activeModifier;
                case GameEvent.Type.transportToOutsideLocation:
                case GameEvent.Type.returnToWorld:
                    return true;
                case GameEvent.Type.modifyCharacter:
                    return ge.characterModifyType
                        == GameEvent.CharacterModify.player_tweenShadow;
                case GameEvent.Type.modifyMainScript:
                    // setTimeFreeze + activeModifier2 → Player.Instance.effects;
                    // without activeModifier2 it toggles world DoUpdateTime — keep that.
                    return ge.mainScriptModify == GameEvent.MainScriptModify.setTimeFreeze
                        && ge.activeModifier2;
                default:
                    return false;
            }
        }
    }
}
