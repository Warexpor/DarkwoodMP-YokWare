using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Tracks which container inventory the local player has open.
/// Verified game API: Player.setOpenedItemInventory(Inventory inv) is called
/// when a chest/wardrobe/corpse is opened, Player.closeOpenedItemInventory()
/// when it closes. On close, ContainerSync broadcasts the remaining content.
///
/// Inventory.hide is ALSO hooked: it is the one chokepoint every close path
/// funnels through (walk-away auto-close, TAB, window close) and the only
/// place the game nulls Player.openedItemInventory - closeOpenedItemInventory
/// is just one optional route into it (verified IL, v0.7 playtest: corpses
/// closed by walking away never released the interaction lock and never
/// flushed their content).
/// </summary>
public sealed class Container_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        switch (target.Name)
        {
            case "setOpenedItemInventory":
                baseHarmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(Container_Patch).GetMethod(nameof(OpenPostfix), statics)!));
                break;
            case "hide":
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Container_Patch).GetMethod(nameof(HidePrefix), statics)!),
                    postfix: new HarmonyMethod(typeof(Container_Patch).GetMethod(nameof(HidePostfix), statics)!));
                break;
            default: // closeOpenedItemInventory
                baseHarmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Container_Patch).GetMethod(nameof(ClosePrefix), statics)!)); // before the reference is cleared
                break;
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "setOpenedItemInventory");
        yield return ("Player", "closeOpenedItemInventory");
        yield return ("Inventory", "hide");
    }

    public static void OpenPostfix(object inv)
    {
        try
        {
            var sync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ContainerSync>();
            sync?.OnContainerOpened(inv as Component);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Container_Patch] {ex.Message}");
        }
    }

    public static void ClosePrefix()
    {
        try
        {
            var sync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ContainerSync>();
            sync?.OnContainerClosing();
            // Closing an item inventory (container or workbench) releases its
            // lock - kind-guarded so an NPC/drag hold is never dropped here.
            var lk = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<InteractionLock>();
            lk?.ReleaseIfKind(InteractionLock.Kind.Container);
            lk?.ReleaseIfKind(InteractionLock.Kind.Workbench);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Container_Patch] {ex.Message}");
        }
    }

    // Was this hide() call actually closing one of the player's open item
    // inventories? Captured in the prefix - hide() no-ops when !open and
    // nulls the Player references before the postfix could compare them.
    public static void HidePrefix(Inventory __instance, ref bool __state)
    {
        __state = false;
        try
        {
            if (__instance == null || !__instance.open) return;
            var player = Player.Instance;
            if (player == null) return;
            __state = player.openedItemInventory == __instance
                   || player.openedItemInventory2 == __instance;
        }
        catch { /* stay false */ }
    }

    public static void HidePostfix(bool __state)
    {
        try
        {
            if (!__state) return;

            // Same treatment as a normal close; both calls are idempotent, so
            // the closeOpenedItemInventory route running first costs nothing.
            var sync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ContainerSync>();
            sync?.OnContainerClosing();

            // Only release once the MAIN container reference is gone - the
            // workbench storage tray (openedItemInventory2) hides first while
            // the bench itself is still open.
            var player = Player.Instance;
            if (player != null && player.openedItemInventory != null) return;
            var lk = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<InteractionLock>();
            lk?.ReleaseIfKind(InteractionLock.Kind.Container);
            lk?.ReleaseIfKind(InteractionLock.Kind.Workbench);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Container_Patch] {ex.Message}");
        }
    }
}
