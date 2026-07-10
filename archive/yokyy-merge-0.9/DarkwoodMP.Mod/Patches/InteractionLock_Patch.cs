using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Exclusive-interaction gate (v0.7). Claims an <see cref="InteractionLock"/> at
/// the true open entry of each shared interactable and blocks the open when
/// another player already holds it. These entry points all run BEFORE the game
/// sets performingAction / halts the player (verified in IL), so a prefix that
/// returns false aborts cleanly with no soft-lock.
///
/// Block-in-prefix, CLAIM-IN-POSTFIX once the open verifiably took effect - all
/// of these entries can early-return (disarm/shadow armour for containers, the
/// busy-player refusal inside initiateOpenCloseInventory for the workbench, the
/// "too far" check in startDragging), and a claim without a real open would
/// strand the lock:
///
/// - Item.openInventory : containers, corpses, lootables (confirm:
///   Player.openedItemInventory set).
/// - Item.startDragging : movable furniture (confirm: beingDragged). Keyed by
///   MovableSync's session-stable registry id, which doesn't drift while the
///   item moves and is claim-mapped to the same value on both machines.
/// - Workbench.open : crafting station (confirm: Player.openedItemInventory).
/// - NPC.talkTo : NPC dialogue incl. traders/ovens - claimed in the prefix
///   because talkTo cannot fail past the guards we mirror; a stranded claim is
///   auto-released by the lock's validity check.
///
/// Releases ride existing hooks: Container_Patch (Player.closeOpenedItemInventory,
/// covers containers AND the workbench), Dialogue_Patch (DialogueWindow.close),
/// and the stopDragging postfix here.
/// </summary>
public sealed class InteractionLock_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        HarmonyMethod Method(string name) =>
            new(typeof(InteractionLock_Patch).GetMethod(name, statics)!);

        switch (target.Name)
        {
            case "openInventory":
                baseHarmony.Patch(target,
                    prefix: Method(nameof(ContainerPrefix)),
                    postfix: Method(nameof(ContainerPostfix)));
                break;
            case "startDragging":
                baseHarmony.Patch(target,
                    prefix: Method(nameof(DragStartPrefix)),
                    postfix: Method(nameof(DragStartPostfix)));
                break;
            case "stopDragging":
                baseHarmony.Patch(target,
                    postfix: Method(nameof(DragStopPostfix)));
                break;
            case "talkTo":
                baseHarmony.Patch(target,
                    prefix: Method(nameof(NpcPrefix)));
                break;
            default: // Workbench.open
                baseHarmony.Patch(target,
                    prefix: Method(nameof(WorkbenchPrefix)),
                    postfix: Method(nameof(WorkbenchPostfix)));
                break;
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Item", "openInventory");
        yield return ("Item", "startDragging");
        yield return ("Item", "stopDragging");
        yield return ("NPC", "talkTo");
        yield return ("Workbench", "open");
    }

    private static InteractionLock Lock()
        => DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<InteractionLock>();

    // ---- Containers / lootables / corpses ----------------------------------
    public static bool ContainerPrefix(Item __instance, ref bool __state)
    {
        __state = false;
        try
        {
            if (RemoteApply.Active) return true;
            var lk = Lock();
            if (lk == null) return true;

            if (lk.IsBlocked(__instance, InteractionLock.Kind.Container))
                return false; // occupied by another player - abort the open

            __state = true; // allowed; claim after we confirm it opened
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock_Patch] ContainerPrefix: {ex.Message}");
        }
        return true;
    }

    public static void ContainerPostfix(Item __instance, bool __state)
    {
        try
        {
            if (!__state || RemoteApply.Active) return;
            // Confirm the open actually happened (not a disarm/gated early return).
            var player = Player.Instance;
            if (player == null || player.openedItemInventory == null) return;
            Lock()?.TryBegin(__instance, InteractionLock.Kind.Container);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock_Patch] ContainerPostfix: {ex.Message}");
        }
    }

    // ---- Draggable furniture ------------------------------------------------
    public static bool DragStartPrefix(Item __instance, ref bool __state)
    {
        __state = false;
        try
        {
            if (RemoteApply.Active) return true;
            var lk = Lock();
            if (lk == null) return true;

            var id = DarkwoodMP.DependencyInjection.ServiceLocator
                .Resolve<MovableSync>()?.SharedIdFor(__instance);
            if (id == null) return true; // unregistered item - fail open

            if (lk.IsBlocked(__instance, InteractionLock.Kind.Drag, id))
                return false; // partner is dragging it

            __state = true;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock_Patch] DragStartPrefix: {ex.Message}");
        }
        return true;
    }

    public static void DragStartPostfix(Item __instance, bool __state)
    {
        try
        {
            if (!__state || RemoteApply.Active) return;
            // startDragging fails silently when too far - only claim real drags.
            if (__instance == null || !__instance.beingDragged) return;

            var id = DarkwoodMP.DependencyInjection.ServiceLocator
                .Resolve<MovableSync>()?.SharedIdFor(__instance);
            if (id == null) return;
            Lock()?.TryBegin(__instance, InteractionLock.Kind.Drag, id);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock_Patch] DragStartPostfix: {ex.Message}");
        }
    }

    public static void DragStopPostfix()
    {
        try
        {
            Lock()?.ReleaseDrag();
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock_Patch] DragStopPostfix: {ex.Message}");
        }
    }

    // ---- NPC dialogue (incl. traders, ovens, story objects) -----------------
    public static bool NpcPrefix(NPC __instance)
    {
        try
        {
            if (RemoteApply.Active) return true;
            // Mirror talkTo's own guard: it only opens dialogue when both hold.
            if (__instance.characterDialogue == null || !__instance.wantsToTalk) return true;

            var lk = Lock();
            if (lk == null) return true;
            return lk.TryBegin(__instance, InteractionLock.Kind.Npc);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock_Patch] NpcPrefix: {ex.Message}");
            return true;
        }
    }

    // ---- Workbench ---------------------------------------------------------
    public static bool WorkbenchPrefix(Workbench __instance, ref bool __state)
    {
        __state = false;
        try
        {
            if (RemoteApply.Active) return true;
            var lk = Lock();
            if (lk == null) return true;

            if (lk.IsBlocked(__instance, InteractionLock.Kind.Workbench))
                return false;

            __state = true; // claim once initiateOpenCloseInventory accepted
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock_Patch] WorkbenchPrefix: {ex.Message}");
        }
        return true;
    }

    public static void WorkbenchPostfix(Workbench __instance, bool __state)
    {
        try
        {
            if (!__state || RemoteApply.Active) return;
            // initiateOpenCloseInventory refuses while the player is busy
            // (running/aiming/...) - only claim when the bench really opened.
            var player = Player.Instance;
            if (player == null || player.openedItemInventory == null) return;
            Lock()?.TryBegin(__instance, InteractionLock.Kind.Workbench);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[InteractionLock_Patch] WorkbenchPostfix: {ex.Message}");
        }
    }
}
