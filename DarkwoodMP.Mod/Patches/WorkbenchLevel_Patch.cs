using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Workbench upgrade sync (v0.4 audit find). The workbench's recipe tier is
/// NOT stored on the workbench - Workbench.setRecipes reads
/// Controller.workbenchLevel, and the only gameplay writer is
/// CraftingRecipes.doCraft (crafting the upgrade recipe bumps it, verified
/// via field-writer scan). Without sync, one player's upgrade never reaches
/// the other's recipe list. Prefix captures the level, postfix broadcasts it
/// when the craft changed it; the receiver takes max(local, remote).
/// </summary>
public sealed class WorkbenchLevel_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(WorkbenchLevel_Patch).GetMethod(nameof(CraftPrefix), statics)!),
            postfix: new HarmonyMethod(typeof(WorkbenchLevel_Patch).GetMethod(nameof(CraftPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("CraftingRecipes", "doCraft");
    }

    public static void CraftPrefix(ref int __state)
    {
        var controller = UnityEngine.Object.FindObjectOfType<Controller>();
        __state = controller != null ? controller.workbenchLevel : -1;
    }

    public static void CraftPostfix(int __state)
    {
        try
        {
            if (RemoteApply.Active || __state < 0) return;
            var controller = UnityEngine.Object.FindObjectOfType<Controller>();
            if (controller == null || controller.workbenchLevel == __state) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            network.SendReliable(new WorkbenchLevelPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                Level = controller.workbenchLevel
            });
            ModLogger.Msg($"[WorkbenchLevel] Upgraded to {controller.workbenchLevel}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[WorkbenchLevel_Patch] {ex.Message}");
        }
    }
}
