using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Syncs door and window barricades. Verified game API:
///   Door.barricade(bool byPlayer) / Door.destroyBarricade(bool silent)
///   Window.barricade(int destHealth, bool byPlayer) / Window.destroyBarricade(bool silent)
/// Building broadcasts the resulting barricade health; destruction (by player
/// or by enemies breaking through) broadcasts the removal.
/// </summary>
public sealed class Barricade_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var isDoor = target.DeclaringType!.Name == "Door";
        string postfixName;
        if (target.Name == "barricade")
            postfixName = isDoor ? nameof(DoorBarricadePostfix) : nameof(WindowBarricadePostfix);
        else
            postfixName = isDoor ? nameof(DoorDestroyPostfix) : nameof(WindowDestroyPostfix);

        baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(Barricade_Patch).GetMethod(postfixName, statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Door", "barricade");
        yield return ("Door", "destroyBarricade");
        yield return ("Window", "barricade");
        yield return ("Window", "destroyBarricade");
    }

    public static void DoorBarricadePostfix(object __instance)
    {
        if (__instance is Door door)
            Send(BarricadeStatePacket.KindDoor, door, barricaded: true, door.barricadeHealth);
    }

    public static void DoorDestroyPostfix(object __instance)
    {
        if (__instance is Component door)
            Send(BarricadeStatePacket.KindDoor, door, barricaded: false, 0);
    }

    public static void WindowBarricadePostfix(object __instance)
    {
        if (__instance is Window window)
            Send(BarricadeStatePacket.KindWindow, window, barricaded: true, window.barricadeHealth);
    }

    public static void WindowDestroyPostfix(object __instance)
    {
        if (__instance is Component window)
            Send(BarricadeStatePacket.KindWindow, window, barricaded: false, 0);
    }

    private static void Send(byte kind, Component target, bool barricaded, int health)
    {
        try
        {
            if (RemoteApply.Active) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var id = GameIds.ForComponent(target);
            ModLogger.Msg($"[Barricade_Patch] kind={kind} barricaded={barricaded} '{id}' health={health}");
            network.SendReliable(new BarricadeStatePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                Kind = kind,
                ObjectId = id,
                Barricaded = barricaded,
                Health = health
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Barricade_Patch] {ex.Message}");
        }
    }
}
