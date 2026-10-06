using System;
using System.Collections.Generic;
using System.Reflection;
using DWMPHorde.Harmony;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Counted suppression for Core.pause / Core.unpause while multiplayer UI is open.
    /// Host and clients must not freeze Time.timeScale independently (asymmetric world).
    /// Map / journal / padlock / dialogue / leveling / skill menus / interactive item UI.
    /// </summary>
    internal static class PauseSuppression
    {
        internal static int SuppressPause;
        internal static int SuppressUnpause;

        /// <summary>True while LevelingMenu.show holds one SuppressPause until hide releases it.</summary>
        internal static bool LevelingHold;

        /// <summary>True when co-op is live, not offline with a dormant network component.</summary>
        internal static bool MultiplayerActive =>
            ModRuntime.Network != null && ModRuntime.Network.IsConnected;

        public static void Reset()
        {
            SuppressPause = 0;
            SuppressUnpause = 0;
            LevelingHold = false;
        }

        internal static void BeginNoPause()
        {
            if (MultiplayerActive)
                SuppressPause++;
        }

        internal static void EndNoPause()
        {
            if (MultiplayerActive && SuppressPause > 0)
                SuppressPause--;
        }

        internal static void BeginNoUnpause()
        {
            if (MultiplayerActive)
                SuppressUnpause++;
        }

        internal static void EndNoUnpause()
        {
            if (MultiplayerActive && SuppressUnpause > 0)
                SuppressUnpause--;
        }
    }

    /// <summary>Blocks Core.pause during multiplayer non-blocking UI.</summary>
    [HarmonyPatch(typeof(Core), "pause")]
    internal static class CorePauseMultiplayerPatch
    {
        private static bool Prefix()
        {
            if (PauseSuppression.MultiplayerActive && PauseSuppression.SuppressPause > 0)
                return false;
            return true;
        }
    }

    /// <summary>Blocks Core.unpause during multiplayer UI.</summary>
    [HarmonyPatch(typeof(Core), "unpause")]
    internal static class CoreUnpauseMultiplayerPatch
    {
        private static bool Prefix()
        {
            if (PauseSuppression.MultiplayerActive && PauseSuppression.SuppressUnpause > 0)
                return false;
            return true;
        }
    }

    // ---- UI open/show paths: hold pause suppression for the whole menu ----
    // Class-level stacked [HarmonyPatch] attributes merge into ONE target, so the groups resolve
    // their targets explicitly. Every UI listed in Open suppresses the vanilla Core.pause and
    // its counterpart in Close suppresses the matching Core.unpause (both or neither).

    [HarmonyPatch]
    internal static class UiOpenNoPausePatches
    {
        // An empty target list aborts PatchAll; skip the class instead (missing targets are logged).
        private static bool Prepare() => System.Linq.Enumerable.Any(TargetMethods());

        // Prepare and the patcher both call TargetMethods; resolve (and log misses) once.
        private static List<MethodBase> _targets; // process-scoped: immutable reflection result

        private static IEnumerable<MethodBase> TargetMethods() =>
            _targets ??= new List<MethodBase>(ResolveTargets());

        private static IEnumerable<MethodBase> ResolveTargets()
        {
            return PatchTargets.Resolve(
                PatchTargets.Find(typeof(Map), "open", Type.EmptyTypes),
                PatchTargets.Find(typeof(Journal), "open", Type.EmptyTypes),
                PatchTargets.Find(typeof(Journal), "showNote", new[] { typeof(JournalNote.Note) }),
                PatchTargets.Find(typeof(Padlock), "activate", Type.EmptyTypes),
                // Core.pause lives in SetDialogue (the setPortrait callback), not in an "open" method.
                PatchTargets.Find(typeof(DialogueWindow), "SetDialogue", Type.EmptyTypes),
                PatchTargets.Find(typeof(SkillPointsMenu), "open", Type.EmptyTypes),
                PatchTargets.Find(typeof(SkillSlotsMenu), "open", Type.EmptyTypes),
                PatchTargets.Find(typeof(InteractiveItem), "open", Type.EmptyTypes));
        }

        private static void Prefix() => PauseSuppression.BeginNoPause();
        // Finalizer (not Postfix): open/show can throw after Begin → stuck
        // SuppressPause blocks Core.pause forever. Finalizer-only End so we do not
        // double-decrement against LevelingMenu.show's cross-method hold.
        private static void Finalizer() => PauseSuppression.EndNoPause();
    }

    // ---- UI close/hide paths: hold unpause suppression ----

    [HarmonyPatch]
    internal static class UiCloseNoUnpausePatches
    {
        // An empty target list aborts PatchAll; skip the class instead (missing targets are logged).
        private static bool Prepare() => System.Linq.Enumerable.Any(TargetMethods());

        // Prepare and the patcher both call TargetMethods; resolve (and log misses) once.
        private static List<MethodBase> _targets; // process-scoped: immutable reflection result

        private static IEnumerable<MethodBase> TargetMethods() =>
            _targets ??= new List<MethodBase>(ResolveTargets());

        private static IEnumerable<MethodBase> ResolveTargets()
        {
            return PatchTargets.Resolve(
                PatchTargets.Find(typeof(Map), "close", new[] { typeof(bool) }),
                PatchTargets.Find(typeof(Journal), "close", new[] { typeof(bool) }),
                PatchTargets.Find(typeof(Journal), "hideNote", Type.EmptyTypes),
                PatchTargets.Find(typeof(Padlock), "deactivate", Type.EmptyTypes),
                // DialogueWindow.close() only starts the tween; the Core.unpause that pairs with
                // SetDialogue's pause runs in onTweenClose.
                PatchTargets.Find(typeof(DialogueWindow), "onTweenClose", Type.EmptyTypes),
                PatchTargets.Find(typeof(SkillPointsMenu), "close", Type.EmptyTypes),
                PatchTargets.Find(typeof(SkillSlotsMenu), "close", Type.EmptyTypes),
                PatchTargets.Find(typeof(InteractiveItem), "close", Type.EmptyTypes));
        }

        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        // Finalizer (not Postfix): close/hide throw after Begin → stuck SuppressUnpause.
        private static void Finalizer() => PauseSuppression.EndNoUnpause();
    }

    // ---- Leveling / skill menus (delayed coroutine pause; different body) ----

    /// <summary>
    /// LevelingMenu.show starts a coroutine that later calls Core.pause().
    /// Hold SuppressPause from show until hide so the delayed pause is blocked.
    /// </summary>
    [HarmonyPatch(typeof(LevelingMenu), "show")]
    internal static class LevelingMenuShowNoPausePatch
    {
        private static void Prefix()
        {
            // One hold per show: a repeated show (or a hide that never ran the vanilla body)
            // must not leave the counter drifting and blocking Core.pause for other menus.
            if (PauseSuppression.MultiplayerActive && !PauseSuppression.LevelingHold)
            {
                PauseSuppression.LevelingHold = true;
                PauseSuppression.SuppressPause++;
            }
        }
    }

    [HarmonyPatch(typeof(LevelingMenu), "hide")]
    internal static class LevelingMenuHideNoUnpausePatch
    {
        private static void Prefix()
        {
            PauseSuppression.BeginNoUnpause();
            // Release hold from show
            if (PauseSuppression.LevelingHold)
            {
                PauseSuppression.LevelingHold = false;
                if (PauseSuppression.SuppressPause > 0)
                    PauseSuppression.SuppressPause--;
            }
        }

        // Finalizer (not Postfix): hide throw after BeginNoUnpause → stuck SuppressUnpause.
        private static void Finalizer() => PauseSuppression.EndNoUnpause();
    }
}