using System.Collections;
using DWMPHorde.Harmony;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

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

        private static bool Prefix(GameEvent __instance, GameObject thisGO, ref IEnumerator __result)
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
            if (IsPersonalPlayerTargeted(__instance))
            {
                __result = HarmonyCoroutineUtil.Empty();
                return false;
            }
            // Screen and input steps of a scripted scene (black screen, camera pan, input lock,
            // hidden HUD, perspective) belong to whoever is in that scene. A peer elsewhere in
            // the world had its camera dragged off, its HUD hidden or its inputs locked.
            if (IsScenePresentation(__instance) && !LocalPlayerInSameLocation(thisGO))
            {
                __result = HarmonyCoroutineUtil.Empty();
                return false;
            }
            return true;
        }

        internal static bool IsScenePresentation(GameEvent ge)
        {
            switch (ge.type)
            {
                case GameEvent.Type.cameraEffect:
                case GameEvent.Type.forbidInputs:
                case GameEvent.Type.switchCantChangeForbidInputs:
                case GameEvent.Type.switchVisibleUI:
                    return true;
                case GameEvent.Type.timeScale:
                    // Slow motion: on the host it slowed the whole simulation for everyone.
                    return true;
                case GameEvent.Type.modifyMainScript:
                    // setWorldGrid picks the walk grid of the area the scene is in; a peer elsewhere
                    // was switched onto a grid for ground it is not standing on.
                    return ge.mainScriptModify == GameEvent.MainScriptModify.switchPerspective
                        || ge.mainScriptModify == GameEvent.MainScriptModify.tweenPerspectiveAlpha
                        || ge.mainScriptModify == GameEvent.MainScriptModify.setWorldGrid;
                default:
                    return false;
            }
        }

        private static bool LocalPlayerInSameLocation(GameObject geObject)
        {
            if (geObject == null)
                return false;
            Player p = Player.Instance;
            if (p == null || p.whereAmI == null || p.whereAmI.bigLocation == null)
                return false;
            Location geLoc = geObject.GetComponentInParent<Location>(true);
            return geLoc != null && geLoc.bigLocation == p.whereAmI.bigLocation;
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
            // Host running a peer's action (its trigger, cursor use, dialogue): the stamped
            // actor's own client replays the personal part; the host body must not get it.
            var net = ModRuntime.Network;
            if (net != null && net.IsConnected && net.Role == NetworkRole.Host)
            {
                int actor = GeFireActorContext.PeekOr(0);
                if (actor > 0 && actor != net.LocalPlayerId)
                    return true;
            }
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
                case GameEvent.Type.worldFlag:
                    // A flag about where this one player is (player_at*, player_entering*, player_in*):
                    // replayed on everyone it put every peer "at the doctor's house" or "entering
                    // the road", wherever they stood.
                    return FlagSyncBoolPatch.IsPerPlayerSpatialFlag(ge.Value);
                case GameEvent.Type.openDialogue:
                    // The scene opens the NPC's talk window for the player it is about.
                    return true;
                case GameEvent.Type.transportToOutsideLocation:
                case GameEvent.Type.returnToWorld:
                    return true;
                case GameEvent.Type.modifyCharacter:
                    return ge.characterModifyType
                        == GameEvent.CharacterModify.player_tweenShadow;
                case GameEvent.Type.runFunction:
                    // SendMessage to the Player: these specials move, animate, dress or
                    // equip the body that triggered the scene (pet the dog, get out of bed,
                    // coat on/off, the flamethrower hand-over and Maciek placed beside that
                    // body, the table leg breaking). Run on any other body they hijacked it.
                    return IsPersonalPlayerFunction(ge.Value);
                case GameEvent.Type.modifyMainScript:
                    // setTimeFreeze + activeModifier2 → Player.Instance.effects;
                    // without activeModifier2 it toggles world DoUpdateTime — keep that.
                    return ge.mainScriptModify == GameEvent.MainScriptModify.setTimeFreeze
                        && ge.activeModifier2;
                default:
                    return false;
            }
        }

        internal static bool IsPersonalPlayerFunction(string fn)
        {
            switch (fn)
            {
                case "special_petDog":
                case "special_drainAllTableLegDurability":
                case "special_getUpFromBed":
                case "special_changeClothes":
                case "special_removeClothes":
                case "special_addFlamethrower":
                    return true;
                default:
                    return false;
            }
        }
    }
}
