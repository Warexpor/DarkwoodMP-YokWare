using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Patches Darkwood's Item drag lifecycle so moved furniture syncs to other players.
/// Verified game API: Item.startDragging(), Item.stopDragging(bool force),
/// fields `draggable`, `beingDragged`.
/// While dragging, MovableSync broadcasts the item position at 10Hz.
/// </summary>
public sealed class ItemDrag_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var postfixName = target.Name == "startDragging" ? nameof(StartPostfix) : nameof(StopPostfix);
        var postfix = new HarmonyMethod(typeof(ItemDrag_Patch).GetMethod(postfixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Item", "startDragging");
        yield return ("Item", "stopDragging");
    }

    public static void StartPostfix(object __instance, bool __runOriginal)
    {
        try
        {
            // Skipped original (interaction lock blocked the grab) - not dragging.
            if (!__runOriginal) return;
            // startDragging can fail silently ("too far") - only track real drags.
            if (__instance is Item item && !item.beingDragged) return;

            var sync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<MovableSync>();
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (sync == null || network == null || !network.IsConnected) return;
            sync.OnLocalDragStart(__instance as Component);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemDrag_Patch] {ex.Message}");
        }
    }

    public static void StopPostfix(object __instance, bool __runOriginal)
    {
        try
        {
            if (!__runOriginal) return;
            var sync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<MovableSync>();
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (sync == null || network == null || !network.IsConnected) return;
            sync.OnLocalDragStop(__instance as Component);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemDrag_Patch] {ex.Message}");
        }
    }
}
