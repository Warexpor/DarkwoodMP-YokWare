using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    // Hooks of DialogMirror (listening in on another player's dialogue). The board hook lives in
    // DialogDisplayNextBoardPatch, the one patch on displayNextBoard.

    /// <summary>Talking to an NPC another player is talking to: listen in instead of being turned away.</summary>
    [HarmonyPatch(typeof(NPC), nameof(NPC.talkTo))]
    public static class DialogMirrorTalkToPatch
    {
        private static bool Prefix(NPC __instance)
        {
            if (__instance == null || __instance.characterDialogue == null)
                return true;
            if (!NetGuard.Connected(out LanNetworkManager net))
                return true;
            int owner = NpcDialogueLock.GetOwner(NpcRef.Of(__instance));
            if (owner < 0 || owner == net.LocalPlayerId)
                return true;
            // The host's one window is busy replaying a peer's board: no listening for a moment.
            if (net.Role == NetworkRole.Host && net.DialogOutcomeApplyHandlers.IsWorldDrainActive)
                return true;
            return !DialogMirror.TryStartSpectating(__instance);
        }
    }

    /// <summary>The talking player's window opened (the lock let it talk).</summary>
    [HarmonyPatch(typeof(DialogueWindow), "initiateDialogue")]
    public static class DialogMirrorOpenPatch
    {
        private static void Postfix(DialogueWindow __instance, bool __runOriginal)
        {
            if (!__runOriginal || __instance == null || DialogMirror.SpectatorActive
                || DialogHostApplyGuard.DialogueApplyActive)
                return;
            // A listener's close and the host's silent replay close leave "no save on exit" set,
            // and vanilla only clears it after a save: this player's own talk saves on exit again.
            __instance.dontSaveOnExit = false;
            DialogMirror.OwnerOpened(__instance);
        }
    }

    /// <summary>A listener's view opened: show the talking player's screen, not the NPC's welcome.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "checkIfWantToDisplayWelcome")]
    public static class DialogMirrorWelcomePatch
    {
        private static bool Prefix(DialogueWindow __instance)
        {
            // A talk the host turned down: it closes as soon as it is open, showing nothing.
            if (DialogMirror.DeniedNpc != null && __instance.npc == DialogMirror.DeniedNpc)
                return false;
            if (!DialogMirror.SpectatorActive)
                return true;
            DialogMirror.OnViewOpened(__instance);
            return false;
        }
    }

    /// <summary>A listener's view opens on the talking player's current portrait.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "setPortrait")]
    public static class DialogMirrorPortraitPatch
    {
        private static void Prefix(DialogueWindow __instance) => DialogMirror.ApplyPortraitOverride(__instance);
    }

    /// <summary>Main options built: the talking player reports them; a listener's are the mirror's.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "populateOptions")]
    public static class DialogMirrorOptionsPatch
    {
        private static bool Prefix(DialogueWindow __instance, out HashSet<int> __state)
        {
            __state = null;
            if (DialogMirror.SpectatorActive)
                return false;
            if (DialogMirror.OwnerCapturing(__instance))
                __state = DialogMirror.ChildIds(__instance.options);
            return true;
        }

        private static void Postfix(DialogueWindow __instance, HashSet<int> __state)
        {
            if (__state != null)
                DialogMirror.OwnerPanel(__instance, DialogMirrorMessage.KindOptions, __instance.options, __state);
        }
    }

    /// <summary>Show-item list built.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "populateShowItems")]
    public static class DialogMirrorItemsPatch
    {
        private static bool Prefix(DialogueWindow __instance, out HashSet<int> __state)
        {
            __state = null;
            if (DialogMirror.SpectatorActive)
                return false;
            if (DialogMirror.OwnerCapturing(__instance))
                __state = DialogMirror.ChildIds(__instance.showItems);
            return true;
        }

        private static void Postfix(DialogueWindow __instance, HashSet<int> __state)
        {
            if (__state != null)
                DialogMirror.OwnerPanel(__instance, DialogMirrorMessage.KindItems, __instance.showItems, __state);
        }
    }

    /// <summary>The trading screen: the listener is told, not shown it.</summary>
    [HarmonyPatch(typeof(DialogueWindow), nameof(DialogueWindow.openTrade))]
    public static class DialogMirrorTradePatch
    {
        private static bool Prefix() => !DialogMirror.SpectatorActive;

        private static void Postfix(DialogueWindow __instance, bool __runOriginal)
        {
            if (__runOriginal)
                DialogMirror.OwnerSimple(__instance, DialogMirrorMessage.KindTrade);
        }
    }

    /// <summary>A journal page shown mid-board closed: the board's text starts writing.</summary>
    [HarmonyPatch(typeof(DialogueWindow), nameof(DialogueWindow.hideJournalItem))]
    public static class DialogMirrorTextStartPatch
    {
        private static void Postfix(DialogueWindow __instance)
            => DialogMirror.OwnerSimple(__instance, DialogMirrorMessage.KindTextStart);
    }

    /// <summary>The talking player skipped the typing, or every line is written.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "speedUpBoard")]
    public static class DialogMirrorSpeedupPatch
    {
        private static void Postfix(DialogueWindow __instance) => DialogMirror.OwnerBoardDone(__instance);
    }

    [HarmonyPatch(typeof(DialogueWindow), nameof(DialogueWindow.finishedElement))]
    public static class DialogMirrorBoardDonePatch
    {
        private static void Postfix(DialogueWindow __instance) => DialogMirror.OwnerBoardDone(__instance);
    }

    /// <summary>A listener does not click through boards.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "onInstantClick")]
    public static class DialogMirrorInstantClickPatch
    {
        private static bool Prefix() => !DialogMirror.SpectatorActive;
    }

    /// <summary>A listener's controller "A" does nothing.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "Update")]
    public static class DialogMirrorUpdatePatch
    {
        private static bool Prefix() => !DialogMirror.SpectatorActive;
    }

    /// <summary>Esc: the listener leaves.</summary>
    [HarmonyPatch(typeof(DialogueWindow), nameof(DialogueWindow.escPress))]
    public static class DialogMirrorEscPatch
    {
        private static bool Prefix()
        {
            if (!DialogMirror.SpectatorActive)
                return true;
            DialogMirror.StopView(sendLeave: true);
            return false;
        }
    }

    /// <summary>The highlight in a listener's view is the talking player's.</summary>
    [HarmonyPatch(typeof(DialogueWindow), nameof(DialogueWindow.selectMenuOption))]
    public static class DialogMirrorSelectPatch
    {
        private static bool Prefix() => !DialogMirror.SpectatorActive || DialogMirror.Driving;
    }

    [HarmonyPatch(typeof(Button), nameof(Button.getClicked))]
    public static class DialogMirrorButtonClickPatch
    {
        private static bool Prefix(Button __instance)
            => !(DialogMirror.SpectatorActive && DialogMirror.IsViewButton(__instance));
    }

    /// <summary>
    /// Highlight changes: a listener's mouse does not move its view's highlight; the talking
    /// player's highlight is reported (index in the window's menu options).
    /// </summary>
    [HarmonyPatch(typeof(Button), nameof(Button.rollover))]
    public static class DialogMirrorRolloverPatch
    {
        private static bool Prefix(Button __instance, out bool __state)
        {
            __state = __instance != null && __instance.rolledOver;
            return !(DialogMirror.SpectatorActive && !DialogMirror.Driving && DialogMirror.IsViewButton(__instance));
        }

        private static void Postfix(Button __instance, bool __state, bool __runOriginal)
        {
            if (__runOriginal && !__state && __instance != null && __instance.rolledOver)
                DialogMirror.OwnerHighlightChanged(__instance);
        }
    }

    [HarmonyPatch(typeof(Button), nameof(Button.rollout))]
    public static class DialogMirrorRolloutPatch
    {
        private static bool Prefix(Button __instance, out bool __state)
        {
            __state = __instance != null && __instance.rolledOver;
            return !(DialogMirror.SpectatorActive && !DialogMirror.Driving && DialogMirror.IsViewButton(__instance));
        }

        private static void Postfix(Button __instance, bool __state, bool __runOriginal)
        {
            if (__runOriginal && __state && __instance != null && !__instance.rolledOver)
                DialogMirror.OwnerHighlightChanged(__instance);
        }
    }

    /// <summary>
    /// close(): a listener's view closes with nothing of the talk's (no exit dialogue, no
    /// onCloseDialogue triggers, no save, no cooking, no dream); the talking player's close ends
    /// the talk for its listeners.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), nameof(DialogueWindow.close))]
    public static class DialogMirrorClosePatch
    {
        internal struct CloseState
        {
            public NPC Npc;
            public bool Listener;
        }

        [HarmonyPriority(Priority.First)]
        private static void Prefix(DialogueWindow __instance, out CloseState __state)
        {
            __state = new CloseState
            {
                Npc = __instance != null ? __instance.npc : null,
                Listener = DialogMirror.SpectatorActive
            };
            if (__state.Listener && __instance != null)
                DialogMirror.PrepareClose(__instance);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(DialogueWindow __instance, CloseState __state, bool __runOriginal)
        {
            if (__state.Listener)
            {
                DialogMirror.AfterClose(__instance, sendLeave: true);
                return;
            }
            if (!__runOriginal || __state.Npc == null || __instance == null || __instance.npc != null)
                return;
            if (DialogHostApplyGuard.DialogueApplyActive)
                return;
            DialogMirror.OwnerClosed();
        }

        [HarmonyFinalizer]
        private static void Finalizer() => DialogMirror.Closing = false;
    }

    /// <summary>A listener closing never shows the NPC's exit dialogue.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "getExitDialogue")]
    public static class DialogMirrorExitDialoguePatch
    {
        private static bool Prefix(ref CharacterDialogue.Dialogue __result)
        {
            if (!DialogMirror.Closing)
                return true;
            __result = null;
            return false;
        }
    }

    /// <summary>A listener closing fires no onCloseDialogue (the talking player's close does).</summary>
    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(UnityEngine.GameObject), typeof(EventTrigger.Type), typeof(bool) })]
    public static class DialogMirrorCloseTriggerPatch
    {
        private static bool Prefix(EventTrigger.Type triggerType)
            => !(DialogMirror.Closing && triggerType == EventTrigger.Type.onCloseDialogue);
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo),
        new[] { typeof(UnityEngine.GameObject), typeof(EventTrigger.Type), typeof(string), typeof(bool) })]
    public static class DialogMirrorCloseTriggerValuePatch
    {
        private static bool Prefix(EventTrigger.Type triggerType)
            => !(DialogMirror.Closing && triggerType == EventTrigger.Type.onCloseDialogue);
    }
}
