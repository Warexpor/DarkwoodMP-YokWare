using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Tracks the workbench's inventories for container sync. Verified game API:
/// Workbench.open()/close(), fields `normalInventory` (item storage tray) and
/// `workbenchInventory` (upgrade slots). Only one of them goes through
/// Player.setOpenedItemInventory, so both are registered here explicitly.
/// </summary>
public sealed class Workbench_Patch : IPatch
{
    private static FieldInfo _normalInventoryField;
    private static FieldInfo _workbenchInventoryField;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var name = target.Name == "open" ? nameof(OpenPostfix) : nameof(ClosePostfix);
        var patch = new HarmonyMethod(typeof(Workbench_Patch).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: patch);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Workbench", "open");
        yield return ("Workbench", "close");
    }

    public static void OpenPostfix(object __instance, bool __runOriginal)
    {
        try
        {
            // Harmony runs postfixes even when a prefix skipped the original
            // (e.g. InteractionLock blocking a second player). Registering the
            // never-populated workbench inventories here used to flush EMPTY
            // stock to the player actually using the bench, wiping their
            // crafting options mid-use.
            if (!__runOriginal) return;

            var sync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ContainerSync>();
            if (sync == null) return;

            var type = __instance.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _normalInventoryField ??= type.GetField("normalInventory", flags);
            _workbenchInventoryField ??= type.GetField("workbenchInventory", flags);

            if (_normalInventoryField?.GetValue(__instance) is Component storage)
                sync.OnContainerOpened(storage);
            if (_workbenchInventoryField?.GetValue(__instance) is Component upgrades)
                sync.OnContainerOpened(upgrades);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Workbench_Patch] {ex.Message}");
        }
    }

    public static void ClosePostfix()
    {
        try
        {
            DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ContainerSync>()?.OnContainerClosing();
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Workbench_Patch] {ex.Message}");
        }
    }
}
